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

## How it is built

```
src/Triage.Engine   C#: business calendar, SLA policy, desk (tickets, assignment, merge, automation). No UI.
src/Triage.Web      Blazor WebAssembly front end (runs the same engine in the browser)
tests/              xUnit tests
```

The clock is injected, so every time rule is tested by moving a simulated clock rather than waiting.

## Tests

```bash
dotnet test
```

236 tests, including:

- the calendar: every edge of the working day, weekends, holidays, custom hours, and `Add` and `Between` being exact inverses;
- each target at its exact boundary (the minute before, the deadline itself, the minute after) for both clocks and all priorities;
- pauses across nights, weekends and repeated waits, and priority changes that keep earlier pauses;
- assignment: round-robin, capacity, skills with fallbacks, queue ordering, away agents, and reassigning on return;
- the conversation rules, merging in every combination, follow-ups, and automation at its exact thresholds;
- a randomized test: 400 random actions across six seeds, after which no open ticket is held by an absent agent, message numbers are contiguous, merged tickets point at real ones, and pending status always matches the pause.

I also broke the rules on purpose to check the tests notice (deadline off by one in each direction, waiting time not credited, a note counting as a reply, round-robin removed, specialists taking any work, merging across people, and more). MUTATION_PLACEHOLDER

## Scope

Data lives in memory, so reloading resets the demo. Email intake, customer-facing portal, attachments and login are out of scope.
