using Triage.Copilot;
using Triage.Engine;

namespace Triage.Copilot.Tests;

public class ApprovalTests
{
    [Fact]
    public async Task Asking_for_suggestions_changes_nothing_on_the_ticket()
    {
        var lab = new Lab();
        var t = lab.New("Charged twice", "Please refund one charge.");
        var before = lab.Get(t);

        Assert.True((await lab.Copilot.SuggestClassificationAsync(t.Id)).Ok);
        Assert.True((await lab.Copilot.SuggestSummaryAsync(t.Id)).Ok);
        Assert.True((await lab.Copilot.SuggestReplyAsync(t.Id)).Ok);
        Assert.True((await lab.Copilot.SuggestAssigneeAsync(t.Id)).Ok);

        var after = lab.Get(t);
        Assert.Equal(before.Priority, after.Priority);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Tags, after.Tags);
        Assert.Equal(before.AssigneeId, after.AssigneeId);
        Assert.Equal(before.Messages.Count, after.Messages.Count);
        Assert.Null(after.FirstResponseAt);
        Assert.Equal(4, lab.Copilot.Suggestions(t.Id, SuggestionStatus.Pending).Count);
    }

    [Fact]
    public async Task Approving_a_classification_sets_priority_and_tags()
    {
        var lab = new Lab();
        var t = lab.New("Site is down", "We cannot access the dashboard. Outage for the whole team.");
        var s = (await lab.Copilot.SuggestClassificationAsync(t.Id)).Value!;

        var r = lab.Copilot.Approve(s.Id, lab.Ana.Id);

        Assert.True(r.Ok, r.Message);
        var after = lab.Get(t);
        Assert.Equal(Priority.Urgent, after.Priority);
        Assert.Contains("incident", after.Tags);
        var done = lab.Copilot.Suggestion(s.Id)!;
        Assert.Equal(SuggestionStatus.Approved, done.Status);
        Assert.Equal(lab.Ana.Id, done.DecidedBy);
        Assert.Equal(lab.Clock.GetUtcNow(), done.DecidedAt);
    }

    [Fact]
    public async Task Approving_a_reply_sends_it_as_the_approving_agent()
    {
        var lab = new Lab();
        var t = lab.New("Charged twice", "Please refund one charge.");
        var s = (await lab.Copilot.SuggestReplyAsync(t.Id)).Value!;

        var r = lab.Copilot.Approve(s.Id, lab.Cleo.Id);

        Assert.True(r.Ok, r.Message);
        var sent = lab.Get(t).Messages.Last();
        Assert.Equal(Author.Agent, sent.Author);
        Assert.Equal("Cleo", sent.By);
        Assert.Equal(s.Text, sent.Text);
        Assert.NotNull(lab.Get(t).FirstResponseAt);
    }

    [Fact]
    public async Task An_edited_reply_sends_the_edit_not_the_draft()
    {
        var lab = new Lab();
        var t = lab.New("Charged twice", "Please refund one charge.");
        var s = (await lab.Copilot.SuggestReplyAsync(t.Id)).Value!;

        var r = lab.Copilot.Approve(s.Id, lab.Ana.Id, "  Hi, I have refunded the duplicate charge.  ");

        Assert.True(r.Ok, r.Message);
        Assert.Equal("Hi, I have refunded the duplicate charge.", lab.Get(t).Messages.Last().Text);
        Assert.Equal("Hi, I have refunded the duplicate charge.", lab.Copilot.Suggestion(s.Id)!.Text);
    }

    [Fact]
    public async Task Approving_a_summary_adds_an_internal_note_the_customer_never_sees()
    {
        var lab = new Lab();
        var t = lab.New();
        var s = (await lab.Copilot.SuggestSummaryAsync(t.Id)).Value!;

        var r = lab.Copilot.Approve(s.Id, lab.Ana.Id);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(Author.Note, lab.Get(t).Messages.Last().Author);
        Assert.Null(lab.Get(t).FirstResponseAt);
    }

    [Fact]
    public async Task Approving_an_assignee_assigns_the_ticket()
    {
        var lab = new Lab();
        var t = lab.New("Charged twice", "Please refund one charge.");
        var s = (await lab.Copilot.SuggestAssigneeAsync(t.Id)).Value!;

        var r = lab.Copilot.Approve(s.Id, lab.Ana.Id);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(lab.Cleo.Id, lab.Get(t).AssigneeId);
    }

    [Fact]
    public async Task Rejecting_changes_nothing_and_cannot_be_approved_later()
    {
        var lab = new Lab();
        var t = lab.New("Charged twice", "Please refund one charge.");
        var s = (await lab.Copilot.SuggestReplyAsync(t.Id)).Value!;
        var count = lab.Get(t).Messages.Count;

        Assert.True(lab.Copilot.Reject(s.Id, lab.Ana.Id).Ok);
        var late = lab.Copilot.Approve(s.Id, lab.Ana.Id);

        Assert.False(late.Ok);
        Assert.Equal(count, lab.Get(t).Messages.Count);
        Assert.Equal(SuggestionStatus.Rejected, lab.Copilot.Suggestion(s.Id)!.Status);
    }

    [Fact]
    public async Task A_suggestion_can_only_be_approved_once()
    {
        var lab = new Lab();
        var t = lab.New();
        var s = (await lab.Copilot.SuggestReplyAsync(t.Id)).Value!;

        Assert.True(lab.Copilot.Approve(s.Id, lab.Ana.Id).Ok);
        var again = lab.Copilot.Approve(s.Id, lab.Ana.Id);

        Assert.False(again.Ok);
        Assert.Equal(2, lab.Get(t).Messages.Count);
    }

    [Fact]
    public async Task Approving_as_an_unknown_agent_is_refused_and_leaves_it_pending()
    {
        var lab = new Lab();
        var t = lab.New();
        var s = (await lab.Copilot.SuggestReplyAsync(t.Id)).Value!;

        var r = lab.Copilot.Approve(s.Id, Guid.NewGuid());

        Assert.False(r.Ok);
        Assert.Equal(SuggestionStatus.Pending, lab.Copilot.Suggestion(s.Id)!.Status);
        Assert.Single(lab.Get(t).Messages);
    }

    [Fact]
    public async Task Only_replies_and_summaries_can_be_edited()
    {
        var lab = new Lab();
        var t = lab.New("Charged twice", "Please refund one charge.");
        var s = (await lab.Copilot.SuggestClassificationAsync(t.Id)).Value!;

        var r = lab.Copilot.Approve(s.Id, lab.Ana.Id, "Priority Urgent");

        Assert.False(r.Ok);
        Assert.Equal(SuggestionStatus.Pending, lab.Copilot.Suggestion(s.Id)!.Status);
        Assert.Equal(Priority.Normal, lab.Get(t).Priority);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_edited_reply_is_refused(string edit)
    {
        var lab = new Lab();
        var t = lab.New();
        var s = (await lab.Copilot.SuggestReplyAsync(t.Id)).Value!;

        Assert.False(lab.Copilot.Approve(s.Id, lab.Ana.Id, edit).Ok);
        Assert.Single(lab.Get(t).Messages);
    }

    [Fact]
    public async Task If_the_change_cannot_be_applied_the_suggestion_stays_pending()
    {
        var lab = new Lab();
        var t = lab.New("Site is down", "Outage for everyone, we cannot access anything.");
        var s = (await lab.Copilot.SuggestClassificationAsync(t.Id)).Value!;
        lab.Desk.Solve(t.Id);

        var r = lab.Copilot.Approve(s.Id, lab.Ana.Id);

        Assert.False(r.Ok);
        Assert.Equal(SuggestionStatus.Pending, lab.Copilot.Suggestion(s.Id)!.Status);
        Assert.Null(lab.Copilot.Suggestion(s.Id)!.DecidedAt);
    }

    [Fact]
    public async Task A_second_pending_suggestion_of_the_same_kind_is_refused_until_the_first_is_decided()
    {
        var lab = new Lab();
        var t = lab.New();
        var first = (await lab.Copilot.SuggestReplyAsync(t.Id)).Value!;

        var duplicate = await lab.Copilot.SuggestReplyAsync(t.Id);
        Assert.False(duplicate.Ok);
        Assert.Contains("already waiting", duplicate.Message);

        lab.Copilot.Reject(first.Id, lab.Ana.Id);
        Assert.True((await lab.Copilot.SuggestReplyAsync(t.Id)).Ok);
    }

    [Fact]
    public async Task Solved_and_unknown_tickets_get_no_suggestions()
    {
        var lab = new Lab();
        var t = lab.New();
        lab.Desk.Solve(t.Id);

        Assert.False((await lab.Copilot.SuggestReplyAsync(t.Id)).Ok);
        Assert.False((await lab.Copilot.SuggestReplyAsync(Guid.NewGuid())).Ok);
        Assert.Empty(lab.Copilot.Suggestions());
    }

    [Fact]
    public async Task A_failed_model_call_stores_nothing()
    {
        var lab = new Lab(chat: new ThrowingChatClient());
        var t = lab.New();

        var r = await lab.Copilot.SuggestReplyAsync(t.Id);

        Assert.False(r.Ok);
        Assert.Null(r.Value);
        Assert.Empty(lab.Copilot.Suggestions());
    }

    [Fact]
    public async Task Returned_suggestions_are_copies_of_the_stored_ones()
    {
        var lab = new Lab();
        var t = lab.New();
        var s = (await lab.Copilot.SuggestReplyAsync(t.Id)).Value!;

        Assert.NotSame(lab.Copilot.Suggestion(s.Id), lab.Copilot.Suggestion(s.Id));
        Assert.NotSame(s, lab.Copilot.Suggestion(s.Id));
        Assert.NotSame(lab.Copilot.Suggestions()[0], lab.Copilot.Suggestions()[0]);
    }
}
