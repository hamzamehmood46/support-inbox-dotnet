using Triage.Engine;

namespace Triage.Engine.Tests;

public class QueryTests
{
    private readonly Shop _s = new();

    [Fact]
    public void Active_tickets_come_first_ordered_by_the_soonest_deadline()
    {
        var normal = _s.New("Normal");
        var urgent = _s.New("Urgent", priority: Priority.Urgent);
        var high = _s.New("High", priority: Priority.High);

        Assert.Equal([urgent.Id, high.Id, normal.Id], _s.Desk.Tickets().Select(t => t.Id));
    }

    [Fact]
    public void Tickets_waiting_on_the_customer_sort_after_the_ones_the_team_must_act_on_and_finished_ones_come_last()
    {
        var pending = _s.New("Pending", priority: Priority.Urgent);
        _s.Reply(pending, status: Status.Pending);
        var open = _s.New("Open", priority: Priority.Low);
        var solved = _s.New("Solved", priority: Priority.Urgent);
        _s.Desk.Solve(solved.Id);

        Assert.Equal([open.Id, pending.Id, solved.Id], _s.Desk.Tickets().Select(t => t.Id));
    }

    [Fact]
    public void Filters_combine()
    {
        var a = _s.New("Alpha printer", "cara@example.com", tags: ["hardware"]);
        _s.New("Beta login", "dan@example.com", tags: ["account"]);
        _s.Desk.Solve(a.Id);

        Assert.Single(_s.Desk.Tickets(status: Status.Solved));
        Assert.Single(_s.Desk.Tickets(tag: "Hardware"));
        Assert.Single(_s.Desk.Tickets(assignee: _s.Get(a).AssigneeId, status: Status.Solved));
        Assert.Empty(_s.Desk.Tickets(tag: "hardware", status: Status.New));
    }

    [Fact]
    public void Unassigned_filters_the_queue()
    {
        _s.Desk.SetAvailable(_s.Ana.Id, false);
        _s.Desk.SetAvailable(_s.Ben.Id, false);
        _s.New("Waiting");

        Assert.Single(_s.Desk.Tickets(unassigned: true));
        Assert.Empty(_s.Desk.Tickets(unassigned: false));
    }

    [Fact]
    public void Search_looks_in_subject_requester_key_and_customer_or_agent_messages_but_not_internal_notes()
    {
        var t = _s.New("Printer jam", "cara@example.com", "Paper stuck inside");
        _s.Reply(t, isPublic: false, text: "secretword in a note");
        _s.Reply(t, text: "publicword for you");

        Assert.Single(_s.Desk.Tickets(search: "printer"));
        Assert.Single(_s.Desk.Tickets(search: "CARA@"));
        Assert.Single(_s.Desk.Tickets(search: "t-0001"));
        Assert.Single(_s.Desk.Tickets(search: "stuck"));
        Assert.Single(_s.Desk.Tickets(search: "publicword"));
        Assert.Empty(_s.Desk.Tickets(search: "secretword"));
    }

    [Fact]
    public void A_ticket_can_be_found_by_its_key_ignoring_case()
    {
        var t = _s.New();

        Assert.Equal(t.Id, _s.Desk.Ticket("t-0001")!.Id);
        Assert.Null(_s.Desk.Ticket("T-9999"));
        Assert.Null(_s.Desk.Ticket(Guid.NewGuid()));
    }

    [Fact]
    public void An_earlier_snapshot_does_not_change_when_the_ticket_does()
    {
        var t = _s.New();
        var before = _s.Get(t);

        _s.Reply(t);
        _s.Desk.AddTag(t.Id, "late-tag");

        Assert.Single(before.Messages);
        Assert.Empty(before.Tags);
        Assert.Equal(Status.New, before.Status);
    }

    [Fact]
    public void Tags_can_be_added_and_removed_with_checks()
    {
        var t = _s.New();

        Assert.True(_s.Desk.AddTag(t.Id, " VIP ").Ok);
        Assert.True(_s.Desk.AddTag(t.Id, "vip").Ok);
        Assert.Equal(["vip"], _s.Get(t).Tags);
        Assert.False(_s.Desk.AddTag(t.Id, "bad tag").Ok);
        Assert.False(_s.Desk.RemoveTag(t.Id, "nope").Ok);
        Assert.True(_s.Desk.RemoveTag(t.Id, "VIP").Ok);
        Assert.Empty(_s.Get(t).Tags);
        Assert.False(_s.Desk.AddTag(Guid.NewGuid(), "x").Ok);
    }

    [Fact]
    public void A_ticket_cannot_get_more_than_ten_tags_one_at_a_time()
    {
        var t = _s.New();
        for (var i = 0; i < Desk.MaxTags; i++)
        {
            Assert.True(_s.Desk.AddTag(t.Id, "t" + i).Ok);
        }

        Assert.False(_s.Desk.AddTag(t.Id, "extra").Ok);
    }

    [Fact]
    public void A_closed_ticket_cannot_be_tagged()
    {
        var t = _s.New();
        _s.Desk.Solve(t.Id);
        _s.Days(4);
        _s.Desk.RunAutomation();

        Assert.False(_s.Desk.AddTag(t.Id, "late").Ok);
    }
}

public class StatsTests
{
    [Fact]
    public void An_empty_desk_has_no_percentages_or_averages()
    {
        var stats = new Shop().Desk.Stats();

        Assert.Equal(0, stats.Active);
        Assert.Null(stats.SlaCompliancePercent);
        Assert.Null(stats.AverageFirstResponse);
        Assert.Equal(Enum.GetValues<Status>().Length, stats.ByStatus.Count);
    }

