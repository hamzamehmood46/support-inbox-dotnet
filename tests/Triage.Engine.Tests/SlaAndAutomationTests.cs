using Triage.Engine;

namespace Triage.Engine.Tests;

public class FirstResponseSlaTests
{
    // A Normal ticket created Monday 10:00 has 8 working hours to get a first reply: until Tuesday 10:00.
    private readonly Shop _s = new();

    [Theory]
    [InlineData(5, 15, 59, SlaState.Ok)]
    [InlineData(5, 16, 0, SlaState.AtRisk)]
    [InlineData(6, 9, 59, SlaState.AtRisk)]
    [InlineData(6, 10, 0, SlaState.AtRisk)]
    [InlineData(6, 10, 1, SlaState.Breached)]
    [InlineData(8, 12, 0, SlaState.Breached)]
    public void An_unanswered_ticket_moves_from_ok_to_at_risk_at_three_quarters_and_to_breached_after_the_deadline(int day, int hour, int minute, SlaState expected)
    {
        var t = _s.New();

        _s.GoTo(Shop.At(10, day, hour, minute));

        Assert.Equal(expected, _s.Sla(t).FirstResponse);
    }

    [Fact]
    public void Nights_and_weekends_do_not_use_up_the_time()
    {
        _s.GoTo(Shop.At(10, 9, 16));
        var t = _s.New();

        _s.GoTo(Shop.At(10, 11, 12));
        Assert.Equal(SlaState.Ok, _s.Sla(t).FirstResponse);

        Assert.Equal(Shop.At(10, 12, 16), _s.Get(t).FirstResponseDue);
        _s.GoTo(Shop.At(10, 12, 15, 59));
        Assert.Equal(SlaState.AtRisk, _s.Sla(t).FirstResponse);
        _s.GoTo(Shop.At(10, 12, 16, 1));
        Assert.Equal(SlaState.Breached, _s.Sla(t).FirstResponse);
    }

    [Fact]
    public void A_reply_in_time_meets_the_target_and_a_late_one_does_not()
    {
        var quick = _s.New("Quick");
        var slow = _s.New("Slow");
        _s.Hours(2);
        _s.Reply(quick);
        _s.GoTo(Shop.At(10, 6, 11));
        _s.Reply(slow);

        Assert.Equal(SlaState.Met, _s.Sla(quick).FirstResponse);
        Assert.Equal(SlaState.Breached, _s.Sla(slow).FirstResponse);
    }

    [Fact]
    public void A_reply_exactly_at_the_deadline_still_counts()
    {
        var t = _s.New();
        _s.GoTo(Shop.At(10, 6, 10));

        _s.Reply(t);

        Assert.Equal(SlaState.Met, _s.Sla(t).FirstResponse);
    }

    [Fact]
    public void An_internal_note_does_not_stop_the_first_response_clock()
    {
        var t = _s.New();
        _s.Reply(t, isPublic: false, text: "Looking at logs.");

        _s.GoTo(Shop.At(10, 6, 11));

        Assert.Equal(SlaState.Breached, _s.Sla(t).FirstResponse);
    }

    [Fact]
    public void Higher_priorities_have_less_time()
    {
        var urgent = _s.New("Urgent", priority: Priority.Urgent);
        var low = _s.New("Low", priority: Priority.Low);

        _s.GoTo(Shop.At(10, 5, 11, 1));

        Assert.Equal(SlaState.Breached, _s.Sla(urgent).FirstResponse);
        Assert.Equal(SlaState.Ok, _s.Sla(low).FirstResponse);
    }

    [Fact]
    public void The_time_left_counts_down_in_working_time_and_goes_negative_once_overdue()
    {
        var t = _s.New();
        _s.Hours(2);
        Assert.Equal(TimeSpan.FromHours(6), _s.Sla(t).FirstResponseRemaining);

        _s.GoTo(Shop.At(10, 6, 12));
        Assert.Equal(TimeSpan.FromHours(-2), _s.Sla(t).FirstResponseRemaining);
    }

    [Fact]
    public void Once_answered_there_is_no_first_response_countdown()
    {
        var t = _s.New();
        _s.Reply(t);

        Assert.Null(_s.Sla(t).FirstResponseRemaining);
    }

    [Fact]
    public void Changing_the_priority_moves_the_first_response_deadline()
    {
        var t = _s.New();
        _s.Hours(2);

        _s.Desk.SetPriority(t.Id, Priority.Urgent);

        Assert.Equal(Shop.At(10, 5, 11), _s.Get(t).FirstResponseDue);
        Assert.Equal(SlaState.Breached, _s.Sla(t).FirstResponse);
    }
}

public class ResolutionSlaTests
{
    // A Normal ticket created Monday 10:00 has 24 working hours to be solved: until Thursday 10:00.
    private readonly Shop _s = new();

