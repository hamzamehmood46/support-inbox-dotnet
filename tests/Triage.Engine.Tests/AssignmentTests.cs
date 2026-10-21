using Triage.Engine;

namespace Triage.Engine.Tests;

public class AssignmentTests
{
    [Fact]
    public void Work_is_shared_round_robin_between_equally_loaded_agents()
    {
        var s = new Shop();

        var owners = Enumerable.Range(1, 6).Select(i => s.New("Ticket " + i).AssigneeId).ToList();

        Assert.Equal([s.Ana.Id, s.Ben.Id, s.Ana.Id, s.Ben.Id, s.Ana.Id, s.Ben.Id], owners);
    }

    [Fact]
    public void When_loads_are_equal_whoever_was_given_work_longest_ago_goes_next()
    {
        var s = new Shop();
        s.Desk.SetAvailable(s.Ana.Id, false);
        var first = s.New("One");
        s.Desk.SetAvailable(s.Ana.Id, true);
        var second = s.New("Two");
        Assert.Equal(s.Ben.Id, first.AssigneeId);
        Assert.Equal(s.Ana.Id, second.AssigneeId);
        s.Desk.Solve(first.Id);
        s.Desk.Solve(second.Id);

        var third = s.New("Three");

        Assert.Equal(s.Ben.Id, third.AssigneeId);
    }

    [Fact]
    public void The_least_loaded_agent_gets_the_next_ticket_even_when_it_is_not_their_turn()
    {
        var s = new Shop();
        var a = s.New("One");
        var b = s.New("Two");
        var c = s.New("Three");
        s.Desk.Solve(a.Id);
        s.Desk.Solve(c.Id);

        // Ana's two tickets are solved, so she has 0 and Ben has 1.
        var next = s.New("Four");

        Assert.Equal(s.Ana.Id, next.AssigneeId);
        Assert.Equal(s.Ben.Id, s.Get(b).AssigneeId);
    }

    [Fact]
    public void An_agent_at_capacity_gets_nothing_more_and_the_ticket_waits_in_the_queue()
    {
        var s = new Shop(capacity: 1);
        s.New("One");
        s.New("Two");

        var third = s.New("Three");

        Assert.Null(third.AssigneeId);
        Assert.Equal(1, s.Desk.Stats().Unassigned);
    }

    [Fact]
    public void Solving_a_ticket_frees_a_slot_and_the_queue_moves()
    {
        var s = new Shop(capacity: 1);
        var first = s.New("One");
        s.New("Two");
        var waiting = s.New("Three");

        s.Desk.Solve(first.Id);

        Assert.Equal(first.AssigneeId, s.Get(waiting).AssigneeId);
    }

    [Fact]
    public void Tickets_waiting_on_the_customer_do_not_count_toward_an_agents_load()
    {
        var s = new Shop(capacity: 1);
        var first = s.New("One");
        s.New("Two");
        s.Reply(first, status: Status.Pending, by: s.Desk.Agents().Single(a => a.Id == first.AssigneeId));

        var third = s.New("Three");

        Assert.Equal(first.AssigneeId, third.AssigneeId);
    }

    [Fact]
    public void An_agent_who_is_away_gets_no_new_tickets()
    {
        var s = new Shop();
        s.Desk.SetAvailable(s.Ana.Id, false);

        var owners = Enumerable.Range(1, 3).Select(i => s.New("Ticket " + i).AssigneeId).Distinct().ToList();

        Assert.Equal([s.Ben.Id], owners);
    }

    [Fact]
    public void Going_away_hands_open_tickets_to_the_others_but_keeps_those_waiting_on_a_customer()
    {
        var s = new Shop();
        var open = s.New("One");
        s.New("Two");
        var waiting = s.New("Three");
        s.Reply(waiting, s.Ana, status: Status.Pending);

        s.Desk.SetAvailable(s.Ana.Id, false);

        Assert.Equal(s.Ben.Id, s.Get(open).AssigneeId);
        Assert.Equal(s.Ana.Id, s.Get(waiting).AssigneeId);
        Assert.Equal(0, s.Desk.Stats().Unassigned);
    }

