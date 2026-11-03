using Triage.Engine;

namespace Triage.Copilot;

public enum SuggestionKind
{
    Classification,
    Summary,
    Reply,
    Assignee,
}

public enum SuggestionStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>What the copilot proposes. It changes nothing on its own: a person has to approve it.</summary>
public sealed class Suggestion
{
    public Guid Id { get; init; }
    public Guid TicketId { get; init; }
    public SuggestionKind Kind { get; init; }
    public SuggestionStatus Status { get; internal set; }

    /// <summary>What a person reads, and for replies and summaries may edit before approving.</summary>
    public string Text { get; internal set; } = "";

    public double Confidence { get; init; }
    public string Rationale { get; init; } = "";
    public Priority? Priority { get; init; }
    public string? Category { get; init; }
    public string? Skill { get; init; }
    public Guid? AgentId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DecidedAt { get; internal set; }
    public Guid? DecidedBy { get; internal set; }

    internal Suggestion Snapshot() => (Suggestion)MemberwiseClone();
}

/// <summary>
/// Joins the desk, an assistant and a list of suggestions. Asking for a suggestion never touches a ticket;
/// only <see cref="Approve"/> does, and only as the agent who approved it.
/// </summary>
public sealed class TriageCopilot
{
    private readonly Desk _desk;
    private readonly ITriageAssistant _assistant;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly List<Suggestion> _all = [];

    public TriageCopilot(Desk desk, ITriageAssistant assistant, TimeProvider clock)
    {
        _desk = desk;
        _assistant = assistant;
        _clock = clock;
    }

    public Task<Created<Suggestion>> SuggestClassificationAsync(Guid ticketId, CancellationToken cancellationToken = default) =>
        SuggestAsync(ticketId, SuggestionKind.Classification, async (t, ct) =>
        {
            var o = await _assistant.ClassifyTicketAsync(t, ct).ConfigureAwait(false);
            if (o.Value is not { } c)
            {
                return (null, o.Reason);
            }

            var tags = string.Join(", ", new[] { c.Category, c.SuggestedSkill }.Where(x => x is not null).Distinct());
            return (new Suggestion { Text = $"Priority {c.Priority}; tags: {tags}", Confidence = c.Confidence, Rationale = c.Rationale, Priority = c.Priority, Category = c.Category, Skill = c.SuggestedSkill }, null);
        }, cancellationToken);

    public Task<Created<Suggestion>> SuggestSummaryAsync(Guid ticketId, CancellationToken cancellationToken = default) =>
        SuggestAsync(ticketId, SuggestionKind.Summary, async (t, ct) =>
        {
            var o = await _assistant.SummarizeThreadAsync(t, ct).ConfigureAwait(false);
            return o.Value is { } s ? (new Suggestion { Text = s.Summary, Confidence = s.Confidence, Rationale = s.Rationale }, null) : (null, o.Reason);
        }, cancellationToken);

    public Task<Created<Suggestion>> SuggestReplyAsync(Guid ticketId, CancellationToken cancellationToken = default) =>
        SuggestAsync(ticketId, SuggestionKind.Reply, async (t, ct) =>
        {
            var o = await _assistant.DraftReplyAsync(t, ct).ConfigureAwait(false);
            return o.Value is { } d ? (new Suggestion { Text = d.Text, Confidence = d.Confidence, Rationale = d.Rationale }, null) : (null, o.Reason);
        }, cancellationToken);

    public Task<Created<Suggestion>> SuggestAssigneeAsync(Guid ticketId, CancellationToken cancellationToken = default) =>
        SuggestAsync(ticketId, SuggestionKind.Assignee, async (t, ct) =>
        {
            var o = await _assistant.SuggestAssigneeAsync(t, AgentOption.From(_desk), ct).ConfigureAwait(false);
            return o.Value is { } a ? (new Suggestion { Text = $"Assign to {a.AgentName}", Confidence = a.Confidence, Rationale = a.Rationale, AgentId = a.AgentId }, null) : (null, o.Reason);
        }, cancellationToken);

    public IReadOnlyList<Suggestion> Suggestions(Guid? ticketId = null, SuggestionStatus? status = null)
    {
        lock (_gate)
        {
            return _all
                .Where(s => (ticketId is null || s.TicketId == ticketId) && (status is null || s.Status == status))
                .Select(s => s.Snapshot())
                .ToList();
        }
    }

    public Suggestion? Suggestion(Guid id)
    {
        lock (_gate)
        {
            return _all.FirstOrDefault(s => s.Id == id)?.Snapshot();
        }
    }

