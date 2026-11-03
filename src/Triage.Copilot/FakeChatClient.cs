using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Triage.Copilot;

/// <summary>
/// A deterministic stand-in for a model: the same ticket always gets the same answer, using plain keyword rules.
/// It is the default provider, it needs no network or key, and it keeps a copy of every request so tests can see what would have been sent.
/// Queue answers with <see cref="Script"/> to simulate a model that misbehaves.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly object _gate = new();
    private readonly Queue<string> _script = new();
    private readonly List<IReadOnlyList<ChatMessage>> _requests = [];

    public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public FakeChatClient Script(params string[] answers)
    {
        lock (_gate)
        {
            foreach (var a in answers)
            {
                _script.Enqueue(a);
            }
        }

        return this;
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = messages.ToList();
        string text;
        lock (_gate)
        {
            _requests.Add(list);
            text = _script.Count > 0 ? _script.Dequeue() : Answer(list);
        }

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private static string Answer(List<ChatMessage> messages)
    {
        var task = Regex.Match(messages[0].Text, @"TASK: (\w+)").Groups[1].Value;
        var input = messages.First(m => m.Role == ChatRole.User).Text;
        var ticket = Between(input, "<ticket>", "</ticket>");
        return task switch
        {
            "classify" => Json(Classify(ticket)),
            "summarize" => Json(Summarize(ticket)),
            "reply" => Json(Reply(ticket)),
            "assign" => Json(Assign(ticket, Between(input, "<agents>", "</agents>"))),
            _ => "{}",
        };
    }

    private static JsonObject Classify(string ticket)
    {
        var (category, priority, skill, confidence, why) = Triage(ticket);
        return new JsonObject { ["category"] = category, ["priority"] = priority, ["suggestedSkill"] = skill, ["confidence"] = confidence, ["rationale"] = why };
    }

    private static (string Category, string Priority, string? Skill, double Confidence, string Why) Triage(string ticket)
    {
        var s = ticket.ToLowerInvariant();
        if (Has(s, "outage", "is down", "cannot access", "can't access", "crash"))
        {
            return ("incident", "Urgent", null, 0.92, "The ticket describes an outage or lost access.");
        }

        if (Has(s, "refund", "invoice", "charge", "billing", "payment"))
        {
            return ("billing", "Normal", "billing", 0.9, "The ticket mentions a billing term.");
        }

        if (Has(s, "password", "login", "log in", "locked out", "2fa"))
        {
            return ("account", Has(s, "locked out") ? "High" : "Normal", "account", 0.88, "The ticket is about signing in to an account.");
        }

        if (Has(s, "vpn", "wifi", "network", "disconnect"))
        {
            return ("network", "Normal", "network", 0.86, "The ticket mentions a network or connection problem.");
        }

        if (Has(s, "feature request", "suggestion", "would be nice"))
        {
            return ("feedback", "Low", null, 0.8, "The ticket is a suggestion rather than a fault.");
        }

        return ("general", "Normal", null, 0.3, "No clear keyword, so this is a weak guess.");
    }

    private static JsonObject Summarize(string ticket)
    {
        var lines = ticket.Split('\n').Where(l => Regex.IsMatch(l, @"^\[(Customer|Agent|Note)\] ")).ToList();
        var first = lines.FirstOrDefault(l => l.StartsWith("[Customer] ", StringComparison.Ordinal)) ?? lines.FirstOrDefault() ?? "";
        var last = lines.LastOrDefault() ?? "";
        var summary = $"{lines.Count} message(s) so far. The customer wrote: {Clip(first[(first.IndexOf(' ') + 1)..], 140)} Latest: {Clip(last, 100)}";
        return new JsonObject { ["summary"] = Clip(summary, 800), ["confidence"] = 0.7, ["rationale"] = "Built from the first customer message and the latest message." };
    }

    private static JsonObject Reply(string ticket)
    {
        var subject = Regex.Match(ticket, @"Subject: (.*)").Groups[1].Value.Trim();
        var (category, _, _, confidence, _) = Triage(ticket);
        var line = category switch
        {
            "billing" => "I am looking at the charge on your account now and will confirm the next step shortly.",
            "account" => "I am checking your account access and will update you in this thread shortly.",
            "incident" => "We are treating this as urgent and are investigating now. I will update you as soon as I know more.",
            "network" => "I am reviewing the connection details and will come back with next steps shortly.",
            _ => "I am looking into this and will follow up shortly.",
        };
        return new JsonObject
        {
            ["reply"] = $"Hi,\n\nThanks for getting in touch about \"{subject}\". {line}\n\nBest regards,\nSupport",
            ["confidence"] = Math.Round(confidence - 0.1, 2),
            ["rationale"] = $"A template for a {category} ticket; it makes no promises.",
        };
    }

    private static JsonObject Assign(string ticket, string agentLines)
    {
        var agents = agentLines.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Select(e => (Id: e.GetProperty("id").GetString()!, Name: e.GetProperty("name").GetString()!, Skills: e.GetProperty("skills").EnumerateArray().Select(x => x.GetString()!).ToList(), Open: e.GetProperty("open").GetInt32(), Capacity: e.GetProperty("capacity").GetInt32()))
            .ToList();
        var skill = Triage(ticket).Skill;
        var matching = skill is null ? [] : agents.Where(a => a.Skills.Contains(skill, StringComparer.OrdinalIgnoreCase)).ToList();
        var pool = matching.Count > 0 ? matching : agents;
        var pick = pool.OrderBy(a => (double)a.Open / Math.Max(1, a.Capacity)).ThenBy(a => a.Name, StringComparer.Ordinal).First();
        return new JsonObject
        {
            ["agentId"] = pick.Id,
            ["confidence"] = matching.Count > 0 ? 0.85 : 0.5,
            ["rationale"] = matching.Count > 0 ? $"{pick.Name} has the {skill} skill and the lightest load among those who do." : $"No skill match, so {pick.Name} has the lightest load.",
        };
    }

    private static bool Has(string text, params string[] words) => words.Any(text.Contains);

    private static string Between(string text, string open, string close)
    {
        var a = text.IndexOf(open, StringComparison.Ordinal);
        var b = text.LastIndexOf(close, StringComparison.Ordinal);
        return a < 0 || b <= a ? "" : text[(a + open.Length)..b].Trim();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "â€¦";

    private static string Json(JsonObject value) => value.ToJsonString();
}
