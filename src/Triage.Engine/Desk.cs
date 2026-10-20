using System.Text.RegularExpressions;

namespace Triage.Engine;

/// <summary>
/// A support desk: tickets with business-hours SLA clocks that pause while the team waits on the customer, fair
/// assignment that respects skills and capacity, merging, reopening, auto-solve and auto-close, and escalation.
/// The clock is injected, so every time rule is tested by moving a simulated clock.
/// </summary>
public sealed partial class Desk
{
    public const int MaxAgents = 20;
    public const int MaxTags = 10;
    public const int MaxText = 5000;

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly BusinessCalendar _calendar;
    private readonly SlaPolicy _sla;
    private readonly DeskOptions _options;
    private readonly IReadOnlyList<TriageRule> _rules;
    private readonly List<Ticket> _tickets = [];
    private readonly Dictionary<Guid, Agent> _agents = [];
    private int _nextNumber = 1;
    private long _assignments;

    public Desk(TimeProvider clock, BusinessCalendar? calendar = null, SlaPolicy? sla = null, DeskOptions? options = null, IEnumerable<TriageRule>? rules = null)
    {
        _clock = clock;
        _calendar = calendar ?? BusinessCalendar.Standard();
        _sla = sla ?? SlaPolicy.Default;
        _options = options ?? new DeskOptions();
        _rules = rules?.ToList() ?? TriageRule.Defaults;
    }

    public BusinessCalendar Calendar => _calendar;

    private DateTimeOffset Now => _clock.GetUtcNow();

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    // ---- Agents ----

