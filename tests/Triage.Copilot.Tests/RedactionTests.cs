using Triage.Copilot;

namespace Triage.Copilot.Tests;

public class RedactionTests
{
    [Theory]
    [InlineData("write me at maria.g@globex.com please", "write me at [email] please")]
    [InlineData("a@b.io and c.d+tag@sub.example.co.uk", "[email] and [email]")]
    [InlineData("call 555-123-4567 today", "call [phone] today")]
    [InlineData("call (555) 123-4567 today", "call [phone] today")]
    [InlineData("call 5551234567 today", "call [phone] today")]
    [InlineData("call +1 555 123 4567 today", "call [phone] today")]
    [InlineData("call +44 20 7946 0958 today", "call [phone] today")]
    public void Emails_and_phone_numbers_are_replaced(string input, string expected) => Assert.Equal(expected, Redactor.Redact(input));

    [Theory]
    [InlineData("It broke on 2026-10-05 09:00")]
    [InlineData("See T-0001 and order 12345")]
    [InlineData("Meeting at 10:30 on 5/10")]
    [InlineData("No personal data here.")]
    public void Ordinary_text_is_left_alone(string input) => Assert.Equal(input, Redactor.Redact(input));

    [Fact]
    public async Task Nothing_personal_reaches_the_model()
    {
        var lab = new Lab();
        var t = lab.New("Refund for maria@globex.com", "Please refund me. Reach me at maria@globex.com or 555-123-4567.", "maria@globex.com");
        lab.Desk.CustomerReply(t.Id, "My other number is (555) 987-6543.");

        await lab.Assistant.ClassifyTicketAsync(lab.Get(t));
        await lab.Assistant.SummarizeThreadAsync(lab.Get(t));
        await lab.Assistant.DraftReplyAsync(lab.Get(t));
        await lab.Assistant.SuggestAssigneeAsync(lab.Get(t), AgentOption.From(lab.Desk));

        Assert.Equal(4, lab.Chat.Requests.Count);
        foreach (var request in lab.Chat.Requests)
        {
            var sent = string.Join('\n', request.Select(m => m.Text));
            Assert.DoesNotContain("maria@globex.com", sent);
            Assert.DoesNotContain("@", sent);
            Assert.DoesNotContain("555-123-4567", sent);
            Assert.DoesNotContain("987-6543", sent);
            Assert.Contains("[email]", sent);
        }
    }

    [Fact]
    public async Task The_requester_address_is_never_sent()
    {
        var lab = new Lab();
        var t = lab.New("Slow exports", "They take minutes.", "someone.private@example.org");

        await lab.Assistant.ClassifyTicketAsync(lab.Get(t));

        Assert.DoesNotContain("someone.private", string.Join('\n', lab.Chat.Requests[0].Select(m => m.Text)));
    }
}
