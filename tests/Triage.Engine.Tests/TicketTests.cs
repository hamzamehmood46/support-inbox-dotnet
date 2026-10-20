using Triage.Engine;

namespace Triage.Engine.Tests;

public class CreationTests
{
    private readonly Shop _s = new();

    [Fact]
    public void A_new_ticket_records_the_customers_message_and_starts_the_clocks()
    {
        var t = _s.New("Cannot print invoices", "cara@example.com", "The print button does nothing.");

        Assert.Equal("T-0001", t.Key);
        Assert.Equal(Status.New, t.Status);
        Assert.Equal(Shop.Start, t.CreatedAt);
        Assert.Equal(Shop.Start, t.SlaStart);
        var message = Assert.Single(t.Messages);
        Assert.Equal((1, Author.Customer, "cara@example.com", "The print button does nothing."), (message.Seq, message.Author, message.By, message.Text));
        Assert.Null(t.FirstResponseAt);
        Assert.Equal(0, t.Reopens);
    }

    [Fact]
    public void Tickets_are_numbered_in_sequence()
    {
        Assert.Equal(["T-0001", "T-0002", "T-0003"], new[] { _s.New("First one"), _s.New("Second one"), _s.New("Third one") }.Select(t => t.Key));
    }

    [Fact]
    public void Urgent_tickets_get_one_hour_to_respond_and_four_to_solve()
    {
        var t = _s.New(priority: Priority.Urgent);

        Assert.Equal(Shop.At(10, 5, 11), t.FirstResponseDue);
        Assert.Equal(Shop.At(10, 5, 14), t.ResolutionDue);
    }

    [Fact]
    public void High_tickets_get_four_hours_to_respond_and_a_working_day_to_solve()
    {
        var t = _s.New(priority: Priority.High);

        Assert.Equal(Shop.At(10, 5, 14), t.FirstResponseDue);
        Assert.Equal(Shop.At(10, 6, 10), t.ResolutionDue);
    }

    [Fact]
    public void Normal_and_low_priority_use_their_longer_targets()
    {
        var normal = _s.New(priority: Priority.Normal);
        var low = _s.New(priority: Priority.Low);

        Assert.Equal(Shop.At(10, 6, 10), normal.FirstResponseDue);
        Assert.Equal(Shop.At(10, 8, 10), normal.ResolutionDue);
        Assert.Equal(Shop.At(10, 8, 10), low.FirstResponseDue);
        Assert.Equal(Shop.At(10, 12, 10), low.ResolutionDue);
    }

