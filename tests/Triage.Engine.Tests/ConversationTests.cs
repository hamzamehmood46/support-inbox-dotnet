using Triage.Engine;

namespace Triage.Engine.Tests;

public class ReplyTests
{
    private readonly Shop _s = new();

    [Fact]
    public void The_first_public_reply_is_the_first_response_and_opens_the_ticket()
    {
        var t = _s.New();
        _s.Hours(1);

        var replied = _s.Reply(t);

        Assert.Equal(Status.Open, replied.Status);
        Assert.Equal(Shop.At(10, 5, 11), replied.FirstResponseAt);
        Assert.Equal((2, Author.Agent, "Ana"), (replied.Messages[1].Seq, replied.Messages[1].Author, replied.Messages[1].By));
    }

    [Fact]
    public void Later_replies_do_not_change_the_first_response_time()
    {
        var t = _s.New();
        _s.Hours(1);
        _s.Reply(t);
        _s.Hours(2);

        var again = _s.Reply(t, text: "Another update.");

        Assert.Equal(Shop.At(10, 5, 11), again.FirstResponseAt);
        Assert.Equal(3, again.Messages.Count);
    }

    [Fact]
    public void An_internal_note_is_not_a_response_and_changes_nothing_else()
    {
        var t = _s.New();

        var noted = _s.Reply(t, isPublic: false, text: "Customer is on the enterprise plan.");

        Assert.Null(noted.FirstResponseAt);
        Assert.Equal(Status.New, noted.Status);
        Assert.Equal(Author.Note, noted.Messages[1].Author);
    }

    [Fact]
    public void A_note_cannot_change_the_status()
    {
        var t = _s.New();

        var result = _s.Desk.Reply(t.Id, _s.Ana.Id, "x", isPublic: false, setStatus: Status.Solved);

        Assert.False(result.Ok);
        Assert.Equal(Status.New, _s.Get(t).Status);
    }

    [Fact]
    public void Replying_to_an_unassigned_ticket_takes_ownership_of_it()
    {
        var full = new Shop(capacity: 1);
        full.New("One");
        full.New("Two");
        var third = full.New("Three");
        Assert.Null(third.AssigneeId);

        var replied = full.Reply(third, full.Ben);

        Assert.Equal(full.Ben.Id, replied.AssigneeId);
    }

    [Fact]
    public void An_agent_who_is_away_does_not_take_ownership_by_replying()
    {
        _s.Desk.SetAvailable(_s.Ana.Id, false);
        _s.Desk.SetAvailable(_s.Ben.Id, false);
        var t = _s.New();

        var replied = _s.Reply(t, _s.Ben);

        Assert.Null(replied.AssigneeId);
    }

    [Fact]
    public void Replying_to_a_ticket_someone_else_owns_does_not_take_it_over()
    {
        var t = _s.New();

        var replied = _s.Reply(t, _s.Ben);

        Assert.Equal(t.AssigneeId, replied.AssigneeId);
    }

    [Fact]
    public void Replying_with_pending_waits_on_the_customer_and_pauses_the_clock()
    {
        var t = _s.New();

        var pending = _s.Reply(t, status: Status.Pending);

        Assert.Equal(Status.Pending, pending.Status);
        Assert.Equal(Shop.Start, pending.PendingSince);
        Assert.Equal(SlaState.Paused, _s.Sla(t).Resolution);
    }

    [Fact]
    public void Replying_with_solved_solves_the_ticket()
    {
        var t = _s.New();

        var solved = _s.Reply(t, status: Status.Solved);

        Assert.Equal(Status.Solved, solved.Status);
        Assert.Equal(Shop.Start, solved.SolvedAt);
    }

