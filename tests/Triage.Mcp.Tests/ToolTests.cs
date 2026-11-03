using System.Text.Json;
using Triage.Copilot;
using Triage.Engine;

namespace Triage.Mcp.Tests;

public class ToolTests
{
    [Fact]
    public async Task The_server_offers_exactly_the_seven_tools()
    {
        await using var rig = await Rig.StartAsync();

        var names = (await rig.Client.ListToolsAsync()).Select(t => t.Name).OrderBy(n => n).ToArray();

        Assert.Equal(["approve_suggestion", "draft_reply", "get_ticket", "list_tickets", "reject_suggestion", "sla_breaches", "summarize_ticket"], names);
    }

    [Fact]
    public async Task Read_tools_are_marked_read_only_and_the_rest_are_not()
    {
        await using var rig = await Rig.StartAsync();

        var tools = (await rig.Client.ListToolsAsync()).ToDictionary(t => t.Name);

        foreach (var name in new[] { "list_tickets", "get_ticket", "summarize_ticket", "sla_breaches" })
        {
            Assert.True(tools[name].ProtocolTool.Annotations?.ReadOnlyHint, $"{name} should be read-only");
        }

        Assert.NotEqual(true, tools["draft_reply"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.NotEqual(true, tools["approve_suggestion"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools["approve_suggestion"].ProtocolTool.Annotations?.DestructiveHint);
    }

    [Fact]
    public async Task Every_tool_that_is_not_read_only_takes_a_confirm_argument()
    {
        await using var rig = await Rig.StartAsync();

        foreach (var tool in await rig.Client.ListToolsAsync())
        {
            if (tool.ProtocolTool.Annotations?.ReadOnlyHint == true)
            {
                continue;
            }

            Assert.True(tool.JsonSchema.GetProperty("properties").TryGetProperty("confirm", out var confirm), $"{tool.Name} has no confirm argument");
            Assert.Equal("boolean", confirm.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task List_tickets_returns_the_seeded_desk_newest_first_without_requester_addresses()
    {
        await using var rig = await Rig.StartAsync();

        var result = await rig.Call("list_tickets");

        Assert.NotEqual(true, result.IsError);
        var body = Rig.Body(result);
        Assert.Equal(4, body.GetProperty("count").GetInt32());
        var keys = body.GetProperty("tickets").EnumerateArray().Select(t => t.GetProperty("key").GetString()).ToArray();
        Assert.Equal(["T-0004", "T-0003", "T-0002", "T-0001"], keys);
        Assert.DoesNotContain("@", Rig.Text(result));
    }

    [Fact]
    public async Task List_tickets_can_filter_by_status_sla_state_and_limit()
    {
        await using var rig = await Rig.StartAsync();

        var pending = Rig.Body(await rig.Call("list_tickets", ("status", "pending")));
        var breached = Rig.Body(await rig.Call("list_tickets", ("sla_state", "Breached")));
        var limited = Rig.Body(await rig.Call("list_tickets", ("limit", 2)));

        Assert.Equal(["T-0001"], pending.GetProperty("tickets").EnumerateArray().Select(t => t.GetProperty("key").GetString()).ToArray());
        Assert.Equal(["T-0003"], breached.GetProperty("tickets").EnumerateArray().Select(t => t.GetProperty("key").GetString()).ToArray());
        Assert.Equal(2, limited.GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData("status", "archived")]
    [InlineData("status", "7")]
    [InlineData("sla_state", "late")]
    public async Task List_tickets_rejects_unknown_filters_with_a_clear_error(string arg, string value)
    {
        await using var rig = await Rig.StartAsync();

        var result = await rig.Call("list_tickets", (arg, value));

        Assert.True(result.IsError);
        Assert.Contains($"Unknown {arg}", Rig.Text(result));
    }

    [Fact]
    public async Task Get_ticket_shows_messages_with_personal_details_redacted()
    {
        await using var rig = await Rig.StartAsync();

        var result = await rig.Call("get_ticket", ("key", "t-0003"));

        Assert.NotEqual(true, result.IsError);
        var text = Rig.Text(result);
        Assert.Contains("[phone]", text);
        Assert.DoesNotContain("555-123-4567", text);
        Assert.DoesNotContain("@", text);
        Assert.Equal("T-0003", Rig.Body(result).GetProperty("ticket").GetProperty("key").GetString());
    }

    [Fact]
    public async Task An_unknown_ticket_is_an_error_not_a_crash()
    {
        await using var rig = await Rig.StartAsync();

        foreach (var tool in new[] { "get_ticket", "summarize_ticket" })
        {
            var result = await rig.Call(tool, ("key", "T-9999"));
            Assert.True(result.IsError);
            Assert.Contains("No ticket", Rig.Text(result));
        }

        var draft = await rig.Call("draft_reply", ("key", "T-9999"), ("confirm", true));
        Assert.True(draft.IsError);
    }

    [Fact]
    public async Task Summarize_ticket_returns_a_summary_and_stores_nothing()
    {
        await using var rig = await Rig.StartAsync();
        var before = rig.Ticket("T-0002").Messages.Count;

        var result = await rig.Call("summarize_ticket", ("key", "T-0002"));

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("message(s)", Rig.Body(result).GetProperty("summary").GetString());
        Assert.Empty(rig.Copilot.Suggestions());
        Assert.Equal(before, rig.Ticket("T-0002").Messages.Count);
    }

    [Fact]
    public async Task Sla_breaches_lists_overdue_tickets_and_counts_those_at_risk()
    {
        await using var rig = await Rig.StartAsync();

        var body = Rig.Body(await rig.Call("sla_breaches"));

        var rows = body.GetProperty("breached").EnumerateArray().ToList();
        Assert.Equal(body.GetProperty("breachedCount").GetInt32(), rows.Count);
        Assert.Contains(rows, r => r.GetProperty("key").GetString() == "T-0003");
        Assert.All(rows, r => Assert.True(r.GetProperty("firstReply").GetString() == "Breached" || r.GetProperty("resolution").GetString() == "Breached"));
        Assert.True(body.GetProperty("atRiskCount").GetInt32() >= 0);
    }

    [Fact]
    public async Task Draft_reply_without_confirm_is_refused_and_stores_nothing()
    {
        await using var rig = await Rig.StartAsync();

        var omitted = await rig.Call("draft_reply", ("key", "T-0002"));
        var no = await rig.Call("draft_reply", ("key", "T-0002"), ("confirm", false));

        Assert.True(omitted.IsError);
        Assert.True(no.IsError);
        Assert.Contains("confirm: true", Rig.Text(omitted));
        Assert.Empty(rig.Copilot.Suggestions());
        Assert.Empty(rig.Chat.Requests);
    }

    [Fact]
    public async Task Draft_reply_with_confirm_stores_a_pending_suggestion_and_sends_nothing()
    {
        await using var rig = await Rig.StartAsync();
        var before = rig.Ticket("T-0002").Messages.Count;

        var result = await rig.Call("draft_reply", ("key", "T-0002"), ("confirm", true));

        Assert.NotEqual(true, result.IsError);
        var s = Rig.Body(result).GetProperty("suggestion");
        Assert.Equal("Pending", s.GetProperty("status").GetString());
        Assert.Equal("Reply", s.GetProperty("kind").GetString());
        Assert.Single(rig.Copilot.Suggestions(rig.Ticket("T-0002").Id, SuggestionStatus.Pending));
        Assert.Equal(before, rig.Ticket("T-0002").Messages.Count);
        Assert.Null(rig.Ticket("T-0002").FirstResponseAt);
    }

    [Fact]
    public async Task Approve_without_confirm_is_refused_and_leaves_everything_as_it_was()
    {
        await using var rig = await Rig.StartAsync();
        var id = await Draft(rig);
        var before = rig.Ticket("T-0002").Messages.Count;

        var omitted = await rig.Call("approve_suggestion", ("suggestion_id", id), ("agent", "Ana"));
        var no = await rig.Call("approve_suggestion", ("suggestion_id", id), ("agent", "Ana"), ("confirm", false));

        Assert.True(omitted.IsError);
        Assert.True(no.IsError);
        Assert.Contains("confirm: true", Rig.Text(no));
        Assert.Equal(before, rig.Ticket("T-0002").Messages.Count);
        Assert.Equal(SuggestionStatus.Pending, rig.Copilot.Suggestion(Guid.Parse(id))!.Status);
    }

    [Fact]
    public async Task The_confirm_check_comes_before_any_lookup()
    {
        await using var rig = await Rig.StartAsync();

        var result = await rig.Call("approve_suggestion", ("suggestion_id", "not-an-id"), ("agent", "Nobody"));

        Assert.True(result.IsError);
        Assert.Contains("confirm: true", Rig.Text(result));
    }

    [Fact]
    public async Task Approve_with_confirm_sends_the_reply_as_the_named_agent()
    {
        await using var rig = await Rig.StartAsync();
        var id = await Draft(rig);

        var result = await rig.Call("approve_suggestion", ("suggestion_id", id), ("agent", "ana"), ("confirm", true));

        Assert.NotEqual(true, result.IsError);
        var sent = rig.Ticket("T-0002").Messages.Last();
        Assert.Equal(Author.Agent, sent.Author);
        Assert.Equal("Ana", sent.By);
        Assert.Equal("Approved", Rig.Body(result).GetProperty("suggestion").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Approve_can_send_edited_text_instead_of_the_draft()
    {
        await using var rig = await Rig.StartAsync();
        var id = await Draft(rig);

        var result = await rig.Call("approve_suggestion", ("suggestion_id", id), ("agent", "Cleo"), ("confirm", true), ("edited_text", "Hi, the duplicate charge is refunded."));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Hi, the duplicate charge is refunded.", rig.Ticket("T-0002").Messages.Last().Text);
    }

    [Fact]
    public async Task Approving_twice_or_as_an_unknown_agent_or_with_a_bad_id_is_refused()
    {
        await using var rig = await Rig.StartAsync();
        var id = await Draft(rig);

        var stranger = await rig.Call("approve_suggestion", ("suggestion_id", id), ("agent", "Mallory"), ("confirm", true));
        var badId = await rig.Call("approve_suggestion", ("suggestion_id", "xyz"), ("agent", "Ana"), ("confirm", true));
        var missing = await rig.Call("approve_suggestion", ("suggestion_id", Guid.NewGuid().ToString()), ("agent", "Ana"), ("confirm", true));
        Assert.True((await rig.Call("approve_suggestion", ("suggestion_id", id), ("agent", "Ana"), ("confirm", true))).IsError != true);
        var again = await rig.Call("approve_suggestion", ("suggestion_id", id), ("agent", "Ana"), ("confirm", true));

        Assert.True(stranger.IsError);
        Assert.Contains("Known agents", Rig.Text(stranger));
        Assert.True(badId.IsError);
        Assert.True(missing.IsError);
        Assert.True(again.IsError);
        Assert.Equal(2, rig.Ticket("T-0002").Messages.Count);
    }

    [Fact]
    public async Task A_second_draft_is_refused_while_the_first_is_still_pending()
    {
        await using var rig = await Rig.StartAsync();
        await Draft(rig);

        var again = await rig.Call("draft_reply", ("key", "T-0002"), ("confirm", true));

        Assert.True(again.IsError);
        Assert.Contains("already waiting", Rig.Text(again));
    }

    [Fact]
    public async Task Get_ticket_shows_the_pending_suggestion()
    {
        await using var rig = await Rig.StartAsync();
        var id = await Draft(rig);

        var suggestions = Rig.Body(await rig.Call("get_ticket", ("key", "T-0002"))).GetProperty("suggestions").EnumerateArray().ToList();

        Assert.Single(suggestions);
        Assert.Equal(id, suggestions[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Reject_without_confirm_is_refused_and_the_suggestion_stays_pending()
    {
        await using var rig = await Rig.StartAsync();
        var id = await Draft(rig);

        var omitted = await rig.Call("reject_suggestion", ("suggestion_id", id), ("agent", "Ana"));
        var no = await rig.Call("reject_suggestion", ("suggestion_id", id), ("agent", "Ana"), ("confirm", false));

        Assert.True(omitted.IsError);
        Assert.True(no.IsError);
        Assert.Contains("confirm: true", Rig.Text(no));
        Assert.Equal(SuggestionStatus.Pending, rig.Copilot.Suggestion(Guid.Parse(id))!.Status);
    }

    [Fact]
    public async Task Reject_with_confirm_sends_nothing_and_frees_the_ticket_for_a_new_draft()
    {
        await using var rig = await Rig.StartAsync();
        var id = await Draft(rig);
        var before = rig.Ticket("T-0002").Messages.Count;

        var result = await rig.Call("reject_suggestion", ("suggestion_id", id), ("agent", "Ana"), ("confirm", true));

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Rejected", Rig.Body(result).GetProperty("suggestion").GetProperty("status").GetString());
        Assert.Equal(before, rig.Ticket("T-0002").Messages.Count);
        Assert.Null(rig.Ticket("T-0002").FirstResponseAt);
        var fresh = await rig.Call("draft_reply", ("key", "T-0002"), ("confirm", true));
        Assert.NotEqual(true, fresh.IsError);
    }

    [Fact]
    public async Task A_rejected_suggestion_cannot_be_approved_and_an_approved_one_cannot_be_rejected()
    {
        await using var rig = await Rig.StartAsync();
        var rejected = await Draft(rig);
        await rig.Call("reject_suggestion", ("suggestion_id", rejected), ("agent", "Ana"), ("confirm", true));
        var late = await rig.Call("approve_suggestion", ("suggestion_id", rejected), ("agent", "Ana"), ("confirm", true));

        var approved = await Draft(rig);
        await rig.Call("approve_suggestion", ("suggestion_id", approved), ("agent", "Ana"), ("confirm", true));
        var tooLate = await rig.Call("reject_suggestion", ("suggestion_id", approved), ("agent", "Ana"), ("confirm", true));

        Assert.True(late.IsError);
        Assert.True(tooLate.IsError);
        Assert.Equal(SuggestionStatus.Approved, rig.Copilot.Suggestion(Guid.Parse(approved))!.Status);
        Assert.Equal(2, rig.Ticket("T-0002").Messages.Count);
    }

    [Theory]
    [InlineData("not-an-id", "Ana")]
    [InlineData("00000000-0000-0000-0000-000000000001", "Ana")]
    [InlineData("valid", "Mallory")]
    public async Task Reject_refuses_bad_ids_missing_suggestions_and_unknown_agents(string id, string agent)
    {
        await using var rig = await Rig.StartAsync();
        var real = await Draft(rig);

        var result = await rig.Call("reject_suggestion", ("suggestion_id", id == "valid" ? real : id), ("agent", agent), ("confirm", true));

        Assert.True(result.IsError);
        Assert.Equal(SuggestionStatus.Pending, rig.Copilot.Suggestion(Guid.Parse(real))!.Status);
    }

    private static async Task<string> Draft(Rig rig)
    {
        var made = await rig.Call("draft_reply", ("key", "T-0002"), ("confirm", true));
        Assert.NotEqual(true, made.IsError);
        return Rig.Body(made).GetProperty("suggestion").GetProperty("id").GetString()!;
    }
}
