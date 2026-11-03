using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Triage.Copilot;
using Triage.Engine;

namespace Triage.Mcp;

/// <summary>
/// The help desk as MCP tools. Read-only tools run freely. Any tool that stores or changes something needs <c>confirm: true</c>
/// and refuses without it, before it looks anything up. Ticket text is redacted and requester addresses are never returned.
/// </summary>
public sealed class TriageTools
{
    private const string ConfirmHelp = "Must be true. Set it only after the user has agreed to this exact action; the call is refused otherwise.";

    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Desk _desk;
    private readonly TriageCopilot _copilot;
    private readonly ITriageAssistant _assistant;

    public TriageTools(Desk desk, TriageCopilot copilot, ITriageAssistant assistant)
    {
        _desk = desk;
        _copilot = copilot;
        _assistant = assistant;
    }

    [McpServerTool(Name = "list_tickets", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List tickets on the demo desk, newest first, with their SLA state. Read-only.")]
    public CallToolResult ListTickets(
        [Description("Only tickets in this status: New, Open, Pending, Solved or Closed.")] string? status = null,
        [Description("Only tickets whose first-reply or resolution clock is in this state: Ok, AtRisk, Breached, Met or Paused.")] string? sla_state = null,
        [Description("Most tickets to return, 1 to 50. Default 20.")] int limit = 20)
    {
        Status? wantStatus = null;
        if (status is not null)
        {
            if (!Enum.TryParse<Status>(status, true, out var s) || !Enum.IsDefined(s))
            {
                return Refuse($"Unknown status '{status}'. Use New, Open, Pending, Solved or Closed.");
            }

            wantStatus = s;
        }

        SlaState? wantSla = null;
        if (sla_state is not null)
        {
            if (!Enum.TryParse<SlaState>(sla_state, true, out var s) || !Enum.IsDefined(s))
            {
                return Refuse($"Unknown sla_state '{sla_state}'. Use Ok, AtRisk, Breached, Met or Paused.");
            }

            wantSla = s;
        }

        var names = Names();
        var rows = _desk.Tickets(wantStatus)
            .Where(t => wantSla is null || Sla(t, out var f, out var r) && (f == wantSla || r == wantSla))
            .OrderByDescending(t => t.Number)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(t => Row(t, names))
            .ToList();
        return Json(new { count = rows.Count, tickets = rows });
    }

    [McpServerTool(Name = "get_ticket", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Show one ticket with its messages and any suggestions waiting on it. Emails and phone numbers are redacted. Read-only.")]
    public CallToolResult GetTicket([Description("The ticket key, for example T-0003.")] string key)
    {
        if (Find(key) is not { } t)
        {
            return NoTicket(key);
        }

        return Json(new
        {
            ticket = Row(t, Names()),
            messages = t.Messages.Select(m => new { author = m.Author, at = m.At, text = Redactor.Redact(m.Text) }),
            suggestions = _copilot.Suggestions(t.Id).Select(Suggestion),
        });
    }

    [McpServerTool(Name = "summarize_ticket", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Summarize a ticket's conversation in a few sentences. Nothing is stored and the ticket is not changed. Read-only.")]
    public async Task<CallToolResult> SummarizeTicket([Description("The ticket key, for example T-0003.")] string key, CancellationToken cancellationToken = default)
    {
        if (Find(key) is not { } t)
        {
            return NoTicket(key);
        }

        var o = await _assistant.SummarizeThreadAsync(t, cancellationToken);
        return o.Value is { } s
            ? Json(new { key = t.Key, summary = s.Summary, confidence = s.Confidence, rationale = s.Rationale })
            : Refuse(o.Reason ?? "No summary available.");
    }

    [McpServerTool(Name = "draft_reply", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Draft a reply for a ticket and store it as a Pending suggestion. Nothing is sent to the customer: a person must approve it with approve_suggestion. Needs confirm: true because it stores a suggestion.")]
    public async Task<CallToolResult> DraftReply(
        [Description("The ticket key, for example T-0003.")] string key,
        [Description(ConfirmHelp)] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            return Refuse("Refused: draft_reply stores a pending suggestion. Call it again with confirm: true once the user agrees.");
        }

        if (Find(key) is not { } t)
        {
            return NoTicket(key);
        }

        var made = await _copilot.SuggestReplyAsync(t.Id, cancellationToken);
        return made.Value is { } s
            ? Json(new { suggestion = Suggestion(s), note = "Nothing has been sent. Use approve_suggestion with confirm: true to send it." })
            : Refuse(made.Message);
    }

    [McpServerTool(Name = "approve_suggestion", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Approve a pending suggestion as a named agent. Approving a reply SENDS it to the customer; approving other kinds changes the ticket's priority, tags, notes or assignee. Needs confirm: true.")]
    public CallToolResult ApproveSuggestion(
        [Description("The suggestion id from draft_reply or get_ticket.")] string suggestion_id,
        [Description("Name of the agent approving it, for example Ana.")] string agent,
        [Description(ConfirmHelp)] bool confirm = false,
        [Description("Optional replacement text for a reply or summary.")] string? edited_text = null)
    {
        if (!confirm)
        {
            return Refuse("Refused: approve_suggestion changes the ticket and may message the customer. Call it again with confirm: true once the user agrees.");
        }

        if (!Guid.TryParse(suggestion_id, out var id))
        {
            return Refuse("suggestion_id must be an id from draft_reply or get_ticket.");
        }

        var who = _desk.Agents().FirstOrDefault(a => a.Name.Equals(agent, StringComparison.OrdinalIgnoreCase));
        if (who is null)
        {
            return Refuse($"No agent named '{agent}'. Known agents: {string.Join(", ", _desk.Agents().Select(a => a.Name))}.");
        }

        var r = _copilot.Approve(id, who.Id, edited_text);
        return r.Ok
            ? Json(new { approved = true, message = r.Message, suggestion = _copilot.Suggestion(id) is { } s ? Suggestion(s) : null })
            : Refuse(r.Message);
    }

    [McpServerTool(Name = "reject_suggestion", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Reject a pending suggestion so a fresh one can be drafted. Nothing is sent and the ticket is not changed. Needs confirm: true because it records a decision.")]
    public CallToolResult RejectSuggestion(
        [Description("The suggestion id from draft_reply or get_ticket.")] string suggestion_id,
        [Description("Name of the agent rejecting it, for example Ana.")] string agent,
        [Description(ConfirmHelp)] bool confirm = false)
    {
        if (!confirm)
        {
            return Refuse("Refused: reject_suggestion records a decision. Call it again with confirm: true once the user agrees.");
        }

        if (!Guid.TryParse(suggestion_id, out var id))
        {
            return Refuse("suggestion_id must be an id from draft_reply or get_ticket.");
        }

        var who = _desk.Agents().FirstOrDefault(a => a.Name.Equals(agent, StringComparison.OrdinalIgnoreCase));
        if (who is null)
        {
            return Refuse($"No agent named '{agent}'. Known agents: {string.Join(", ", _desk.Agents().Select(a => a.Name))}.");
        }

        var r = _copilot.Reject(id, who.Id);
        return r.Ok
            ? Json(new { rejected = true, message = r.Message, suggestion = _copilot.Suggestion(id) is { } s ? Suggestion(s) : null })
            : Refuse(r.Message);
    }

    [McpServerTool(Name = "sla_breaches", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List active tickets whose first-reply or resolution deadline has already passed, most overdue first, plus how many more are at risk. Read-only.")]
    public CallToolResult SlaBreaches()
    {
        var names = Names();
        var active = _desk.Tickets().Where(t => t.IsActive).ToList();
        var breached = active
            .Where(t => Sla(t, out var f, out var r) && (f == SlaState.Breached || r == SlaState.Breached))
            .OrderBy(t => _desk.Assess(t).FirstResponseRemaining ?? TimeSpan.MaxValue)
            .Select(t => Row(t, names))
            .ToList();
        var atRisk = active.Count(t => Sla(t, out var f, out var r) && (f == SlaState.AtRisk || r == SlaState.AtRisk) && f != SlaState.Breached && r != SlaState.Breached);
        return Json(new { breachedCount = breached.Count, atRiskCount = atRisk, breached });
    }

    private Ticket? Find(string key) => _desk.Ticket(key?.Trim().ToUpperInvariant() ?? "");

    private bool Sla(Ticket t, out SlaState first, out SlaState resolution)
    {
        var view = _desk.Assess(t);
        first = view.FirstResponse;
        resolution = view.Resolution;
        return true;
    }

    private Dictionary<Guid, string> Names() => _desk.Agents().ToDictionary(a => a.Id, a => a.Name);

    private object Row(Ticket t, Dictionary<Guid, string> names)
    {
        var sla = _desk.Assess(t);
        return new
        {
            key = t.Key,
            subject = Redactor.Redact(t.Subject),
            status = t.Status,
            priority = t.Priority,
            assignee = t.AssigneeId is { } a && names.TryGetValue(a, out var n) ? n : null,
            tags = t.Tags,
            escalated = t.Escalated,
            firstReply = sla.FirstResponse,
            resolution = sla.Resolution,
        };
    }

    private static object Suggestion(Suggestion s) => new
    {
        id = s.Id,
        kind = s.Kind,
        status = s.Status,
        text = Redactor.Redact(s.Text),
        confidence = s.Confidence,
        rationale = s.Rationale,
    };

    private static CallToolResult NoTicket(string key) => Refuse($"No ticket '{key}'. Use list_tickets to see the keys.");

    private static CallToolResult Json(object value) => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, Output) }] };

    private static CallToolResult Refuse(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