    [Theory]
    [InlineData(5, 11, 0, SlaState.Ok)]
    [InlineData(7, 11, 59, SlaState.Ok)]
    [InlineData(7, 12, 0, SlaState.AtRisk)]
    [InlineData(8, 10, 0, SlaState.AtRisk)]
    [InlineData(8, 10, 1, SlaState.Breached)]
    public void An_open_ticket_moves_from_ok_to_at_risk_to_breached(int day, int hour, int minute, SlaState expected)
    {
        var t = _s.New();
        _s.Reply(t);

        _s.GoTo(Shop.At(10, day, hour, minute));

        Assert.Equal(expected, _s.Sla(t).Resolution);
    }

    [Fact]
    public void Solving_in_time_meets_the_target_and_solving_late_breaches_it()
    {
        var early = _s.New("Early");
        var late = _s.New("Late");
        _s.GoTo(Shop.At(10, 7, 15));
        _s.Desk.Solve(early.Id);
        _s.GoTo(Shop.At(10, 8, 10, 1));
        _s.Desk.Solve(late.Id);

        Assert.Equal(SlaState.Met, _s.Sla(early).Resolution);
        Assert.Equal(SlaState.Breached, _s.Sla(late).Resolution);
    }

    [Fact]
    public void Solving_exactly_at_the_deadline_counts_as_met()
    {
        var t = _s.New();
        _s.GoTo(Shop.At(10, 8, 10));

        _s.Desk.Solve(t.Id);

        Assert.Equal(SlaState.Met, _s.Sla(t).Resolution);
    }

    [Fact]
    public void The_time_left_to_solve_counts_down_and_goes_negative_when_overdue()
    {
        var t = _s.New();
        Assert.Equal(TimeSpan.FromHours(24), _s.Sla(t).ResolutionRemaining);

        _s.GoTo(Shop.At(10, 8, 11));
        Assert.Equal(TimeSpan.FromHours(-1), _s.Sla(t).ResolutionRemaining);
    }

