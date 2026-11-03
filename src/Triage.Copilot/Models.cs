using Triage.Engine;

namespace Triage.Copilot;

/// <summary>Every suggestion carries a confidence between 0 and 1 and one sentence of evidence. It never carries the model's private reasoning.</summary>
public sealed record Classification(string Category, Priority Priority, string? SuggestedSkill, double Confidence, string Rationale);

public sealed record ThreadSummary(string Summary, double Confidence, string Rationale);

public sealed record ReplyDraft(string Text, double Confidence, string Rationale);

public sealed record AssigneeSuggestion(Guid AgentId, string AgentName, double Confidence, string Rationale);

/// <summary>An agent as the assistant sees one: enough to choose between people, nothing more.</summary>
public sealed record AgentOption(Guid Id, string Name, IReadOnlyList<string> Skills, bool Available, int Open, int Capacity)
{
    public bool HasRoom => Available && Open < Capacity;

    public static IReadOnlyList<AgentOption> From(Desk desk)
    {
        var loads = desk.Stats().Agents.ToDictionary(l => l.AgentId);
        return desk.Agents()
            .Select(a => new AgentOption(a.Id, a.Name, a.Skills, a.Available, loads.TryGetValue(a.Id, out var l) ? l.Open : 0, a.Capacity))
            .ToList();
    }
}

/// <summary>Either an answer, or the reason there is none. The assistant never throws because a model misbehaved.</summary>
public sealed record Outcome<T>(T? Value, string? Reason) where T : class
{
    public bool Ok => Value is not null;

    public static Outcome<T> Of(T value) => new(value, null);

    public static Outcome<T> None(string reason) => new(null, reason);
}

public interface ITriageAssistant
{
    Task<Outcome<Classification>> ClassifyTicketAsync(Ticket ticket, CancellationToken cancellationToken = default);

    Task<Outcome<ThreadSummary>> SummarizeThreadAsync(Ticket ticket, CancellationToken cancellationToken = default);

    Task<Outcome<ReplyDraft>> DraftReplyAsync(Ticket ticket, CancellationToken cancellationToken = default);

    Task<Outcome<AssigneeSuggestion>> SuggestAssigneeAsync(Ticket ticket, IReadOnlyList<AgentOption> agents, CancellationToken cancellationToken = default);
}

public sealed record CopilotOptions
{
    /// <summary>The most characters of ticket text that are ever sent to a model. Older messages are dropped first.</summary>
    public int MaxInputChars { get; init; } = 6000;

    /// <summary>How long one model call may take before the suggestion is abandoned.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