    [Fact]
    public void Tickets_nobody_can_take_wait_until_someone_returns()
    {
        var s = new Shop();
        s.Desk.SetAvailable(s.Ana.Id, false);
        s.Desk.SetAvailable(s.Ben.Id, false);
        var t = s.New("One");
        Assert.Null(t.AssigneeId);

        s.Desk.SetAvailable(s.Ben.Id, true);

        Assert.Equal(s.Ben.Id, s.Get(t).AssigneeId);
    }

    [Fact]
    public void A_new_agent_picks_up_the_queue()
    {
        var s = new Shop(agents: false);
        var t = s.New("One");
        Assert.Null(t.AssigneeId);

        var agent = s.Desk.AddAgent("Cleo").Value!;

        Assert.Equal(agent.Id, s.Get(t).AssigneeId);
    }

    [Fact]
    public void A_tagged_ticket_goes_to_an_agent_with_that_skill()
    {
        var s = new Shop(agents: false);
        var generalist = s.Desk.AddAgent("Gen").Value!;
        var network = s.Desk.AddAgent("Net", ["network"]).Value!;

        var t = s.New("Switch is flapping", tags: ["network"]);

        Assert.Equal(network.Id, t.AssigneeId);
        _ = generalist;
    }

    [Fact]
    public void Without_a_matching_specialist_a_tagged_ticket_goes_to_a_generalist()
    {
        var s = new Shop(agents: false);
        s.Desk.AddAgent("Net", ["network"]);
        var generalist = s.Desk.AddAgent("Gen").Value!;

        var t = s.New("Question about hours", tags: ["opening-hours"]);

        Assert.Equal(generalist.Id, t.AssigneeId);
    }

    [Fact]
    public void A_specialist_does_not_take_work_outside_their_skills_if_there_is_no_generalist()
    {
        var s = new Shop(agents: false);
        s.Desk.AddAgent("Net", ["network"]);

        Assert.Null(s.New("Refund please", tags: ["billing"]).AssigneeId);
    }

    [Fact]
    public void An_untagged_ticket_can_go_to_anyone()
    {
        var s = new Shop(agents: false);
        var specialist = s.Desk.AddAgent("Net", ["network"]).Value!;

        Assert.Equal(specialist.Id, s.New("Something general").AssigneeId);
    }

    [Fact]
    public void Among_matching_specialists_the_least_loaded_wins()
    {
        var s = new Shop(agents: false);
        var a = s.Desk.AddAgent("Net A", ["network"]).Value!;
        var b = s.Desk.AddAgent("Net B", ["network"]).Value!;

        var owners = Enumerable.Range(1, 4).Select(i => s.New("Net " + i, tags: ["network"]).AssigneeId).ToList();

        Assert.Equal([a.Id, b.Id, a.Id, b.Id], owners);
    }

    [Fact]
    public void A_specialist_at_capacity_leaves_the_ticket_for_a_generalist()
    {
        var s = new Shop(agents: false);
        s.Desk.AddAgent("Net", ["network"], capacity: 1);
        var generalist = s.Desk.AddAgent("Gen").Value!;
        s.New("First", tags: ["network"]);

        var second = s.New("Second", tags: ["network"]);

        Assert.Equal(generalist.Id, second.AssigneeId);
    }

    [Fact]
    public void When_a_slot_opens_the_most_urgent_queued_ticket_goes_first()
    {
        var s = new Shop(agents: false);
        var agent = s.Desk.AddAgent("Solo", capacity: 1).Value!;
        var busy = s.New("Busy", priority: Priority.Normal);
        var low = s.New("Low and old", priority: Priority.Low);
        s.Hours(1);
        var urgent = s.New("Urgent and newer", priority: Priority.Urgent);

        s.Desk.Solve(busy.Id);

        Assert.Equal(agent.Id, s.Get(urgent).AssigneeId);
        Assert.Null(s.Get(low).AssigneeId);
    }

    [Fact]
    public void Equal_priority_is_first_come_first_served()
    {
        var s = new Shop(agents: false);
        var agent = s.Desk.AddAgent("Solo", capacity: 1).Value!;
        var busy = s.New("Busy");
        var older = s.New("Older");
        s.Hours(1);
        var newer = s.New("Newer");

        s.Desk.Solve(busy.Id);

        Assert.Equal(agent.Id, s.Get(older).AssigneeId);
        Assert.Null(s.Get(newer).AssigneeId);
    }