    [Fact]
    public void While_waiting_on_the_customer_the_clock_is_paused_and_no_countdown_is_shown()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Pending);

        _s.GoTo(Shop.At(10, 9, 12));

        var sla = _s.Sla(t);
        Assert.Equal(SlaState.Paused, sla.Resolution);
        Assert.Null(sla.ResolutionRemaining);
    }

    [Fact]
    public void The_deadline_moves_out_by_the_working_time_spent_waiting()
    {
        var t = _s.New();
        _s.GoTo(Shop.At(10, 6, 10));
        _s.Reply(t, status: Status.Pending);
        _s.GoTo(Shop.At(10, 7, 10));

        var after = _s.Customer(t);

        // Tuesday 10:00 to Wednesday 10:00 is 8 working hours (7 on Tuesday, 1 on Wednesday).
        Assert.Equal(TimeSpan.FromHours(8), after.PausedBusiness);
        Assert.Equal(Shop.At(10, 9, 10), after.ResolutionDue);
        Assert.Equal(SlaState.Ok, _s.Sla(t).Resolution);
    }

    [Fact]
    public void A_wait_over_a_weekend_only_counts_the_working_time_in_it()
    {
        var t = _s.New();
        _s.GoTo(Shop.At(10, 9, 16));
        _s.Reply(t, status: Status.Pending);
        _s.GoTo(Shop.At(10, 12, 10));

        var after = _s.Customer(t);

        Assert.Equal(TimeSpan.FromHours(2), after.PausedBusiness);
    }

    [Fact]
    public void Several_waits_add_up()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Pending);
        _s.Hours(2);
        _s.Customer(t);
        _s.Hours(1);
        _s.Reply(t, status: Status.Pending);
        _s.Hours(3);

        var after = _s.Customer(t);

        Assert.Equal(TimeSpan.FromHours(5), after.PausedBusiness);
        Assert.Equal(Shop.At(10, 8, 15), after.ResolutionDue);
    }

    [Fact]
    public void Solving_while_waiting_ends_the_pause_and_can_still_meet_the_target()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Pending);
        _s.Days(2);

        _s.Desk.Solve(t.Id);

        var solved = _s.Get(t);
        Assert.Null(solved.PendingSince);
        Assert.Equal(SlaState.Met, _s.Sla(t).Resolution);
    }

    [Fact]
    public void Reopening_starts_a_fresh_resolution_clock()
    {
        var t = _s.New();
        _s.Reply(t);
        _s.Desk.Solve(t.Id);
        _s.GoTo(Shop.At(10, 9, 10));

        _s.Customer(t, "It is back.");

        var after = _s.Get(t);
        Assert.Equal(Shop.At(10, 9, 10), after.SlaStart);
        Assert.Equal(Shop.At(10, 14, 10), after.ResolutionDue);
        Assert.Equal(SlaState.Ok, _s.Sla(t).Resolution);
    }

    [Fact]
    public void An_agent_can_reopen_a_solved_ticket_and_the_same_rules_apply()
    {
        var t = _s.New();
        _s.Desk.Solve(t.Id);
        _s.Hours(1);

        var result = _s.Desk.Reopen(t.Id);

        Assert.True(result.Ok);
        Assert.Equal(Status.Open, _s.Get(t).Status);
        Assert.Equal(1, _s.Get(t).Reopens);
        Assert.Equal(Shop.At(10, 5, 11), _s.Get(t).SlaStart);
    }

    [Fact]
    public void Only_a_solved_ticket_can_be_reopened()
    {
        var open = _s.New();
        var closed = _s.New("Closed one");
        _s.Desk.Solve(closed.Id);
        _s.Days(4);
        _s.Desk.RunAutomation();

        Assert.False(_s.Desk.Reopen(open.Id).Ok);
        var refused = _s.Desk.Reopen(closed.Id);
        Assert.False(refused.Ok);
        Assert.Contains("follow-up", refused.Message);
        Assert.False(_s.Desk.Reopen(Guid.NewGuid()).Ok);
    }

    [Fact]
    public void Changing_the_priority_recomputes_the_deadline_from_when_the_clock_started_and_keeps_the_pauses()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Pending);
        _s.Hours(2);
        _s.Customer(t);

        _s.Desk.SetPriority(t.Id, Priority.High);

        // 8 working hours from Monday 10:00, plus the 2 hours spent waiting.
        Assert.Equal(Shop.At(10, 6, 12), _s.Get(t).ResolutionDue);
    }

    [Fact]
    public void Priority_cannot_be_changed_once_a_ticket_is_solved_and_unknown_tickets_are_refused()
    {
        var t = _s.New();
        _s.Desk.Solve(t.Id);

        Assert.False(_s.Desk.SetPriority(t.Id, Priority.Urgent).Ok);
        Assert.False(_s.Desk.SetPriority(Guid.NewGuid(), Priority.Urgent).Ok);
    }

    [Fact]
    public void Solving_twice_or_an_unknown_ticket_is_refused()
    {
        var t = _s.New();
        _s.Desk.Solve(t.Id);

        Assert.False(_s.Desk.Solve(t.Id).Ok);
        Assert.False(_s.Desk.Solve(Guid.NewGuid()).Ok);
    }

    [Fact]
    public void A_custom_policy_and_at_risk_share_are_respected()
    {
        var policy = new SlaPolicy(Enum.GetValues<Priority>().ToDictionary(p => p, _ => new SlaTarget(TimeSpan.FromHours(2), TimeSpan.FromHours(4))));
        var s = new Shop(options: new DeskOptions { AtRiskFraction = 0.5 }, sla: policy);
        var t = s.New();

        s.Hours(1);
        Assert.Equal(SlaState.AtRisk, s.Sla(t).FirstResponse);
        Assert.Equal(Shop.At(10, 5, 12), s.Get(t).FirstResponseDue);
        Assert.Equal(Shop.At(10, 5, 14), s.Get(t).ResolutionDue);
    }
}

public class AutomationTests
{
    private readonly Shop _s = new();