    /// <summary>Applies a pending suggestion as <paramref name="agentId"/>. Replies and summaries may be edited first. If the change cannot be applied the suggestion stays pending.</summary>
    public Result Approve(Guid suggestionId, Guid agentId, string? editedText = null)
    {
        lock (_gate)
        {
            var s = _all.FirstOrDefault(x => x.Id == suggestionId);
            if (s is null)
            {
                return Result.Fail("No such suggestion.");
            }

            if (s.Status != SuggestionStatus.Pending)
            {
                return Result.Fail($"That suggestion is already {s.Status.ToString().ToLowerInvariant()}.");
            }

            if (_desk.Agents().All(a => a.Id != agentId))
            {
                return Result.Fail("No such agent.");
            }

            var editable = s.Kind is SuggestionKind.Reply or SuggestionKind.Summary;
            if (editedText is not null && !editable)
            {
                return Result.Fail("Only replies and summaries can be edited.");
            }

            var text = editedText?.Trim() ?? s.Text;
            if (editable && (text.Length == 0 || text.Length > Desk.MaxText))
            {
                return Result.Fail($"The text needs 1 to {Desk.MaxText} characters.");
            }

            var applied = Apply(s, agentId, text);
            if (!applied.Ok)
            {
                return applied;
            }

            s.Text = text;
            s.Status = SuggestionStatus.Approved;
            s.DecidedAt = _clock.GetUtcNow();
            s.DecidedBy = agentId;
            return applied;
        }
    }

    public Result Reject(Guid suggestionId, Guid agentId)
    {
        lock (_gate)
        {
            var s = _all.FirstOrDefault(x => x.Id == suggestionId);
            if (s is null)
            {
                return Result.Fail("No such suggestion.");
            }

            if (s.Status != SuggestionStatus.Pending)
            {
                return Result.Fail($"That suggestion is already {s.Status.ToString().ToLowerInvariant()}.");
            }

            if (_desk.Agents().All(a => a.Id != agentId))
            {
                return Result.Fail("No such agent.");
            }

            s.Status = SuggestionStatus.Rejected;
            s.DecidedAt = _clock.GetUtcNow();
            s.DecidedBy = agentId;
            return Result.Success("Rejected. Nothing was changed.");
        }
    }

    private Result Apply(Suggestion s, Guid agentId, string text)
    {
        switch (s.Kind)
        {
            case SuggestionKind.Reply:
            {
                var sent = _desk.Reply(s.TicketId, agentId, text, isPublic: true);
                return sent.Ok ? Result.Success("Reply sent to the customer.") : Result.Fail(sent.Message);
            }

            case SuggestionKind.Summary:
            {
                var note = _desk.Reply(s.TicketId, agentId, text, isPublic: false);
                return note.Ok ? Result.Success("Summary saved as an internal note.") : Result.Fail(note.Message);
            }

            case SuggestionKind.Assignee:
                return _desk.Assign(s.TicketId, s.AgentId);

            default:
            {
                var ticket = _desk.Ticket(s.TicketId);
                if (ticket is null)
                {
                    return Result.Fail("No such ticket.");
                }

                var wanted = new[] { s.Category, s.Skill }.Where(x => x is not null).Select(x => x!).Distinct().ToList();
                var added = wanted.Count(t => !ticket.Tags.Contains(t));
                if (ticket.Tags.Count + added > Desk.MaxTags)
                {
                    return Result.Fail($"A ticket can have up to {Desk.MaxTags} tags, so these cannot be added.");
                }

                var priority = _desk.SetPriority(s.TicketId, s.Priority!.Value);
                if (!priority.Ok)
                {
                    return priority;
                }

                foreach (var tag in wanted)
                {
                    var r = _desk.AddTag(s.TicketId, tag);
                    if (!r.Ok)
                    {
                        return r;
                    }
                }

                return Result.Success($"Priority set to {s.Priority} and tagged {string.Join(", ", wanted)}.");
            }
        }
    }

    private async Task<Created<Suggestion>> SuggestAsync(Guid ticketId, SuggestionKind kind, Func<Ticket, CancellationToken, Task<(Suggestion? Draft, string? Reason)>> ask, CancellationToken cancellationToken)
    {
        var ticket = _desk.Ticket(ticketId);
        if (ticket is null)
        {
            return new(false, "No such ticket.", null);
        }

        if (!ticket.IsActive)
        {
            return new(false, $"{ticket.Key} is already {ticket.Status.ToString().ToLowerInvariant()}.", null);
        }

        if (HasPending(ticketId, kind))
        {
            return new(false, $"A {kind.ToString().ToLowerInvariant()} suggestion is already waiting. Approve or reject it first.", null);
        }

        var (draft, reason) = await ask(ticket, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return new(false, reason ?? "No suggestion.", null);
        }

        var suggestion = new Suggestion
        {
            Id = Guid.NewGuid(),
            TicketId = ticketId,
            Kind = kind,
            Status = SuggestionStatus.Pending,
            Text = draft.Text,
            Confidence = draft.Confidence,
            Rationale = draft.Rationale,
            Priority = draft.Priority,
            Category = draft.Category,
            Skill = draft.Skill,
            AgentId = draft.AgentId,
            CreatedAt = _clock.GetUtcNow(),
        };

        lock (_gate)
        {
            if (HasPending(ticketId, kind))
            {
                return new(false, $"A {kind.ToString().ToLowerInvariant()} suggestion is already waiting. Approve or reject it first.", null);
            }

            _all.Add(suggestion);
            return new(true, "Suggestion ready for review.", suggestion.Snapshot());
        }
    }

    private bool HasPending(Guid ticketId, SuggestionKind kind)
    {
        lock (_gate)
        {
            return _all.Any(s => s.TicketId == ticketId && s.Kind == kind && s.Status == SuggestionStatus.Pending);
        }
    }
}
