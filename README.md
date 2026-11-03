# Triage: a support inbox with honest SLA timers

[![Test and deploy](https://github.com/hamzamehmood46/support-inbox-dotnet/actions/workflows/pages.yml/badge.svg)](https://github.com/hamzamehmood46/support-inbox-dotnet/actions/workflows/pages.yml)

**Live demo: [hamzamehmood46.github.io/support-inbox-dotnet](https://hamzamehmood46.github.io/support-inbox-dotnet/)**

A help-desk engine in C#. Tickets have service level targets that run in working hours only, pause while the team waits on the customer, and escalate when missed. Work is shared fairly between agents by skill and capacity. Blazor WebAssembly runs the same engine in your browser, on a clock you control, so you can watch days of support work in a minute.

## Try it (3 minutes)

1. The clock starts **Tuesday 10:30**. The *outage* ticket was marked urgent automatically from its words and has one hour to get a first reply.
2. Press **+1 day** without replying: tickets go from ok to at risk to breached, and breached ones are escalated to the top. Nights and weekends do not count.
3. Reply to a ticket and choose **Pending**: the resolution timer pauses. Press **Customer replies**: it resumes, and the deadline has moved out by the working time you waited.
4. On the **Team** tab mark an agent away: their open tickets go back to the queue and are shared out. Tagged tickets go to the agent with the matching skill.
5. Leave a pending ticket for **a week**: it is solved automatically, and closed three days later.

## What it does

| Capability | Behavior |
|---|---|
| **Business-hours SLAs** | First-response and resolution targets per priority, counted in working time (default 09:00 to 17:00 UTC, Monday to Friday, holidays supported). A ticket arriving on Saturday starts its clock on Monday morning. |
| **Honest pauses** | While a ticket is pending on the customer the resolution clock stops. When they reply, the deadline moves out by exactly the working time spent waiting, across nights, weekends and several separate waits. Reopening starts a fresh clock. |
| **States** | ok, at risk (75% used), breached, met, paused. A reply or solve exactly at the deadline counts as met. |
| **Assignment** | The least busy available agent with room takes the ticket, ties go round-robin. Tagged tickets prefer agents with the skill, then generalists. Going away hands open tickets back to the queue, and a reopened ticket never stays with someone who is away. The queue is worked escalated first, then by priority, then oldest. |
| **Triage rules** | Words in a new ticket add tags and a minimum priority (for example an outage becomes urgent), on whole words only. |
| **Conversation rules** | A public reply is the first response. An internal note never is, and cannot change status. A customer reply resumes a pending ticket, reopens a solved one, and on a closed one opens a linked follow-up. |
| **Merging** | Duplicates from the same person merge into one ticket: messages interleave by time, the earliest first response counts, tags are combined, and the duplicate closes with a pointer. Later customer replies to it land on the surviving ticket. |
| **Automation** | Pending with no reply for 7 days is solved, solved for 3 days is closed, missed targets escalate once, the queue is assigned. All thresholds are configurable. |
| **Reports** | Compliance (share of solved tickets within target), average first reply in working time, status counts and each agent's load. |

## AI copilot and MCP server

Two additions sit on top of the engine. Both only make suggestions: **nothing is sent to a customer and no ticket changes until a person approves it.**

> **The default model is a deterministic fake.** `FakeChatClient` answers with plain keyword rules. It needs no network, no key and no account, it gives the same answer for the same ticket, and it is what the live demo, the tests and the MCP server use unless you configure otherwise. It is not a real AI model.

Quick start:

```bash
git clone https://github.com/hamzamehmood46/support-inbox-dotnet
cd support-inbox-dotnet
dotnet test
dotnet build src/Triage.Mcp -c Release
dotnet src/Triage.Mcp/bin/Release/net8.0/Triage.Mcp.dll
```

The last line starts the MCP server on standard input and output, so it waits for a client. To see the copilot instead, open the live demo, select a ticket and use the **AI copilot** panel.

### The copilot

`ITriageAssistant` can classify a ticket (priority and tags), summarize a thread, draft a reply and suggest an assignee. Each answer is a typed record with a confidence score and a one-sentence rationale. `TriageCopilot` stores each one as Pending, Approved or Rejected, and only **Approve** acts on the ticket, as the agent who approved it: a reply is sent, a summary becomes an internal note, a classification sets priority and tags, an assignee assigns. Replies and summaries can be edited first. If a change cannot be applied the suggestion stays pending.

Guardrails around every model call:

- Email addresses and phone numbers are replaced with `[email]` and `[phone]` before any text leaves the process, and the requester's address is never sent. Names, street addresses and card numbers are **not** detected, and a bare 10-digit order number can be mistaken for a phone number.
- At most 6,000 characters of ticket text are sent; the oldest messages are dropped first.
- A call that takes longer than 30 seconds is abandoned.
- The answer must match a JSON schema, field by field. An invalid answer gets one retry that says what was wrong. After that there is no suggestion and no exception.
- The assistant can only pick agents who are available and have room, so a model cannot assign work to someone who is away or does not exist.
- Ticket text is passed as data. Instructions written inside a ticket are not followed, and anything a model suggests still needs a person's approval.

### Choosing a model

Set environment variables; nothing is read from code and the key is never printed.

| Variable | Meaning |
|---|---|
| `TRIAGE_AI_PROVIDER` | `fake` (default), `ollama` or `openai-compatible` |
| `TRIAGE_AI_ENDPOINT` | Server address. Ollama defaults to `http://localhost:11434`. |
| `TRIAGE_AI_MODEL` | Model name, required for `ollama` and `openai-compatible` |
| `TRIAGE_AI_API_KEY` | Bearer token for a hosted service, if it needs one |

The `ollama` and `openai-compatible` clients are plain `HttpClient` code behind `Microsoft.Extensions.AI`'s `IChatClient`. **They are tested against a stub HTTP server only. I have not run them against a live Ollama or hosted model.** The browser demo can only use the fake model, because a static page cannot safely hold a key or reach your local server.

### The MCP server

`src/Triage.Mcp` exposes the help desk to Claude over the Model Context Protocol (standard input and output, official C# SDK `ModelContextProtocol.Core` 2.3.0). It hosts **its own demo desk with fake data on a frozen clock (Tuesday 10:30)**, so it does not share state with the browser demo, and suggestions live in memory until the process exits.

| Tool | What it does | Needs `confirm: true` |
|---|---|---|
| `list_tickets` | List tickets, filter by status and SLA state | no, read-only |
| `get_ticket` | One ticket with messages and pending suggestions | no, read-only |
| `summarize_ticket` | Summarize a ticket; stores nothing | no, read-only |
| `sla_breaches` | Overdue tickets, plus how many are at risk | no, read-only |
| `draft_reply` | Draft a reply and store it as a Pending suggestion | **yes** |
| `approve_suggestion` | Approve as a named agent. Approving a reply **sends it.** | **yes** |
| `reject_suggestion` | Reject a pending suggestion; sends nothing | **yes** |

A tool that stores or changes anything refuses unless `confirm` is `true`, and it checks that before it looks anything up. Ticket text is redacted in every result and requester addresses are never returned.

Build once with `dotnet build src/Triage.Mcp -c Release`, then register it.

**Claude Desktop**, in `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "triage": {
      "command": "dotnet",
      "args": ["C:/path/to/support-inbox-dotnet/src/Triage.Mcp/bin/Release/net8.0/Triage.Mcp.dll"],
      "env": { "TRIAGE_AI_PROVIDER": "fake" }
    }
  }
}
```

**Claude Code**:

```bash
claude mcp add --transport stdio triage -- dotnet /path/to/support-inbox-dotnet/src/Triage.Mcp/bin/Release/net8.0/Triage.Mcp.dll
```

Then ask something like *"Which tickets have breached their SLA? Summarize the worst one, then draft a reply."* Claude should ask before it sets `confirm`.

I checked the server by running the real executable and speaking the protocol to it over standard input and output (handshake, tool list, a refused call, a real result), and by the protocol-level tests below. I have not connected it to Claude Desktop or Claude Code myself, so treat the two snippets above as unrun configuration.

## How it is built

```
src/Triage.Engine   C#: business calendar, SLA policy, desk (tickets, assignment, merge, automation). No UI.
src/Triage.Copilot  Typed AI suggestions behind IChatClient, redaction, schema checks, approval gating
src/Triage.Mcp      MCP server (standard input and output) exposing the desk and the copilot
src/Triage.Web      Blazor WebAssembly front end (runs the same engine in the browser)
tests/              xUnit tests for the engine, the copilot and the MCP server
```

The clock is injected, so every time rule is tested by moving a simulated clock rather than waiting.

## Tests

```bash
dotnet test
```

329 tests: 237 for the engine, 65 for the copilot and 27 for the MCP server.

The MCP tests start the real server and a real MCP client joined by in-memory pipes, so they see exactly what a client would: the seven tools and their read-only and destructive hints, every state-changing tool refusing without `confirm: true` (and a test that fails if a future one lacks the argument), redaction in results, approve and reject flows, and bad ids and unknown agents.

The copilot tests cover redaction (including that nothing personal reaches the model), the size limit, schema-invalid answers with one retry, timeouts and failing providers, approval gating, and the settings and HTTP clients against a stub server.

The engine tests include:

- the calendar: every edge of the working day, weekends, holidays, custom hours, and `Add` and `Between` being exact inverses;
- each target at its exact boundary (the minute before, the deadline itself, the minute after) for both clocks and all priorities;
- pauses across nights, weekends and repeated waits, and priority changes that keep earlier pauses;
- assignment: round-robin, capacity, skills with fallbacks, queue ordering, away agents, and reassigning on return;
- the conversation rules, merging in every combination, follow-ups, and automation at its exact thresholds;
- a randomized test: 400 random actions across six seeds, after which no open ticket is held by an absent agent, message numbers are contiguous, merged tickets point at real ones, and pending status always matches the pause.

I also broke the engine's rules on purpose to check its tests notice (deadline off by one in each direction, waiting time not credited, a note counting as a reply, round-robin removed, specialists taking any work, merging across people, and more). 29 of 30 made tests fail, and one made the test run loop forever, which counts as noticed. The 30th (removing the round-robin tie-break) was not noticed until I added a test for it. I have not done that kind of deliberate-breakage check on the copilot or MCP code.

## Scope

Data lives in memory, so reloading resets the demo, and the copilot's suggestions are lost with it. Email intake, customer-facing portal, attachments and login are out of scope.