    [Fact]
    public void A_pending_ticket_is_solved_after_seven_days_of_silence_and_not_before()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Pending);

        _s.GoTo(Shop.Start + TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        Assert.Equal(0, _s.Desk.RunAutomation().AutoSolved);
        Assert.Equal(Status.Pending, _s.Get(t).Status);

        _s.Hours(1d / 60);
        var report = _s.Desk.RunAutomation();

        Assert.Equal(1, report.AutoSolved);
        Assert.Equal(Status.Solved, _s.Get(t).Status);
        Assert.Contains(_s.Get(t).Messages, m => m.Author == Author.Note && m.Text.Contains("automatically"));
    }

    [Fact]
    public void A_solved_ticket_is_closed_after_three_days_and_not_before()
    {
        var t = _s.New();
        _s.Desk.Solve(t.Id);

        _s.GoTo(Shop.Start + TimeSpan.FromDays(3) - TimeSpan.FromMinutes(1));
        Assert.Equal(0, _s.Desk.RunAutomation().AutoClosed);

        _s.Hours(1d / 60);
        var report = _s.Desk.RunAutomation();

        Assert.Equal(1, report.AutoClosed);
        Assert.Equal(Status.Closed, _s.Get(t).Status);
        Assert.NotNull(_s.Get(t).ClosedAt);
    }

    [Fact]
    public void A_customer_reply_stops_the_auto_solve()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Pending);
        _s.Days(6);
        _s.Customer(t);

        _s.Days(5);
        var report = _s.Desk.RunAutomation();

        Assert.Equal(0, report.AutoSolved);
        Assert.Equal(Status.Open, _s.Get(t).Status);
    }

    [Fact]
    public void A_customer_reply_before_the_auto_close_reopens_the_ticket_instead()
    {
        var t = _s.New();
        _s.Desk.Solve(t.Id);
        _s.Days(2);
        _s.Customer(t, "Wait, not fixed.");

        _s.Days(2);
        var report = _s.Desk.RunAutomation();

        Assert.Equal(0, report.AutoClosed);
        Assert.Equal(Status.Open, _s.Get(t).Status);
    }

    [Fact]
    public void An_auto_solved_ticket_is_then_closed_three_days_later()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Pending);
        _s.Days(7);
        _s.Desk.RunAutomation();

        _s.Days(3);
        _s.Desk.RunAutomation();

        Assert.Equal(Status.Closed, _s.Get(t).Status);
    }

    [Fact]
    public void A_ticket_that_misses_its_first_response_deadline_is_escalated_once()
    {
        var t = _s.New();
        _s.GoTo(Shop.At(10, 6, 10, 1));

        var first = _s.Desk.RunAutomation();
        var second = _s.Desk.RunAutomation();

        Assert.Equal(1, first.Escalated);
        Assert.Equal(0, second.Escalated);
        Assert.True(_s.Get(t).Escalated);
        Assert.Single(_s.Get(t).Messages, m => m.Text.StartsWith("Escalated"));
    }

    [Fact]
    public void A_ticket_that_is_answered_but_not_solved_in_time_is_escalated_too()
    {
        var t = _s.New();
        _s.Reply(t);
        _s.GoTo(Shop.At(10, 8, 10, 1));

        Assert.Equal(1, _s.Desk.RunAutomation().Escalated);
        Assert.True(_s.Get(t).Escalated);
    }

    [Fact]
    public void A_ticket_within_its_targets_is_left_alone()
    {
        var t = _s.New();
        _s.Reply(t);
        _s.Hours(3);

        Assert.Equal(0, _s.Desk.RunAutomation().Escalated);
        Assert.False(_s.Get(t).Escalated);
    }

    [Fact]
    public void Waiting_on_the_customer_is_never_a_breach()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Pending);
        _s.Days(5);

        Assert.Equal(0, _s.Desk.RunAutomation().Escalated);
        Assert.False(_s.Get(t).Escalated);
    }

    [Fact]
    public void Solved_tickets_are_not_escalated_even_if_they_were_late()
    {
        var t = _s.New();
        _s.GoTo(Shop.At(10, 9, 12));
        _s.Desk.Solve(t.Id);

        Assert.Equal(0, _s.Desk.RunAutomation().Escalated);
    }

    [Fact]
    public void Escalated_tickets_are_listed_first_whatever_their_deadline()
    {
        var late = _s.New("Late one");
        _s.GoTo(Shop.At(10, 6, 11));
        var soon = _s.New("Due soon", priority: Priority.Urgent);
        _s.Desk.RunAutomation();

        var listed = _s.Desk.Tickets().Select(t => t.Id);

        Assert.Equal([late.Id, soon.Id], listed);
    }

    [Fact]
    public void The_report_counts_everything_that_happened()
    {
        var pending = _s.New("Pending");
        _s.Reply(pending, status: Status.Pending);
        var solved = _s.New("Solved");
        _s.Desk.Solve(solved.Id);
        _s.Days(8);
        var breached = _s.New("Breached", priority: Priority.Urgent);
        _s.Days(1);

        var report = _s.Desk.RunAutomation();

        Assert.Equal(1, report.AutoSolved);
        Assert.Equal(1, report.AutoClosed);
        Assert.Equal(1, report.Escalated);
        Assert.True(report.DidAnything);
        Assert.Equal(Status.Closed, _s.Get(solved).Status);
        Assert.True(_s.Get(breached).Escalated);
    }

    [Fact]
    public void Running_it_twice_in_a_row_does_nothing_the_second_time()
    {
        _s.New();
        _s.Days(10);
        _s.Desk.RunAutomation();

        var again = _s.Desk.RunAutomation();

        Assert.False(again.DidAnything);
    }

    [Fact]
    public void The_auto_times_can_be_changed()
    {
        var s = new Shop(options: new DeskOptions { PendingAutoSolveAfter = TimeSpan.FromDays(1), SolvedAutoCloseAfter = TimeSpan.FromHours(6) });
        var t = s.New();
        s.Reply(t, status: Status.Pending);

        s.Days(1);
        s.Desk.RunAutomation();
        Assert.Equal(Status.Solved, s.Get(t).Status);

        s.Hours(6);
        s.Desk.RunAutomation();
        Assert.Equal(Status.Closed, s.Get(t).Status);
    }
}
