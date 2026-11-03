using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Triage.Copilot;
using Triage.Engine;

namespace Triage.Mcp;

public static class TriageMcp
{
    /// <summary>Registers every [McpServerTool] method of <see cref="TriageTools"/> on one server.</summary>
    public static McpServerOptions Options(TriageTools tools)
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var method in typeof(TriageTools).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
        {
            collection.Add(McpServerTool.Create(method, tools));
        }

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "triage-mcp", Version = "1.0.0" },
            ToolCollection = collection,
            ServerInstructions =
                "A demo help desk on a frozen clock with fake data. Read tools are free to use. " +
                "draft_reply, approve_suggestion and reject_suggestion change state and need confirm: true, which you must only set after the user agrees. " +
                "Nothing reaches a customer unless approve_suggestion is called for a reply.",
        };
    }

    /// <summary>The fake desk the server hosts: three agents and a handful of tickets, with the clock stopped on Tuesday 10:30 so some are already overdue.</summary>
    public static (Desk Desk, SimulatedClock Clock) DemoDesk()
    {
        var clock = new SimulatedClock(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));
        var desk = new Desk(clock);
        desk.AddAgent("Ana");
        desk.AddAgent("Ben", ["network"], 3);
        var cleo = desk.AddAgent("Cleo", ["billing", "account"], 4).Value!;

        void Hours(double h) => clock.Advance(TimeSpan.FromHours(h));

        Hours(1);
        var login = desk.Create("Locked out of my account", "maria@globex.example", "I tried my password five times and now I am locked out before a demo.").Value!;
        Hours(1);
        desk.Create("Charged twice for September", "li@initech.example", "My card was charged twice for the September invoice. Please refund one.");
        Hours(1);
        desk.Create("VPN drops every hour", "sam@acme.example", "The VPN disconnects roughly every hour since Monday. You can reach me on 555-123-4567.", Priority.High, ["network"]);
        desk.Reply(login.Id, cleo.Id, "Hi Maria, I have reset your lock. Can you try signing in now?", true, Status.Pending);
        Hours(22);
        desk.RunAutomation();
        desk.Create("Site is down for our whole team", "ops@umbrella.example", "We cannot access the dashboard at all. This is an outage for us.");
        Hours(0.5);
        return (desk, clock);
    }
}
