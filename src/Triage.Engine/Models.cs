namespace Triage.Engine;

public enum Priority
{
    Low,
    Normal,
    High,
    Urgent,
}

public enum Status
{
    New,
    Open,

    /// <summary>Waiting on the customer. The resolution clock is paused.</summary>
    Pending,
    Solved,
    Closed,
}

public enum Author
{
    Customer,
    Agent,

    /// <summary>An internal note the customer never sees. It is not a reply, so it never counts as a first response.</summary>
    Note,
}

public sealed record Message(int Seq, Author Author, string By, string Text, DateTimeOffset At);

public sealed class Agent
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Tags this agent handles. An agent with no skills is a generalist.</summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    public int Capacity { get; init; } = 5;
    public bool Available { get; internal set; } = true;
    internal long LastAssigned { get; set; }

    internal Agent Snapshot() => (Agent)MemberwiseClone();
}

public sealed class Ticket
{
    public Guid Id { get; init; }
    public int Number { get; init; }
    public string Key => $"T-{Number:0000}";
    public string Subject { get; internal set; } = "";
    public string Requester { get; init; } = "";
    public Priority Priority { get; internal set; }
    public Status Status { get; internal set; }
    public Guid? AssigneeId { get; internal set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FirstResponseAt { get; internal set; }
    public DateTimeOffset? SolvedAt { get; internal set; }
    public DateTimeOffset? ClosedAt { get; internal set; }
    public DateTimeOffset? PendingSince { get; internal set; }

    /// <summary>When the resolution clock last started: the creation time, or the moment the customer reopened it.</summary>
    public DateTimeOffset SlaStart { get; internal set; }

    /// <summary>Working time already spent waiting on the customer, which does not count against the deadline.</summary>
    public TimeSpan PausedBusiness { get; internal set; }

    public DateTimeOffset FirstResponseDue { get; internal set; }
    public DateTimeOffset ResolutionDue { get; internal set; }
    public bool Escalated { get; internal set; }
    public int Reopens { get; internal set; }
    public Guid? MergedInto { get; internal set; }
    public Guid? FollowUpOf { get; internal set; }
    public IReadOnlyList<string> Tags { get; internal set; } = [];
    public IReadOnlyList<Message> Messages { get; internal set; } = [];

    public bool IsActive => Status is Status.New or Status.Open or Status.Pending;
    public bool NeedsAgent => Status is Status.New or Status.Open;

    internal Ticket Snapshot() => (Ticket)MemberwiseClone();
}

public enum SlaState
{
    Ok,
    AtRisk,
    Breached,
    Met,
    Paused,
    NotApplicable,
}

public sealed record SlaTarget(TimeSpan FirstResponse, TimeSpan Resolution);

/// <summary>How long each priority has to get a first reply and to be solved, in working time.</summary>
public sealed class SlaPolicy
{
    private readonly Dictionary<Priority, SlaTarget> _targets;

    public SlaPolicy(IReadOnlyDictionary<Priority, SlaTarget> targets)
    {
        foreach (var priority in Enum.GetValues<Priority>())
        {
            if (!targets.TryGetValue(priority, out var target) || target.FirstResponse <= TimeSpan.Zero || target.Resolution < target.FirstResponse)
            {
                throw new ArgumentException($"Priority {priority} needs a first-response target above zero and a resolution target at least as long.");
            }
        }

        _targets = new Dictionary<Priority, SlaTarget>(targets);
    }

    public SlaTarget For(Priority priority) => _targets[priority];

    public static SlaPolicy Default { get; } = new(new Dictionary<Priority, SlaTarget>
    {
        [Priority.Urgent] = new(TimeSpan.FromHours(1), TimeSpan.FromHours(4)),
        [Priority.High] = new(TimeSpan.FromHours(4), TimeSpan.FromHours(8)),
        [Priority.Normal] = new(TimeSpan.FromHours(8), TimeSpan.FromHours(24)),
        [Priority.Low] = new(TimeSpan.FromHours(24), TimeSpan.FromHours(40)),
    });
}

/// <summary>Words in a new ticket that decide its tags and a minimum priority, so triage does not wait for a person.</summary>
public sealed record TriageRule(string[] Keywords, string? Tag, Priority? AtLeast = null)
{
    public static IReadOnlyList<TriageRule> Defaults { get; } =
    [
        new(["refund", "invoice", "charge", "charged", "billing", "payment"], "billing"),
        new(["password", "login", "log in", "locked out", "2fa"], "account"),
        new(["outage", "is down", "cannot access", "can't access", "crash", "crashing"], "incident", Priority.Urgent),
        new(["feature request", "suggestion", "would be nice"], "feedback"),
    ];
}

public sealed record Result(bool Ok, string Message)
{
    public static Result Success(string message = "Done.") => new(true, message);
    public static Result Fail(string message) => new(false, message);
}

public sealed record Created<T>(bool Ok, string Message, T? Value) where T : class;

public sealed record AutomationReport(int AutoSolved, int AutoClosed, int Escalated, int Assigned)
{
    public bool DidAnything => AutoSolved + AutoClosed + Escalated + Assigned > 0;
}

public sealed record DeskOptions
{
    public TimeSpan PendingAutoSolveAfter { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan SolvedAutoCloseAfter { get; init; } = TimeSpan.FromDays(3);

    /// <summary>The share of a target that may be used up before a ticket is marked at risk.</summary>
    public double AtRiskFraction { get; init; } = 0.75;
}

public sealed record DeskStats(
    int Active,
    int Unassigned,
    int AtRisk,
    int Breached,
    int Solved,
    double? SlaCompliancePercent,
    TimeSpan? AverageFirstResponse,
    IReadOnlyDictionary<Status, int> ByStatus,
    IReadOnlyList<AgentLoad> Agents);

public sealed record AgentLoad(Guid AgentId, string Name, bool Available, int Open, int Capacity);