    [Fact]
    public void An_escalated_ticket_jumps_the_queue()
    {
        var s = new Shop(agents: false);
        var late = s.New("Waiting for days");
        s.Days(2);
        var urgent = s.New("Fresh and urgent", priority: Priority.Urgent);
        s.Desk.RunAutomation();
        Assert.True(s.Get(late).Escalated);

        var agent = s.Desk.AddAgent("Solo", capacity: 1).Value!;

        Assert.Equal(agent.Id, s.Get(late).AssigneeId);
        Assert.Null(s.Get(urgent).AssigneeId);
    }

    [Fact]
    public void A_person_can_assign_beyond_capacity_but_not_to_someone_who_is_away()
    {
        var s = new Shop(capacity: 1);
        s.New("One");
        s.New("Two");
        var third = s.New("Three");
        s.Desk.SetAvailable(s.Ben.Id, false);

        Assert.True(s.Desk.Assign(third.Id, s.Ana.Id).Ok);
        Assert.Equal(2, s.Desk.Stats().Agents.Single(a => a.AgentId == s.Ana.Id).Open);
        var refused = s.Desk.Assign(third.Id, s.Ben.Id);
        Assert.False(refused.Ok);
        Assert.Contains("away", refused.Message);
    }

    [Fact]
    public void A_ticket_can_be_put_back_in_the_queue_and_waits_for_the_next_assignment_run()
    {
        var s = new Shop();
        var t = s.New("One");

        Assert.True(s.Desk.Assign(t.Id, null).Ok);
        Assert.Null(s.Get(t).AssigneeId);

        Assert.Equal(1, s.Desk.AssignQueued());
        Assert.NotNull(s.Get(t).AssigneeId);
    }

    [Fact]
    public void Solved_and_closed_tickets_cannot_be_assigned()
    {
        var s = new Shop();
        var t = s.New("One");
        s.Desk.Solve(t.Id);

        Assert.False(s.Desk.Assign(t.Id, s.Ana.Id).Ok);
    }

    [Fact]
    public void Assigning_to_things_that_do_not_exist_is_refused()
    {
        var s = new Shop();
        var t = s.New("One");

        Assert.False(s.Desk.Assign(Guid.NewGuid(), s.Ana.Id).Ok);
        Assert.False(s.Desk.Assign(t.Id, Guid.NewGuid()).Ok);
        Assert.False(s.Desk.SetAvailable(Guid.NewGuid(), true).Ok);
    }

    [Fact]
    public void Agent_details_are_checked()
    {
        var s = new Shop(agents: false);

        Assert.False(s.Desk.AddAgent(" ").Ok);
        Assert.False(s.Desk.AddAgent(new string('a', 41)).Ok);
        Assert.False(s.Desk.AddAgent("Ok", capacity: 0).Ok);
        Assert.False(s.Desk.AddAgent("Ok", capacity: 51).Ok);
        Assert.False(s.Desk.AddAgent("Ok", ["bad skill"]).Ok);
        Assert.Equal(["network", "linux"], s.Desk.AddAgent("Ok", [" Network ", "linux", "NETWORK"]).Value!.Skills);
    }

    [Fact]
    public void A_desk_has_an_agent_limit()
    {
        var s = new Shop(agents: false);
        for (var i = 0; i < Desk.MaxAgents; i++)
        {
            Assert.True(s.Desk.AddAgent("Agent " + i).Ok);
        }

        Assert.False(s.Desk.AddAgent("One too many").Ok);
    }

    [Fact]
    public void Stats_show_each_agents_load_and_the_queue()
    {
        var s = new Shop(capacity: 2);
        for (var i = 0; i < 5; i++)
        {
            s.New("Ticket " + i);
        }

        var stats = s.Desk.Stats();

        Assert.Equal(1, stats.Unassigned);
        Assert.Equal([2, 2], stats.Agents.Select(a => a.Open));
        Assert.All(stats.Agents, a => Assert.Equal(2, a.Capacity));
    }
}