    [Fact]
    public void Replying_with_open_on_a_pending_ticket_takes_it_back_and_restarts_the_clock()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Pending);
        _s.Hours(2);

        var reopened = _s.Reply(t, status: Status.Open, text: "Actually, one more thing.");

        Assert.Equal(Status.Open, reopened.Status);
        Assert.Null(reopened.PendingSince);
        Assert.Equal(TimeSpan.FromHours(2), reopened.PausedBusiness);
    }

    [Theory]
    [InlineData(Status.New)]
    [InlineData(Status.Closed)]
    public void A_reply_cannot_set_the_other_statuses(Status status)
    {
        var t = _s.New();

        Assert.False(_s.Desk.Reply(t.Id, _s.Ana.Id, "x", true, status).Ok);
    }

    [Fact]
    public void A_solved_ticket_can_get_a_closing_reply_but_not_be_made_pending_without_reopening()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Solved);

        var thanks = _s.Reply(t, text: "Glad that worked.");
        var refused = _s.Desk.Reply(t.Id, _s.Ana.Id, "x", true, Status.Pending);

        Assert.Equal(Status.Solved, thanks.Status);
        Assert.False(refused.Ok);
        Assert.Contains("Reopen", refused.Message);
    }

    [Fact]
    public void A_closed_ticket_takes_no_replies_or_notes()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Solved);
        _s.Days(4);
        _s.Desk.RunAutomation();

        Assert.False(_s.Desk.Reply(t.Id, _s.Ana.Id, "x").Ok);
        Assert.False(_s.Desk.Reply(t.Id, _s.Ana.Id, "x", false).Ok);
    }

    [Fact]
    public void Replies_need_text_a_real_ticket_and_a_real_agent()
    {
        var t = _s.New();

        Assert.False(_s.Desk.Reply(t.Id, _s.Ana.Id, " ").Ok);
        Assert.False(_s.Desk.Reply(t.Id, _s.Ana.Id, new string('x', Desk.MaxText + 1)).Ok);
        Assert.False(_s.Desk.Reply(Guid.NewGuid(), _s.Ana.Id, "x").Ok);
        Assert.False(_s.Desk.Reply(t.Id, Guid.NewGuid(), "x").Ok);
    }

    [Fact]
    public void Message_numbers_stay_in_sequence()
    {
        var t = _s.New();
        _s.Reply(t);
        _s.Customer(t);
        _s.Reply(t, isPublic: false, text: "note");

        Assert.Equal([1, 2, 3, 4], _s.Get(t).Messages.Select(m => m.Seq));
    }
}

public class CustomerReplyTests
{
    private readonly Shop _s = new();

    [Fact]
    public void A_message_on_an_open_ticket_is_added_and_the_status_stays()
    {
        var t = _s.New();
        _s.Reply(t);

        var after = _s.Customer(t, "Thanks, still not working.");

        Assert.Equal(Status.Open, after.Status);
        Assert.Equal(Author.Customer, after.Messages[^1].Author);
    }

    [Fact]
    public void A_message_on_a_new_ticket_keeps_it_new()
    {
        var t = _s.New();

        Assert.Equal(Status.New, _s.Customer(t).Status);
    }