    public Created<Agent> AddAgent(string name, IEnumerable<string>? skills = null, int capacity = 5)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 40)
        {
            return new(false, "An agent needs a name of up to 40 characters.", null);
        }

        if (capacity is < 1 or > 50)
        {
            return new(false, "Capacity must be between 1 and 50 open tickets.", null);
        }

        var normalized = new List<string>();
        foreach (var skill in skills ?? [])
        {
            var tag = NormalizeTag(skill);
            if (tag is null)
            {
                return new(false, $"'{skill}' is not a valid skill. Use letters, numbers and hyphens.", null);
            }

            if (!normalized.Contains(tag))
            {
                normalized.Add(tag);
            }
        }

        lock (_gate)
        {
            if (_agents.Count >= MaxAgents)
            {
                return new(false, $"A desk can have up to {MaxAgents} agents.", null);
            }

            var agent = new Agent { Id = Guid.NewGuid(), Name = name.Trim(), Skills = normalized, Capacity = capacity };
            _agents[agent.Id] = agent;
            AssignQueuedCore();
            return new(true, "Agent added.", agent.Snapshot());
        }
    }

    /// <summary>
    /// Going away hands the agent's open tickets back to the queue for others to pick up (tickets waiting on a customer
    /// stay with them). Coming back lets the agent take queued work.
    /// </summary>
    public Result SetAvailable(Guid agentId, bool available)
    {
        lock (_gate)
        {
            if (!_agents.TryGetValue(agentId, out var agent))
            {
                return Result.Fail("No such agent.");
            }

            agent.Available = available;
            if (!available)
            {
                foreach (var ticket in _tickets.Where(t => t.AssigneeId == agentId && t.NeedsAgent))
                {
                    ticket.AssigneeId = null;
                }
            }

            AssignQueuedCore();
            return Result.Success(available ? "Available." : "Away. Open tickets went back to the queue.");
        }
    }

    public IReadOnlyList<Agent> Agents()
    {
        lock (_gate)
        {
            return _agents.Values.Select(a => a.Snapshot()).OrderBy(a => a.Name).ToList();
        }
    }

    // ---- Tickets ----

    public Created<Ticket> Create(string subject, string requester, string body, Priority priority = Priority.Normal, IEnumerable<string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Trim().Length is < 3 or > 120)
        {
            return new(false, "The subject needs 3 to 120 characters.", null);
        }

        if (string.IsNullOrWhiteSpace(requester) || !EmailPattern().IsMatch(requester.Trim()))
        {
            return new(false, "The requester needs a valid email address.", null);
        }

        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxText)
        {
            return new(false, $"The message needs 1 to {MaxText} characters.", null);
        }

        var explicitTags = new List<string>();
        foreach (var tag in tags ?? [])
        {
            var normalized = NormalizeTag(tag);
            if (normalized is null)
            {
                return new(false, $"'{tag}' is not a valid tag. Use letters, numbers and hyphens, up to 24 characters.", null);
            }

            if (!explicitTags.Contains(normalized))
            {
                explicitTags.Add(normalized);
            }
        }

        if (explicitTags.Count > MaxTags)
        {
            return new(false, $"A ticket can have up to {MaxTags} tags.", null);
        }

        lock (_gate)
        {
            var ticket = CreateCore(subject.Trim(), requester.Trim(), body.Trim(), priority, explicitTags, null);
            AssignQueuedCore();
            return new(true, $"{ticket.Key} created.", ticket.Snapshot());
        }
    }

    private Ticket CreateCore(string subject, string requester, string body, Priority priority, List<string> explicitTags, Guid? followUpOf)
    {
        var text = subject + " " + body;
        var tags = new List<string>(explicitTags);
        foreach (var rule in _rules)
        {
            if (!rule.Keywords.Any(k => Regex.IsMatch(text, $@"\b{Regex.Escape(k)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            {
                continue;
            }

            if (rule.Tag is not null && !tags.Contains(rule.Tag) && tags.Count < MaxTags)
            {
                tags.Add(rule.Tag);
            }

            if (rule.AtLeast is { } floor && floor > priority)
            {
                priority = floor;
            }
        }

        var now = Now;
        var ticket = new Ticket
        {
            Id = Guid.NewGuid(),
            Number = _nextNumber++,
            Subject = subject,
            Requester = requester,
            Priority = priority,
            Status = Status.New,
            CreatedAt = now,
            SlaStart = now,
            FollowUpOf = followUpOf,
            Tags = tags,
            Messages = [new Message(1, Author.Customer, requester, body, now)],
        };
        Recompute(ticket);
        _tickets.Add(ticket);
        return ticket;
    }

    /// <summary>A customer writes in. Where the message lands depends on the ticket: see the rules in the README.</summary>
    public Created<Ticket> CustomerReply(Guid ticketId, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxText)
        {
            return new(false, $"The message needs 1 to {MaxText} characters.", null);
        }

        lock (_gate)
        {
            var ticket = Find(ticketId);
            if (ticket is null)
            {
                return new(false, "No such ticket.", null);
            }

            // A merged ticket's conversation lives on in the ticket it was merged into.
            while (ticket.MergedInto is { } into && Find(into) is { } target)
            {
                ticket = target;
            }

            var now = Now;
            switch (ticket.Status)
            {
                case Status.Closed:
                    var followUp = CreateCore("Re: " + ticket.Subject.Replace("Re: ", "", StringComparison.Ordinal), ticket.Requester, text.Trim(), ticket.Priority, [.. ticket.Tags], ticket.Id);
                    AssignQueuedCore();
                    return new(true, $"{ticket.Key} is closed, so a follow-up {followUp.Key} was opened.", followUp.Snapshot());
                case Status.Solved:
                    ticket.Reopens++;
                    ticket.SolvedAt = null;
                    ticket.Status = Status.Open;
                    ticket.SlaStart = now;
                    ticket.PausedBusiness = TimeSpan.Zero;
                    Recompute(ticket);
                    ReleaseIfAway(ticket);
                    AddMessage(ticket, Author.Customer, ticket.Requester, text.Trim(), now);
                    AssignQueuedCore();
                    return new(true, $"{ticket.Key} was reopened.", ticket.Snapshot());
                case Status.Pending:
                    ResumeClock(ticket, now);
                    ticket.Status = Status.Open;
                    ReleaseIfAway(ticket);
                    break;
            }

            AddMessage(ticket, Author.Customer, ticket.Requester, text.Trim(), now);
            AssignQueuedCore();
            return new(true, "Message added.", ticket.Snapshot());
        }
    }

    /// <summary>
    /// An agent writes. A public reply is the first response if none was sent yet. A note is internal: it never counts
    /// as a response and cannot change the status. Replying to an unassigned ticket takes ownership of it.
    /// </summary>
    public Created<Ticket> Reply(Guid ticketId, Guid agentId, string text, bool isPublic = true, Status? setStatus = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxText)
        {
            return new(false, $"The message needs 1 to {MaxText} characters.", null);
        }

        if (setStatus is not (null or Status.Open or Status.Pending or Status.Solved))
        {
            return new(false, "A reply can leave the ticket open, make it pending or solve it.", null);
        }

        lock (_gate)
        {
            var ticket = Find(ticketId);
            if (ticket is null)
            {
                return new(false, "No such ticket.", null);
            }

            if (!_agents.TryGetValue(agentId, out var agent))
            {
                return new(false, "No such agent.", null);
            }

            if (ticket.Status == Status.Closed)
            {
                return new(false, $"{ticket.Key} is closed. If the customer writes again a follow-up is opened.", null);
            }

            var now = Now;
            if (!isPublic)
            {
                if (setStatus is not null)
                {
                    return new(false, "An internal note cannot change the status.", null);
                }

                AddMessage(ticket, Author.Note, agent.Name, text.Trim(), now);
                return new(true, "Note added.", ticket.Snapshot());
            }

            if (ticket.Status == Status.Solved && setStatus is Status.Open or Status.Pending)
            {
                return new(false, "This ticket is solved. Reopen it first.", null);
            }

            AddMessage(ticket, Author.Agent, agent.Name, text.Trim(), now);
            ticket.FirstResponseAt ??= now;
            if (ticket.AssigneeId is null && ticket.Status != Status.Solved && agent.Available)
            {
                ticket.AssigneeId = agent.Id;
                agent.LastAssigned = ++_assignments;
            }

            if (ticket.Status == Status.New)
            {
                ticket.Status = Status.Open;
            }

            switch (setStatus)
            {
                case Status.Open when ticket.Status is Status.Pending:
                    ResumeClock(ticket, now);
                    ticket.Status = Status.Open;
                    ReleaseIfAway(ticket);
                    break;
                case Status.Pending when ticket.Status is Status.Open:
                    ticket.Status = Status.Pending;
                    ticket.PendingSince = now;
                    break;
                case Status.Solved when ticket.Status != Status.Solved:
                    SolveCore(ticket, now);
                    break;
            }

            return new(true, "Reply sent.", ticket.Snapshot());
        }
    }

    public Result Solve(Guid ticketId)
    {
        lock (_gate)
        {
            var ticket = Find(ticketId);
            if (ticket is null)
            {
                return Result.Fail("No such ticket.");
            }

            if (!ticket.IsActive)
            {
                return Result.Fail($"{ticket.Key} is already {ticket.Status.ToString().ToLowerInvariant()}.");
            }

            SolveCore(ticket, Now);
            return Result.Success($"{ticket.Key} solved.");
        }
    }

    /// <summary>An agent reopens a solved ticket. The resolution clock starts again from now.</summary>
    public Result Reopen(Guid ticketId)
    {
        lock (_gate)
        {
            var ticket = Find(ticketId);
            if (ticket is null)
            {
                return Result.Fail("No such ticket.");
            }

            if (ticket.Status != Status.Solved)
            {
                return Result.Fail(ticket.Status == Status.Closed ? $"{ticket.Key} is closed. Start a follow-up instead." : $"{ticket.Key} is not solved.");
            }

            ticket.Reopens++;
            ticket.SolvedAt = null;
            ticket.Status = Status.Open;
            ticket.SlaStart = Now;
            ticket.PausedBusiness = TimeSpan.Zero;
            Recompute(ticket);
            ReleaseIfAway(ticket);
            AssignQueuedCore();
            return Result.Success($"{ticket.Key} reopened.");
        }
    }

    public Result SetPriority(Guid ticketId, Priority priority)
    {
        lock (_gate)
        {
            var ticket = Find(ticketId);
            if (ticket is null)
            {
                return Result.Fail("No such ticket.");
            }

            if (!ticket.IsActive)
            {
                return Result.Fail($"{ticket.Key} is already {ticket.Status.ToString().ToLowerInvariant()}.");
            }

            ticket.Priority = priority;
            Recompute(ticket);
            return Result.Success($"{ticket.Key} is now {priority}.");
        }
    }

    public Result AddTag(Guid ticketId, string tag)
    {
        var normalized = NormalizeTag(tag);
        if (normalized is null)
        {
            return Result.Fail("Tags use letters, numbers and hyphens, up to 24 characters.");
        }

        lock (_gate)
        {
            var ticket = Find(ticketId);
            if (ticket is null)
            {
                return Result.Fail("No such ticket.");
            }

            if (ticket.Status == Status.Closed)
            {
                return Result.Fail($"{ticket.Key} is closed.");
            }

            if (ticket.Tags.Contains(normalized))
            {
                return Result.Success("Already tagged.");
            }

            if (ticket.Tags.Count >= MaxTags)
            {
                return Result.Fail($"A ticket can have up to {MaxTags} tags.");
            }

            ticket.Tags = [.. ticket.Tags, normalized];
            return Result.Success("Tagged.");
        }
    }

    public Result RemoveTag(Guid ticketId, string tag)
    {
        lock (_gate)
        {
            var ticket = Find(ticketId);
            if (ticket is null)
            {
                return Result.Fail("No such ticket.");
            }

            var normalized = tag?.Trim().ToLowerInvariant() ?? "";
            if (!ticket.Tags.Contains(normalized))
            {
                return Result.Fail("The ticket does not have that tag.");
            }

            ticket.Tags = ticket.Tags.Where(t => t != normalized).ToList();
            return Result.Success("Tag removed.");
        }
    }

    /// <summary>Hands a ticket to an agent, or back to the queue with null. A person's choice overrides capacity but not absence.</summary>
    public Result Assign(Guid ticketId, Guid? agentId)
    {
        lock (_gate)
        {
            var ticket = Find(ticketId);
            if (ticket is null)
            {
                return Result.Fail("No such ticket.");
            }

            if (ticket.Status is Status.Solved or Status.Closed)
            {
                return Result.Fail($"{ticket.Key} is already {ticket.Status.ToString().ToLowerInvariant()}.");
            }

            if (agentId is null)
            {
                ticket.AssigneeId = null;
                return Result.Success("Back in the queue. The next assignment run will give it to someone.");
            }

            if (!_agents.TryGetValue(agentId.Value, out var agent))
            {
                return Result.Fail("No such agent.");
            }

            if (!agent.Available)
            {
                return Result.Fail($"{agent.Name} is away.");
            }

            ticket.AssigneeId = agent.Id;
            agent.LastAssigned = ++_assignments;
            return Result.Success($"{ticket.Key} assigned to {agent.Name}.");
        }
    }

    /// <summary>
    /// Folds a duplicate into the ticket it duplicates. Only tickets from the same requester can be merged. The conversation
    /// is combined in time order, the earliest first response counts, and the duplicate is closed with a pointer.
    /// </summary>
    public Result Merge(Guid sourceId, Guid targetId)
    {
        lock (_gate)
        {
            var source = Find(sourceId);
            var target = Find(targetId);
            if (source is null || target is null)
            {
                return Result.Fail("No such ticket.");
            }

            if (source.Id == target.Id)
            {
                return Result.Fail("A ticket cannot be merged into itself.");
            }

            if (!string.Equals(source.Requester, target.Requester, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Fail("Only tickets from the same requester can be merged.");
            }

            if (source.Status == Status.Closed || target.Status == Status.Closed)
            {
                return Result.Fail("Closed tickets cannot be merged.");
            }

            var now = Now;
            var combined = target.Messages.Concat(source.Messages).OrderBy(m => m.At).ThenBy(m => m.Seq).ToList();
            combined.Add(new Message(0, Author.Note, "system", $"Merged {source.Key} into this ticket.", now));
            target.Messages = combined.Select((m, i) => m with { Seq = i + 1 }).ToList();
            target.Tags = [.. target.Tags.Union(source.Tags).Take(MaxTags)];
            target.FirstResponseAt = new[] { target.FirstResponseAt, source.FirstResponseAt }.Where(d => d is not null).Min();
            if (target.Status == Status.Pending && source.NeedsAgent)
            {
                // The customer has written in again through the other ticket, so the wait is over.
                ResumeClock(target, now);
                target.Status = Status.Open;
                ReleaseIfAway(target);
            }

            if (target.Status == Status.Solved && source.IsActive)
            {
                target.Reopens++;
                target.SolvedAt = null;
                target.Status = Status.Open;
                target.SlaStart = now;
                target.PausedBusiness = TimeSpan.Zero;
                target.PendingSince = null;
                Recompute(target);
                ReleaseIfAway(target);
            }

            source.Status = Status.Closed;
            source.ClosedAt = now;
            source.MergedInto = target.Id;
            source.AssigneeId = null;
            source.PendingSince = null;
            return Result.Success($"{source.Key} merged into {target.Key}.");
        }
    }

    // ---- Automation ----

    /// <summary>The nightly job: solve what the customer has gone quiet on, close what stayed solved, escalate breaches, assign the queue.</summary>
    public AutomationReport RunAutomation()
    {
        lock (_gate)
        {
            var now = Now;
            var solved = 0;
            foreach (var ticket in _tickets.Where(t => t.Status == Status.Pending && t.PendingSince is { } since && now - since >= _options.PendingAutoSolveAfter).ToList())
            {
                SolveCore(ticket, now);
                AddMessage(ticket, Author.Note, "system", "Solved automatically: the customer did not reply.", now);
                solved++;
            }

            var closed = 0;
            foreach (var ticket in _tickets.Where(t => t.Status == Status.Solved && t.SolvedAt is { } at && now - at >= _options.SolvedAutoCloseAfter).ToList())
            {
                ticket.Status = Status.Closed;
                ticket.ClosedAt = now;
                closed++;
            }

            var escalated = 0;
            foreach (var ticket in _tickets.Where(t => t.NeedsAgent && !t.Escalated).ToList())
            {
                if (FirstResponseStateCore(ticket, now) == SlaState.Breached || ResolutionStateCore(ticket, now) == SlaState.Breached)
                {
                    ticket.Escalated = true;
                    AddMessage(ticket, Author.Note, "system", "Escalated: a service level target was missed.", now);
                    escalated++;
                }
            }

            return new AutomationReport(solved, closed, escalated, AssignQueuedCore());
        }
    }

    /// <summary>Gives queued tickets to the best available agent. Returns how many were assigned.</summary>
    public int AssignQueued()
    {
        lock (_gate)
        {
            return AssignQueuedCore();
        }
    }

    private int AssignQueuedCore()
    {
        var assigned = 0;
        var queue = _tickets.Where(t => t.NeedsAgent && t.AssigneeId is null)
            .OrderByDescending(t => t.Escalated).ThenByDescending(t => t.Priority).ThenBy(t => t.CreatedAt).ThenBy(t => t.Number)
            .ToList();
        foreach (var ticket in queue)
        {
            var agent = PickAgent(ticket);
            if (agent is null)
            {
                continue;
            }

            ticket.AssigneeId = agent.Id;
            agent.LastAssigned = ++_assignments;
            assigned++;
        }

        return assigned;
    }

    /// <summary>
    /// Among available agents with room: those whose skills match the ticket's tags, or if none, generalists; for an untagged
    /// ticket anyone. The least loaded wins, then whoever was given work longest ago, so work is shared round-robin.
    /// </summary>
    private Agent? PickAgent(Ticket ticket)
    {
        var candidates = _agents.Values.Where(a => a.Available && Load(a.Id) < a.Capacity).ToList();
        if (ticket.Tags.Count > 0)
        {
            var skilled = candidates.Where(a => a.Skills.Any(ticket.Tags.Contains)).ToList();
            candidates = skilled.Count > 0 ? skilled : candidates.Where(a => a.Skills.Count == 0).ToList();
        }

        return candidates.OrderBy(a => Load(a.Id)).ThenBy(a => a.LastAssigned).ThenBy(a => a.Name, StringComparer.Ordinal).FirstOrDefault();
    }

    private int Load(Guid agentId) => _tickets.Count(t => t.AssigneeId == agentId && t.NeedsAgent);

    // ---- Service level ----

    /// <summary>Where a ticket stands against its two targets, right now.</summary>
    public SlaView Assess(Ticket ticket)
    {
        lock (_gate)
        {
            var now = Now;
            var live = Find(ticket.Id) ?? ticket;
            return new SlaView(
                FirstResponseStateCore(live, now),
                ResolutionStateCore(live, now),
                live.FirstResponseAt is null && live.IsActive ? Signed(live.FirstResponseDue, now) : null,
                live.IsActive && live.PendingSince is null ? Signed(live.ResolutionDue, now) : null);
        }
    }

    private TimeSpan Signed(DateTimeOffset due, DateTimeOffset now) => now <= due ? _calendar.Between(now, due) : -_calendar.Between(due, now);

    private SlaState FirstResponseStateCore(Ticket ticket, DateTimeOffset now)
    {
        if (ticket.MergedInto is not null)
        {
            return SlaState.NotApplicable;
        }

        if (ticket.FirstResponseAt is { } answered)
        {
            return answered <= ticket.FirstResponseDue ? SlaState.Met : SlaState.Breached;
        }

        if (!ticket.IsActive)
        {
            return SlaState.NotApplicable;
        }

        if (now > ticket.FirstResponseDue)
        {
            return SlaState.Breached;
        }

        var target = _sla.For(ticket.Priority).FirstResponse;
        return _calendar.Between(ticket.CreatedAt, now) >= target * _options.AtRiskFraction ? SlaState.AtRisk : SlaState.Ok;
    }

    private SlaState ResolutionStateCore(Ticket ticket, DateTimeOffset now)
    {
        if (ticket.MergedInto is not null)
        {
            return SlaState.NotApplicable;
        }

        if (ticket.SolvedAt is { } solved)
        {
            return solved <= ticket.ResolutionDue ? SlaState.Met : SlaState.Breached;
        }

        if (ticket.Status == Status.Closed)
        {
            return SlaState.NotApplicable;
        }

        if (ticket.PendingSince is not null)
        {
            return SlaState.Paused;
        }

        if (now > ticket.ResolutionDue)
        {
            return SlaState.Breached;
        }

        var target = _sla.For(ticket.Priority).Resolution;
        var used = _calendar.Between(ticket.SlaStart, now) - ticket.PausedBusiness;
        return used >= target * _options.AtRiskFraction ? SlaState.AtRisk : SlaState.Ok;
    }

    // ---- Reading ----

    public Ticket? Ticket(Guid id)
    {
        lock (_gate)
        {
            return Find(id)?.Snapshot();
        }
    }

    public Ticket? Ticket(string key)
    {
        lock (_gate)
        {
            return _tickets.FirstOrDefault(t => string.Equals(t.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase))?.Snapshot();
        }
    }

    /// <summary>
    /// Active tickets first, in the order a team should work them (escalated, then the soonest deadline), then solved and
    /// closed ones, newest first.
    /// </summary>
    public IReadOnlyList<Ticket> Tickets(Status? status = null, Guid? assignee = null, bool? unassigned = null, string? tag = null, string? search = null)
    {
        lock (_gate)
        {
            var wanted = tag?.Trim().ToLowerInvariant();
            var needle = search?.Trim();
            return _tickets
                .Where(t => (status is null || t.Status == status)
                            && (assignee is null || t.AssigneeId == assignee)
                            && (unassigned is null || (t.AssigneeId is null) == unassigned)
                            && (string.IsNullOrEmpty(wanted) || t.Tags.Contains(wanted))
                            && (string.IsNullOrEmpty(needle)
                                || t.Subject.Contains(needle, StringComparison.OrdinalIgnoreCase)
                                || t.Requester.Contains(needle, StringComparison.OrdinalIgnoreCase)
                                || t.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)
                                || t.Messages.Any(m => m.Author != Author.Note && m.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))))
                .OrderBy(t => t.IsActive ? 0 : 1)
                .ThenByDescending(t => t.IsActive && t.Escalated)
                .ThenBy(t => t.IsActive ? (t.PendingSince is null ? t.ResolutionDue : DateTimeOffset.MaxValue) : DateTimeOffset.MaxValue)
                .ThenByDescending(t => t.CreatedAt)
                .ThenByDescending(t => t.Number)
                .Select(t => t.Snapshot())
                .ToList();
        }
    }

    public DeskStats Stats()
    {
        lock (_gate)
        {
            var now = Now;
            var active = _tickets.Where(t => t.IsActive).ToList();
            var atRisk = active.Count(t => FirstResponseStateCore(t, now) == SlaState.AtRisk || ResolutionStateCore(t, now) == SlaState.AtRisk);
            var breached = active.Count(t => FirstResponseStateCore(t, now) == SlaState.Breached || ResolutionStateCore(t, now) == SlaState.Breached);
            var finished = _tickets.Where(t => t.SolvedAt is not null).ToList();
            double? compliance = finished.Count == 0 ? null : Math.Round(100.0 * finished.Count(t => t.SolvedAt <= t.ResolutionDue) / finished.Count, 1);
            var answered = _tickets.Where(t => t.FirstResponseAt is not null && t.MergedInto is null).ToList();
            TimeSpan? average = answered.Count == 0 ? null : TimeSpan.FromTicks((long)answered.Average(t => _calendar.Between(t.CreatedAt, t.FirstResponseAt!.Value).Ticks));
            return new DeskStats(
                active.Count,
                active.Count(t => t.NeedsAgent && t.AssigneeId is null),
                atRisk,
                breached,
                _tickets.Count(t => t.Status == Status.Solved),
                compliance,
                average,
                Enum.GetValues<Status>().ToDictionary(s => s, s => _tickets.Count(t => t.Status == s)),
                _agents.Values.OrderBy(a => a.Name).Select(a => new AgentLoad(a.Id, a.Name, a.Available, Load(a.Id), a.Capacity)).ToList());
        }
    }

    // ---- Internals ----

    private Ticket? Find(Guid id) => _tickets.FirstOrDefault(t => t.Id == id);

    private static string? NormalizeTag(string? tag)
    {
        var value = tag?.Trim().ToLowerInvariant();
        return value is { Length: > 0 and <= 24 } && TagPattern().IsMatch(value) ? value : null;
    }

    private static void AddMessage(Ticket ticket, Author author, string by, string text, DateTimeOffset at) =>
        ticket.Messages = [.. ticket.Messages, new Message(ticket.Messages.Count + 1, author, by, text, at)];

    private void Recompute(Ticket ticket)
    {
        var target = _sla.For(ticket.Priority);
        ticket.FirstResponseDue = _calendar.Add(ticket.CreatedAt, target.FirstResponse);
        ticket.ResolutionDue = _calendar.Add(ticket.SlaStart, target.Resolution + ticket.PausedBusiness);
    }

    /// <summary>The customer answered, so the time spent waiting does not count against the team.</summary>
    private void ResumeClock(Ticket ticket, DateTimeOffset now)
    {
        if (ticket.PendingSince is { } since)
        {
            ticket.PausedBusiness += _calendar.Between(since, now);
            ticket.PendingSince = null;
            Recompute(ticket);
        }
    }

    /// <summary>A ticket that needs an agent again must not stay with someone who has gone away.</summary>
    private void ReleaseIfAway(Ticket ticket)
    {
        if (ticket.AssigneeId is { } owner && _agents.TryGetValue(owner, out var agent) && !agent.Available)
        {
            ticket.AssigneeId = null;
        }
    }

    private void SolveCore(Ticket ticket, DateTimeOffset now)
    {
        ResumeClock(ticket, now);
        ticket.Status = Status.Solved;
        ticket.SolvedAt = now;
        AssignQueuedCore();
    }
}

public sealed record SlaView(SlaState FirstResponse, SlaState Resolution, TimeSpan? FirstResponseRemaining, TimeSpan? ResolutionRemaining);