    [Fact]
    public void A_ticket_that_arrives_at_night_or_at_the_weekend_starts_its_clock_when_the_team_opens()
    {
        _s.GoTo(Shop.At(10, 10, 12));

        var t = _s.New(priority: Priority.Urgent);

        Assert.Equal(Shop.At(10, 12, 10), t.FirstResponseDue);
        Assert.Equal(Shop.At(10, 12, 13), t.ResolutionDue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ab")]
    public void A_subject_needs_three_characters(string subject)
    {
        var result = _s.Desk.Create(subject, "cara@example.com", "Hello");

        Assert.False(result.Ok);
        Assert.Contains("subject", result.Message);
    }

    [Fact]
    public void A_subject_is_limited_to_a_hundred_and_twenty_characters()
    {
        Assert.True(_s.Desk.Create(new string('a', 120), "cara@example.com", "Hello").Ok);
        Assert.False(_s.Desk.Create(new string('a', 121), "cara@example.com", "Hello").Ok);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("a b@c.com")]
    [InlineData("@c.com")]
    public void A_requester_needs_a_valid_email(string email) => Assert.False(_s.Desk.Create("Subject", email, "Hello").Ok);

    [Fact]
    public void The_message_must_not_be_empty_or_huge()
    {
        Assert.False(_s.Desk.Create("Subject", "a@b.com", " ").Ok);
        Assert.True(_s.Desk.Create("Subject", "a@b.com", new string('x', Desk.MaxText)).Ok);
        Assert.False(_s.Desk.Create("Subject", "a@b.com", new string('x', Desk.MaxText + 1)).Ok);
    }

    [Fact]
    public void Whitespace_around_the_text_is_trimmed()
    {
        var t = _s.Desk.Create("  Spaced out  ", "  cara@example.com ", "  hello  ").Value!;

        Assert.Equal("Spaced out", t.Subject);
        Assert.Equal("cara@example.com", t.Requester);
        Assert.Equal("hello", t.Messages[0].Text);
    }

    [Fact]
    public void Tags_are_lowercased_trimmed_and_deduplicated()
    {
        var t = _s.New(tags: [" Billing ", "billing", "VIP-customer"]);

        Assert.Equal(["billing", "vip-customer"], t.Tags);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--dash")]
    [InlineData("")]
    [InlineData("abcdefghijklmnopqrstuvwxyz")]
    public void Badly_formed_tags_are_refused_and_nothing_is_created(string tag)
    {
        var result = _s.Desk.Create("Subject", "a@b.com", "Hello", tags: [tag]);

        Assert.False(result.Ok);
        Assert.Empty(_s.Desk.Tickets());
    }

    [Fact]
    public void A_ticket_can_have_ten_tags_but_not_eleven()
    {
        var ten = Enumerable.Range(1, 10).Select(i => "t" + i).ToList();

        Assert.True(_s.Desk.Create("Subject", "a@b.com", "Hello", tags: ten).Ok);
        Assert.False(_s.Desk.Create("Subject", "a@b.com", "Hello", tags: ten.Append("t11")).Ok);
    }

    [Fact]
    public void A_refused_ticket_does_not_use_up_a_number()
    {
        _s.Desk.Create("", "a@b.com", "Hello");

        Assert.Equal("T-0001", _s.New().Key);
    }
}

public class TriageRuleTests
{
    private readonly Shop _s = new();

    [Theory]
    [InlineData("I was charged twice", "billing")]
    [InlineData("Need a REFUND please", "billing")]
    [InlineData("Question about my invoice", "billing")]
    [InlineData("I forgot my password", "account")]
    [InlineData("Cannot log in to the portal", "account")]
    [InlineData("Locked out of my account", "account")]
    [InlineData("It would be nice to export to CSV", "feedback")]
    public void Keywords_add_a_tag(string subject, string tag) => Assert.Contains(tag, _s.New(subject).Tags);

    [Fact]
    public void Only_whole_words_count()
    {
        var t = _s.New("The discharge summary page", body: "It shows the wrong date for the surcharges report.");

        Assert.DoesNotContain("billing", t.Tags);
    }

    [Fact]
    public void Keywords_in_the_message_count_as_well_as_those_in_the_subject()
    {
        var t = _s.New("Question", body: "Why was my card charged?");

        Assert.Contains("billing", t.Tags);
    }

    [Theory]
    [InlineData("The whole site is down")]
    [InlineData("Customers cannot access checkout")]
    [InlineData("App keeps crashing on start")]
    [InlineData("Major outage in the EU")]
    public void Incident_words_make_the_ticket_urgent_and_tag_it(string subject)
    {
        var t = _s.New(subject);

        Assert.Equal(Priority.Urgent, t.Priority);
        Assert.Contains("incident", t.Tags);
        Assert.Equal(Shop.At(10, 5, 11), t.FirstResponseDue);
    }

    [Fact]
    public void A_rule_never_lowers_a_priority_chosen_by_hand()
    {
        var t = _s.New("Nothing special", priority: Priority.Urgent);

        Assert.Equal(Priority.Urgent, t.Priority);
    }

    [Fact]
    public void Several_rules_can_apply_at_once_and_a_tag_is_added_only_once()
    {
        var t = _s.New("Outage after my refund", body: "The refund page is down. Refund please.");

        Assert.Equal(["billing", "incident"], t.Tags.Order());
        Assert.Equal(Priority.Urgent, t.Priority);
    }

    [Fact]
    public void A_desk_can_be_given_its_own_rules()
    {
        var desk = new Desk(new SimulatedClock(Shop.Start), rules: [new TriageRule(["vip"], "vip", Priority.High)]);

        var t = desk.Create("A VIP customer asks", "a@b.com", "hello").Value!;

        Assert.Equal(["vip"], t.Tags);
        Assert.Equal(Priority.High, t.Priority);
    }

    [Fact]
    public void A_desk_can_run_without_rules()
    {
        var desk = new Desk(new SimulatedClock(Shop.Start), rules: []);

        var t = desk.Create("The site is down", "a@b.com", "outage").Value!;

        Assert.Empty(t.Tags);
        Assert.Equal(Priority.Normal, t.Priority);
    }
}