    [Fact]
    public void A_reply_on_a_pending_ticket_opens_it_and_the_wait_stops_counting_against_the_team()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Pending);
        _s.Hours(3);

        var after = _s.Customer(t);

        Assert.Equal(Status.Open, after.Status);
        Assert.Null(after.PendingSince);
        Assert.Equal(TimeSpan.FromHours(3), after.PausedBusiness);
    }

    [Fact]
    public void A_reply_on_a_solved_ticket_reopens_it_and_restarts_the_resolution_clock()
    {
        var t = _s.New(priority: Priority.High);
        _s.Reply(t, status: Status.Solved);
        _s.Days(1);

        var after = _s.Customer(t, "It broke again.");

        Assert.Equal(Status.Open, after.Status);
        Assert.Equal(1, after.Reopens);
        Assert.Null(after.SolvedAt);
        Assert.Equal(Shop.At(10, 6, 10), after.SlaStart);
        Assert.Equal(Shop.At(10, 7, 10), after.ResolutionDue);
        Assert.Equal(TimeSpan.Zero, after.PausedBusiness);
    }

    [Fact]
    public void A_reopened_ticket_keeps_its_original_first_response()
    {
        var t = _s.New();
        _s.Hours(1);
        _s.Reply(t, status: Status.Solved);
        _s.Days(1);

        var after = _s.Customer(t);

        Assert.Equal(Shop.At(10, 5, 11), after.FirstResponseAt);
    }

    [Fact]
    public void A_reply_on_a_closed_ticket_opens_a_follow_up_that_points_back_to_it()
    {
        var t = _s.New("Printer on fire", tags: ["hardware"], priority: Priority.High);
        _s.Reply(t, status: Status.Solved);
        _s.Days(4);
        _s.Desk.RunAutomation();

        var follow = _s.Customer(t, "It is on fire again.");

        Assert.NotEqual(t.Id, follow.Id);
        Assert.Equal(t.Id, follow.FollowUpOf);
        Assert.Equal("Re: Printer on fire", follow.Subject);
        Assert.Equal(["hardware"], follow.Tags);
        Assert.Equal(Priority.High, follow.Priority);
        Assert.Equal(Status.New, follow.Status);
        Assert.Equal("cara@example.com", follow.Requester);
        Assert.Equal(Status.Closed, _s.Get(t).Status);
    }

    [Fact]
    public void A_follow_up_of_a_follow_up_does_not_pile_up_prefixes()
    {
        var t = _s.New("Printer on fire");
        _s.Reply(t, status: Status.Solved);
        _s.Days(4);
        _s.Desk.RunAutomation();
        var first = _s.Customer(t);
        _s.Reply(first, status: Status.Solved);
        _s.Days(4);
        _s.Desk.RunAutomation();

        var second = _s.Customer(first);

        Assert.Equal("Re: Printer on fire", second.Subject);
    }

    [Fact]
    public void A_reply_to_a_merged_ticket_lands_on_the_ticket_it_was_merged_into()
    {
        var keep = _s.New("Login problem");
        var duplicate = _s.New("Login problem again");
        _s.Desk.Merge(duplicate.Id, keep.Id);

        var after = _s.Customer(duplicate, "Any update?");

        Assert.Equal(keep.Id, after.Id);
        Assert.Equal("Any update?", after.Messages[^1].Text);
    }

    [Fact]
    public void A_reply_to_a_ticket_merged_into_a_closed_one_opens_a_follow_up_of_the_closed_one()
    {
        var keep = _s.New("Login problem");
        var duplicate = _s.New("Login problem again");
        _s.Desk.Merge(duplicate.Id, keep.Id);
        _s.Reply(keep, status: Status.Solved);
        _s.Days(4);
        _s.Desk.RunAutomation();

        var follow = _s.Customer(duplicate, "Still broken.");

        Assert.Equal(keep.Id, follow.FollowUpOf);
    }

    [Fact]
    public void A_customer_message_needs_text_and_a_real_ticket()
    {
        var t = _s.New();

        Assert.False(_s.Desk.CustomerReply(t.Id, " ").Ok);
        Assert.False(_s.Desk.CustomerReply(t.Id, new string('x', Desk.MaxText + 1)).Ok);
        Assert.False(_s.Desk.CustomerReply(Guid.NewGuid(), "hello").Ok);
    }

    [Fact]
    public void A_reopened_ticket_goes_back_to_the_queue_if_its_agent_is_gone()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Solved);
        _s.Desk.SetAvailable(_s.Ana.Id, false);
        _s.Desk.SetAvailable(_s.Ben.Id, false);

        var after = _s.Customer(t);

        Assert.Equal(Status.Open, after.Status);
        Assert.Null(after.AssigneeId);
        Assert.Equal(1, _s.Desk.Stats().Unassigned);
    }

    [Fact]
    public void A_customer_reply_on_a_pending_ticket_whose_agent_is_away_sends_it_to_someone_else()
    {
        var t = _s.New();
        _s.Reply(t, status: Status.Pending);
        var owner = _s.Get(t).AssigneeId!.Value;
        var other = owner == _s.Ana.Id ? _s.Ben : _s.Ana;
        _s.Desk.SetAvailable(owner, false);

        var after = _s.Customer(t);

        Assert.Equal(other.Id, after.AssigneeId);
    }
}

public class MergeTests
{
    private readonly Shop _s = new();

    [Fact]
    public void Merging_combines_the_conversations_in_time_order_and_closes_the_duplicate()
    {
        var keep = _s.New("Login problem");
        _s.Hours(1);
        var duplicate = _s.New("Login problem again", body: "Same issue, second email.");
        _s.Hours(1);
        _s.Customer(keep, "Third message on the first ticket.");

        var result = _s.Desk.Merge(duplicate.Id, keep.Id);

        Assert.True(result.Ok);
        var merged = _s.Get(keep);
        Assert.Equal(["It takes several minutes to export.", "Same issue, second email.", "Third message on the first ticket.", "Merged T-0002 into this ticket."], merged.Messages.Select(m => m.Text));
        Assert.Equal([1, 2, 3, 4], merged.Messages.Select(m => m.Seq));
        var closed = _s.Get(duplicate);
        Assert.Equal(Status.Closed, closed.Status);
        Assert.Equal(keep.Id, closed.MergedInto);
        Assert.NotNull(closed.ClosedAt);
        Assert.Null(closed.AssigneeId);
    }

    [Fact]
    public void The_earliest_first_response_of_the_two_counts()
    {
        var keep = _s.New("Login problem");
        var duplicate = _s.New("Login problem again");
        _s.Hours(1);
        _s.Reply(duplicate);
        _s.Hours(1);
        _s.Reply(keep);

        _s.Desk.Merge(duplicate.Id, keep.Id);

        Assert.Equal(Shop.At(10, 5, 11), _s.Get(keep).FirstResponseAt);
    }

