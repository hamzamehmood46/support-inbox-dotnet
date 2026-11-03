using System.Text.Json;
using Triage.Copilot;

namespace Triage.Copilot.Tests;

public class ProviderTests
{
    [Fact]
    public void The_default_provider_is_the_fake_one()
    {
        var settings = CopilotSettings.FromEnvironment(_ => null);

        Assert.Equal("fake", settings.Provider);
        Assert.IsType<FakeChatClient>(CopilotClients.Create(settings));
    }

    [Fact]
    public void Settings_come_from_environment_variables_and_never_print_the_key()
    {
        var env = new Dictionary<string, string>
        {
            ["TRIAGE_AI_PROVIDER"] = "OpenAI-Compatible",
            ["TRIAGE_AI_ENDPOINT"] = "https://llm.example.test/v1",
            ["TRIAGE_AI_MODEL"] = "some-model",
            ["TRIAGE_AI_API_KEY"] = "sk-super-secret-value",
        };

        var settings = CopilotSettings.FromEnvironment(k => env.GetValueOrDefault(k));

        Assert.Equal("openai-compatible", settings.Provider);
        Assert.Equal("sk-super-secret-value", settings.ApiKey);
        Assert.DoesNotContain("sk-super-secret-value", settings.ToString());
        Assert.Contains("ApiKey=(set)", settings.ToString());
    }

    [Fact]
    public void Bad_provider_settings_are_rejected_with_a_clear_message()
    {
        Assert.Throws<ArgumentException>(() => CopilotClients.Create(new CopilotSettings { Provider = "carrier-pigeon" }));
        Assert.Contains("TRIAGE_AI_MODEL", Assert.Throws<ArgumentException>(() => CopilotClients.Create(new CopilotSettings { Provider = "ollama" })).Message);
        Assert.Contains("TRIAGE_AI_ENDPOINT", Assert.Throws<ArgumentException>(() => CopilotClients.Create(new CopilotSettings { Provider = "openai-compatible", Model = "m" })).Message);
        Assert.IsType<OllamaChatClient>(CopilotClients.Create(new CopilotSettings { Provider = "ollama", Model = "llama3.2" }, new HttpClient(new StubHandler("{}"))));
    }

    [Fact]
    public async Task Ollama_gets_the_schema_and_returns_the_message_content()
    {
        var reply = JsonSerializer.Serialize(new { message = new { role = "assistant", content = Lab.Good() } });
        var handler = new StubHandler(reply);
        var client = new OllamaChatClient(new HttpClient(handler), "llama3.2", "http://localhost:11434");
        var assistant = new TriageAssistant(client);
        var lab = new Lab();

        var o = await assistant.ClassifyTicketAsync(lab.Get(lab.New("Charged twice", "refund please")));

        Assert.True(o.Ok, o.Reason);
        Assert.Equal("http://localhost:11434/api/chat", handler.Request!.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("llama3.2", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("format").ValueKind);
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task A_hosted_provider_gets_a_bearer_token_and_the_answer_is_read_from_choices()
    {
        var reply = JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = Lab.Good() } } } });
        var handler = new StubHandler(reply);
        var client = new OpenAiCompatibleChatClient(new HttpClient(handler), "https://llm.example.test/v1", "some-model", "test-key-123");
        var assistant = new TriageAssistant(client);
        var lab = new Lab();

        var o = await assistant.ClassifyTicketAsync(lab.Get(lab.New("Charged twice", "refund please")));

        Assert.True(o.Ok, o.Reason);
        Assert.Equal("https://llm.example.test/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("test-key-123", handler.Request.Headers.Authorization.Parameter);
        Assert.DoesNotContain("test-key-123", handler.Body);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Without_a_key_no_authorization_header_is_sent()
    {
        var handler = new StubHandler(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "{}" } } } }));
        var client = new OpenAiCompatibleChatClient(new HttpClient(handler), "http://localhost:1234/v1/", "m", null);

        await client.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hi")]);

        Assert.Null(handler.Request!.Headers.Authorization);
    }
}
