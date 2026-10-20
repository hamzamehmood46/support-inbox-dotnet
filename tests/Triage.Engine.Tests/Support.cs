using Triage.Engine;

namespace Triage.Engine.Tests;

/// <summary>A desk on a clock the test controls. It starts on Monday 5 October 2026 at 10:00 UTC, an hour into the working day.</summary>
public sealed class Shop
{
    public static readonly DateTimeOffset Start = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    public SimulatedClock Clock { get; } = new(Start);
    public Desk Desk { get; }
    public Agent Ana { get; }
    public Agent Ben { get; }

    public Shop(bool agents = true, BusinessCalendar? calendar = null, DeskOptions? options = null, SlaPolicy? sla = null, int capacity = 5)
    {
        Desk = new Desk(Clock, calendar, sla, options);
        if (agents)
        {
            Ana = Desk.AddAgent("Ana", capacity: capacity).Value!;
            Ben = Desk.AddAgent("Ben", capacity: capacity).Value!;
        }
        else
        {
            Ana = null!;
            Ben = null!;
        }
    }

    public Ticket New(string subject = "Report export is slow", string requester = "cara@example.com", string body = "It takes several minutes to export.", Priority priority = Priority.Normal, params string[] tags)
    {
        var created = Desk.Create(subject, requester, body, priority, tags);
        Assert.True(created.Ok, created.Message);
        return created.Value!;
    }

    public Ticket Get(Ticket t) => Desk.Ticket(t.Id)!;

    public void Hours(double hours) => Clock.Advance(TimeSpan.FromHours(hours));

    public void Days(double days) => Clock.Advance(TimeSpan.FromDays(days));

    public void GoTo(DateTimeOffset at) => Clock.Advance(at - Clock.GetUtcNow());

    public Ticket Reply(Ticket t, Agent? by = null, string text = "Looking into it.", bool isPublic = true, Status? status = null)
    {
        var result = Desk.Reply(t.Id, (by ?? Ana).Id, text, isPublic, status);
        Assert.True(result.Ok, result.Message);
        return result.Value!;
    }

    public Ticket Customer(Ticket t, string text = "Any news?")
    {
        var result = Desk.CustomerReply(t.Id, text);
        Assert.True(result.Ok, result.Message);
        return result.Value!;
    }

    public SlaView Sla(Ticket t) => Desk.Assess(Get(t));

    public static DateTimeOffset At(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);
}
