# AG-UI Hosting: Microsoft's Preview Package vs the Harness's Own

Evaluated 2026-10-11. Companion to the A2A evaluation in
`a2a-message-contract.md`; the same canary
(`Infrastructure.AI.MafHostingCanary.Tests/MafHostingPackagesCanaryTests`) keeps both
decisions honest.

## Decision

1. **Do not adopt `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` for the
   run endpoint.** Keep `Presentation.AgentHub/AgUi/`.
2. **The stable protocol packages underneath it are worth a scoped spike,
   not a decision yet:** `AGUI.Abstractions` 1.0.0 (the protocol's event and
   message types) and `AGUI.Server` 1.0.0 (an adapter from
   `Microsoft.Extensions.AI` chat streams to AG-UI events). Both are 1.0 and
   come from the AG-UI protocol project, not from Agent Framework's preview
   line.

## What was evaluated

| Package | Version | Status | Public surface |
|---|---|---|---|
| `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` | 1.24.0-preview.261006.1 | preview | `AddAGUIServer`, `MapAGUIServer` (three overloads) |
| `AGUI.Server` | 1.0.0 | stable | stream-to-event adapter, `AGUIStreamOptions`, request-context helpers |
| `AGUI.Abstractions` | 1.0.0 | stable | the protocol's event types (run, step, text message, tool call, state, reasoning, subagent, and others), message and tool types, JSON converters |

Also seen in the NuGet search index, not inspected: `AGUI.Client`,
`AGUI.Formatting` and `AGUI.Protobuf` (all 1.0.0, from the AG-UI protocol
project) and `Microsoft.Agents.AI.AGUI` (a Microsoft client package, still at
1.13.0-preview, so it has not moved with the 1.24 line). The protocol
project's own stable 1.0 packages look like the maintained surface; the
Microsoft preview wrapper trails them. This list comes from a search, not a
full enumeration.

## Why not the hosting package

`MapAGUIServer` maps an endpoint that runs one registered agent and streams
its output as AG-UI events. The harness's run endpoint
(`AgUiRunHandler`, about 520 lines inside a 2,357-line AG-UI layer) does the
work *around* that run, and none of it is in the package's surface:

- conversation ownership: every store read passes the authenticated caller
  and the store refuses a conversation someone else owns. (Agent Framework's
  hosting layer has its own per-caller scoping, an isolation key with a
  strict mode; how that compares with this refusal rule was not examined, so
  this is a difference in where the rule lives, not a verified gap);
- a leased turn per conversation, so two runs cannot interleave;
- running the turn as a command through the mediator pipeline (validation,
  content safety, budget, governance) rather than as a bare agent run;
- the harness's own events: plan progress, drift, escalation and approval
  requests, Magentic workflow progress, client-tool round trips, and the
  pending-tool-call registry that correlates them.

Putting the package in front would mean re-wrapping every one of those around
it, and the endpoint would then have two owners of the run lifecycle. It is
also a preview.

## Why the stable protocol packages are interesting

The harness hand-writes its AG-UI event shapes (`AgUiEventType`,
`AgUiEvents`, and one file per event family). `AGUI.Abstractions` already
defines the protocol's standard events and JSON converters. Adopting it
would replace the standard-event DTOs with the protocol's own, which keeps
the wire format aligned with other AG-UI clients as the protocol evolves,
and leave the harness's own events as protocol `CustomEvent`s.

## What a spike has to answer first (not verified)

None of this was tested; the decision above rests on public-surface and
dependency evidence only.

- **Wire compatibility:** does the harness's current JSON, field for field,
  match what `AGUI.Abstractions` writes for the standard events? The two
  browser clients (React and Angular) parse the current shape.
- **Polymorphism:** the harness registers its event types with
  `[JsonDerivedType]`; the package uses its own converter
  (`BaseEventJsonConverter`). Mixing the two on one stream needs proving.
- **Custom events:** the harness's families (plan, drift, escalation,
  Magentic) would have to ride as `CustomEvent`, which changes their type
  names on the wire. Unknown event types are ignored by both clients today,
  so this is a rendering change to coordinate, not a break.
- **Dependency cost:** `AGUI.Abstractions` brings `System.Text.Json` and
  `Microsoft.Extensions.AI.Abstractions` (which the harness already uses);
  `AGUI.Server` adds the chat-stream-to-event adapter the harness does not
  need.

If the spike shows the wire shape matches, adoption is a contained refactor
of the event DTOs with no change to the run lifecycle. If it does not, the
cost is the clients, and that is the reason to stop.
