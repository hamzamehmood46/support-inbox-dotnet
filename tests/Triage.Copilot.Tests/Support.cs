using Microsoft.Extensions.AI;
using Triage.Copilot;
using Triage.Engine;

namespace Triage.Copilot.Tests;

/// <summary>A desk, a fake model and a copilot, on a clock the test controls (Monday 5 October 2026, 10:00 UTC).</summary>
public sealed class Lab
{
    public static readonly DateTimeOffset Start = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    public SimulatedClock Clock { get; } = new(Start);
    public Desk Desk { get; }
    public Agent Ana { get; }
    public Agent Cleo { get; }
    public FakeChatClient Chat { get; }
    public TriageAssistant Assistant { get; }
    public TriageCopilot Copilot { get; }

    public Lab(CopilotOptions? options = null, IChatClient? chat = null)
    {
        Desk = new Desk(Clock);
        Ana = Desk.AddAgent("Ana").Value!;
        Cleo = Desk.AddAgent("Cleo", ["billing"], 4).Value!;
        Chat = (chat as FakeChatClient) ?? new FakeChatClient();
        Assistant = new TriageAssistant(chat ?? Chat, options);
        Copilot = new TriageCopilot(Desk, Assistant, Clock);
    }

    public Ticket New(string subject = "Report export is slow", string body = "It takes several minutes to export.", string requester = "cara@example.com")
    {
        var created = Desk.Create(subject, requester, body);
        Assert.True(created.Ok, created.Message);
        return created.Value!;
    }

    public Ticket Get(Ticket t) => Desk.Ticket(t.Id)!;

    public static string Good(string category = "billing", string priority = "Normal", string? skill = "billing", double confidence = 0.9) =>
        $$"""{"category":"{{category}}","priority":"{{priority}}","suggestedSkill":{{(skill is null ? "null" : $"\"{skill}\"")}},"confidence":{{confidence}},"rationale":"Because of the wording."}""";

    /// <summary>The ticket block the model was given, with the surrounding tags removed.</summary>
    public static string TicketBlock(FakeChatClient chat, int request = 0)
    {
        var text = chat.Requests[request].First(m => m.Role == ChatRole.User).Text;
        var a = text.IndexOf("<ticket>", StringComparison.Ordinal) + "<ticket>".Length;
        var b = text.IndexOf("</ticket>", StringComparison.Ordinal);
        return text[a..b].Trim('\n');
    }
}

public sealed class ThrowingChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new HttpRequestException("connection refused");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

public sealed class SlowChatClient : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A pretend HTTP server that records what it was sent and replies with a fixed body.</summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly string _reply;

    public StubHandler(string reply) => _reply = reply;

    public HttpRequestMessage? Request { get; private set; }
    public string Body { get; private set; } = "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request = request;
        Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(_reply, System.Text.Encoding.UTF8, "application/json") };
    }
}