    [Fact]
    public void Compliance_is_the_share_of_solved_tickets_that_met_their_resolution_target()
    {
        var s = new Shop();
        var good = s.New("Good");
        var bad = s.New("Bad");
        s.Desk.Solve(good.Id);
        s.GoTo(Shop.At(10, 8, 11));
        s.Desk.Solve(bad.Id);

        Assert.Equal(50.0, s.Desk.Stats().SlaCompliancePercent);
        Assert.Equal(2, s.Desk.Stats().Solved);
    }

    [Fact]
    public void Compliance_still_counts_tickets_that_were_solved_and_then_closed()
    {
        var s = new Shop();
        var t = s.New();
        s.Desk.Solve(t.Id);
        s.Days(4);
        s.Desk.RunAutomation();

        Assert.Equal(100.0, s.Desk.Stats().SlaCompliancePercent);
    }

    [Fact]
    public void The_average_first_response_is_measured_in_working_time()
    {
        var s = new Shop();
        var a = s.New("Ticket A");
        var b = s.New("Ticket B");
        s.Hours(2);
        s.Reply(a);
        s.Hours(1);
        s.Reply(b);

        Assert.Equal(TimeSpan.FromHours(2.5), s.Desk.Stats().AverageFirstResponse);
    }

    [Fact]
    public void Merged_tickets_are_left_out_of_the_average()
    {
        var s = new Shop();
        var keep = s.New("Keep");
        var dup = s.New("Dup");
        s.Hours(1);
        s.Reply(keep);
        s.Desk.Merge(dup.Id, keep.Id);

        Assert.Equal(TimeSpan.FromHours(1), s.Desk.Stats().AverageFirstResponse);
    }

    [Fact]
    public void At_risk_and_breached_are_counted_among_active_tickets()
    {
        var s = new Shop();
        s.New("Fine");
        var risky = s.New("Risky", priority: Priority.High);
        var late = s.New("Late", priority: Priority.Urgent);
        s.GoTo(Shop.At(10, 5, 11, 1));

        var stats = s.Desk.Stats();

        Assert.Equal(3, stats.Active);
        Assert.Equal(1, stats.Breached);
        Assert.Equal(0, stats.AtRisk);
        _ = (risky, late);
    }
}

public class PropertyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void After_random_activity_the_desk_stays_consistent(int seed)
    {
        var random = new Random(seed);
        var s = new Shop(capacity: 3);
        var agents = new[] { s.Ana, s.Ben, s.Desk.AddAgent("Cleo", ["network"]).Value! };
        var emails = new[] { "a@x.com", "b@x.com", "c@x.com" };

        for (var step = 0; step < 400; step++)
        {
            var tickets = s.Desk.Tickets();
            var pick = tickets.Count == 0 ? null : tickets[random.Next(tickets.Count)];
            switch (random.Next(11))
            {
                case 0:
                case 1:
                    s.Desk.Create("Ticket " + step, emails[random.Next(emails.Length)], "body", (Priority)random.Next(4), random.Next(3) == 0 ? ["network"] : []);
                    break;
                case 2 when pick is not null:
                    s.Desk.Reply(pick.Id, agents[random.Next(3)].Id, "reply", random.Next(4) != 0, random.Next(3) switch { 0 => Status.Pending, 1 => Status.Solved, _ => null });
                    break;
                case 3 when pick is not null:
                    s.Desk.CustomerReply(pick.Id, "customer");
                    break;
                case 4:
                    s.Clock.Advance(TimeSpan.FromMinutes(random.Next(1, 600)));
                    break;
                case 5:
                    s.Desk.RunAutomation();
                    break;
                case 6 when pick is not null:
                    s.Desk.Merge(pick.Id, tickets[random.Next(tickets.Count)].Id);
                    break;
                case 7:
                    s.Desk.SetAvailable(agents[random.Next(3)].Id, random.Next(3) != 0);
                    break;
                case 8 when pick is not null:
                    s.Desk.SetPriority(pick.Id, (Priority)random.Next(4));
                    break;
                case 9 when pick is not null:
                    s.Desk.Reopen(pick.Id);
                    break;
                case 10 when pick is not null:
                    s.Desk.Solve(pick.Id);
                    break;
            }

            var all = s.Desk.Tickets();
            var live = s.Desk.Agents().ToDictionary(a => a.Id);
            foreach (var t in all)
            {
                Assert.Equal(Enumerable.Range(1, t.Messages.Count), t.Messages.Select(m => m.Seq));
                Assert.Equal(t.Status == Status.Pending, t.PendingSince is not null);
                if (t.Status == Status.Solved)
                {
                    Assert.NotNull(t.SolvedAt);
                }

                if (t.Status is Status.New or Status.Open or Status.Pending)
                {
                    Assert.Null(t.MergedInto);
                    Assert.True(t.ResolutionDue >= t.FirstResponseDue);
                }

                if (t.Status == Status.Closed && t.MergedInto is null)
                {
                    Assert.NotNull(t.ClosedAt);
                }

                if (t.MergedInto is { } into)
                {
                    Assert.Equal(Status.Closed, t.Status);
                    Assert.Contains(all, x => x.Id == into);
                }

                if (t.NeedsAgent && t.AssigneeId is { } owner)
                {
                    Assert.True(live[owner].Available, "open work is never held by someone who is away");
                }
            }

            Assert.Equal(all.Count, all.Select(t => t.Number).Distinct().Count());
            Assert.Equal(all.Count, s.Desk.Stats().ByStatus.Values.Sum());
        }
    }
}
