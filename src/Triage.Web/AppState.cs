using Triage.Engine;

namespace Triage.Web;

/// <summary>A small support team and its inbox, on a clock you control.</summary>
public sealed class AppState
{
    public static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    public SimulatedClock Clock { get; private set; } = null!;
    public Desk Desk { get; private set; } = null!;
    public bool AutoRun { get; set; } = true;
    public Guid? Selected { get; set; }
    public Guid AgentId { get; set; }
    public string Message { get; private set; } = "";
    public bool MessageOk { get; private set; } = true;

    public AppState() => Reset();

    public DateTimeOffset Now => Clock.GetUtcNow();

    public void Reset()
    {
        Clock = new SimulatedClock(Start);
        Desk = new Desk(Clock);
        var ana = Desk.AddAgent("Ana").Value!;
        Desk.AddAgent("Ben", ["network"], 3);
        Desk.AddAgent("Cleo", ["billing", "account"], 4);
        AgentId = ana.Id;
        Seed();
        Selected = null;
        Message = "";
    }

    private void Seed()
    {
        Hours(1);
        var login = Desk.Create("Locked out of my account", "maria@globex.com", "I tried my password five times and now I am locked out before a demo.").Value!;
        Hours(1);
        Desk.Create("Charged twice for September", "li@initech.io", "My card was charged twice for the September invoice. Please refund one.");
        Hours(1);
        var vpn = Desk.Create("VPN drops every hour", "sam@acme.org", "The VPN disconnects roughly every hour since Monday.", Priority.High, ["network"]).Value!;
        Reply(login.Id, Desk.Agents().First(a => a.Name == "Cleo").Id, "Hi Maria, I have reset your lock. Can you try signing in now?", Status.Pending);
        Hours(22);
        Desk.RunAutomation();
        Desk.Create("Site is down for our whole team", "ops@umbrella.co", "We cannot access the dashboard at all. This is an outage for us.");
        _ = vpn;
        Hours(0.5);
    }

    private void Hours(double h) => Clock.Advance(TimeSpan.FromHours(h));

    private void Reply(Guid ticket, Guid agent, string text, Status? status) => Desk.Reply(ticket, agent, text, true, status);

    public void Say(string message, bool ok = true)
    {
        Message = message;
        MessageOk = ok;
    }

    /// <summary>Moves time forward an hour at a time so the daily rules fire at the right moments.</summary>
    public void Wait(TimeSpan by)
    {
        var steps = (int)Math.Round(by.TotalHours);
        var report = new AutomationReport(0, 0, 0, 0);
        for (var i = 0; i < steps; i++)
        {
            Clock.Advance(TimeSpan.FromHours(1));
            if (AutoRun)
            {
                var r = Desk.RunAutomation();
                report = new AutomationReport(report.AutoSolved + r.AutoSolved, report.AutoClosed + r.AutoClosed, report.Escalated + r.Escalated, report.Assigned + r.Assigned);
            }
        }

        Say(AutoRun
            ? $"Moved forward {steps} hour(s). Automation: {report.AutoSolved} auto-solved, {report.AutoClosed} auto-closed, {report.Escalated} escalated."
            : $"Moved forward {steps} hour(s). Automation did not run.");
    }

    public static string Fmt(TimeSpan? span)
    {
        if (span is null)
        {
            return "";
        }

        var abs = span.Value.Duration();
        var text = abs.TotalDays >= 1 ? $"{(int)abs.TotalHours}h" : abs.TotalHours >= 1 ? $"{(int)abs.TotalHours}h {abs.Minutes}m" : $"{abs.Minutes}m";
        return span.Value < TimeSpan.Zero ? $"overdue {text}" : $"{text} left";
    }
}