    [Fact]
    public void The_target_gains_a_first_response_when_only_the_duplicate_had_one()
    {
        var keep = _s.New("Login problem");
        var duplicate = _s.New("Login problem again");
        _s.Reply(duplicate);

        _s.Desk.Merge(duplicate.Id, keep.Id);

        Assert.NotNull(_s.Get(keep).FirstResponseAt);
    }

    [Fact]
    public void Tags_are_combined_up_to_the_limit()
    {
        var keep = _s.New("Ticket A", tags: ["a1", "a2", "a3", "a4", "a5", "shared"]);
        var duplicate = _s.New("Ticket B", tags: ["b1", "b2", "b3", "b4", "b5", "shared"]);

        _s.Desk.Merge(duplicate.Id, keep.Id);

        Assert.Equal(Desk.MaxTags, _s.Get(keep).Tags.Count);
        Assert.Equal(1, _s.Get(keep).Tags.Count(t => t == "shared"));
    }

    [Fact]
    public void A_pending_target_wakes_up_when_the_customer_has_written_in_again()
    {
        var keep = _s.New("Login problem");
        _s.Reply(keep, status: Status.Pending);
        _s.Hours(2);
        var duplicate = _s.New("Login problem again");

        _s.Desk.Merge(duplicate.Id, keep.Id);

        var merged = _s.Get(keep);
        Assert.Equal(Status.Open, merged.Status);
        Assert.Null(merged.PendingSince);
        Assert.Equal(TimeSpan.FromHours(2), merged.PausedBusiness);
    }

    [Fact]
    public void A_solved_target_is_reopened_by_an_active_duplicate()
    {
        var keep = _s.New("Login problem");
        _s.Reply(keep, status: Status.Solved);
        var duplicate = _s.New("Login problem again");

        _s.Desk.Merge(duplicate.Id, keep.Id);

        var merged = _s.Get(keep);
        Assert.Equal(Status.Open, merged.Status);
        Assert.Equal(1, merged.Reopens);
        Assert.Null(merged.SolvedAt);
    }

    [Fact]
    public void A_solved_duplicate_does_not_reopen_an_active_target()
    {
        var keep = _s.New("Login problem");
        var duplicate = _s.New("Login problem again");
        _s.Reply(duplicate, status: Status.Solved);

        _s.Desk.Merge(duplicate.Id, keep.Id);

        Assert.Equal(0, _s.Get(keep).Reopens);
        Assert.Equal(Status.New, _s.Get(keep).Status);
    }

    [Fact]
    public void Only_tickets_from_the_same_person_can_be_merged_and_case_does_not_matter()
    {
        var a = _s.New("Ticket A", "cara@example.com");
        var b = _s.New("Ticket B", "CARA@example.com");
        var other = _s.New("Ticket C", "dan@example.com");

        Assert.False(_s.Desk.Merge(other.Id, a.Id).Ok);
        Assert.True(_s.Desk.Merge(b.Id, a.Id).Ok);
    }

    [Fact]
    public void A_ticket_cannot_be_merged_into_itself_or_into_something_that_does_not_exist()
    {
        var a = _s.New("Ticket A");

        Assert.False(_s.Desk.Merge(a.Id, a.Id).Ok);
        Assert.False(_s.Desk.Merge(a.Id, Guid.NewGuid()).Ok);
        Assert.False(_s.Desk.Merge(Guid.NewGuid(), a.Id).Ok);
    }

    [Fact]
    public void Closed_and_already_merged_tickets_cannot_be_merged()
    {
        var a = _s.New("Ticket A");
        var b = _s.New("Ticket B");
        var c = _s.New("Ticket C");
        _s.Desk.Merge(b.Id, a.Id);

        Assert.False(_s.Desk.Merge(b.Id, c.Id).Ok);
        Assert.False(_s.Desk.Merge(c.Id, b.Id).Ok);
    }

    [Fact]
    public void A_merged_ticket_no_longer_counts_toward_service_levels_or_workload()
    {
        var keep = _s.New("Login problem");
        var duplicate = _s.New("Login problem again");
        _s.Days(3);

        _s.Desk.Merge(duplicate.Id, keep.Id);

        var sla = _s.Sla(duplicate);
        Assert.Equal((SlaState.NotApplicable, SlaState.NotApplicable), (sla.FirstResponse, sla.Resolution));
        Assert.Equal(1, _s.Desk.Stats().Active);
        Assert.Equal(1, _s.Desk.Stats().Agents.Sum(a => a.Open));
    }
}
