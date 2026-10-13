# A2A Message Contract

PR-7 ships the harness Agent-to-Agent (A2A) surface — the common dispatch
shape every inter-skill call goes through, whether the callee is in the
same process or a remote one. The shape is identical across transports
so a consumer can split a skill into its own process later without
touching call sites.

## Why not a thin wrapper over MAF A2A?

The plan calls for "thin wrapper over MAF" but the core Microsoft Agent
Framework assemblies the harness loads (`Microsoft.Agents.AI`, current pin
1.24.0) do **not** ship an A2A client/server surface. The Linux Foundation
A2A protocol is young (June 2025) and the core assemblies publish no
primitives that the harness could wrap. PR-7 therefore ships a
harness-native A2A surface with the same goals — wire shape, identity
propagation, OTel span linking, mutual-TLS-plus-JWT auth — and pins the MAF
version via a canary test (`A2AVersionPinTests`) that fails if a loaded MAF
assembly gains an A2A surface.

Microsoft also publishes A2A and AG-UI hosting as separate preview packages
(`Microsoft.Agents.AI.Hosting.A2A`, `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`).
The harness does not load them in production. The decision on them is recorded
under "Evaluation: Microsoft's preview A2A hosting" at the end of this page,
and the AG-UI one in `ag-ui-hosting-evaluation.md`; a second canary
(`MafHostingPackagesCanaryTests`) keeps both decisions honest.

## Envelope

Every A2A call carries an `A2AEnvelope` plus a payload. The envelope is
JSON-serializable with explicit camelCase property names — no
serializer-config drift between processes.

```json
{
  "schemaVersion": 1,
  "correlationId": "ec0b3a4d6e6f4b5a8e9f0c1d2e3f4a5b",
  "callerAgentId": "sre-agent",
  "callerKind": "FederatedCredential",
  "calleeAgentId": "workspace-agent",
  "calleeSkill": "search-tickets",
  "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "extensions": { "tenantId": "contoso" }
}
```

| Field | Required | Purpose |
| --- | --- | --- |
| `schemaVersion` | yes | Envelope version; current `1`. Bump on any breaking shape change. |
| `correlationId` | yes | UUIDv4 (no hyphens). Shared across caller + callee spans. |
| `callerAgentId` | yes | Caller agent id from `IAgentExecutionContext.AgentIdentity.Id`. |
| `callerKind` | yes | Caller identity kind, e.g. `FederatedCredential`. |
| `calleeAgentId` | yes | Target agent id; resolves DNS / DI key on the callee side. |
| `calleeSkill` | no | Target skill name. Null → callee's default skill. |
| `traceparent` | no | W3C trace-context header value, captured client-side. |
| `extensions` | no | Vendor-extensible headers, bounded by `MaxExtensionHeaders`. |

## Request / response

```json
// Request
{
  "envelope": { ... },
  "taskDescription": "Find open SEV-2 tickets for tenant contoso",
  "input": { "tenantId": "contoso", "severity": 2 }
}

// Success response
{
  "correlationId": "ec0b3a4d...",
  "success": true,
  "output": { "tickets": [ ... ] }
}

// Failure response
{
  "correlationId": "ec0b3a4d...",
  "success": false,
  "errorCode": "a2a.auth_rejected",
  "errorMessage": null
}
```

Stable failure codes:

| Code | Meaning |
| --- | --- |
| `a2a.auth_rejected` | Auth provider rejected the inbound envelope (no JWT, bad signature, sub mismatch, etc). |
| `a2a.unsupported_envelope_version` | `schemaVersion` does not match server's supported version. |
| `a2a.too_many_extensions` | Extension dictionary exceeded `MaxExtensionHeaders`. |
| `a2a.identity_conflict` | Server's execution scope was already bound to a different identity. |
| `a2a.skill_not_found` | No keyed handler registered for `{calleeAgentId}` or `{calleeAgentId}:{calleeSkill}`. |
| `a2a.skill_failed` | Handler threw or returned a `Result.Fail`. |
| `a2a.cancelled` | Cooperative cancellation. |
| `a2a.bad_response` | Cross-process callee returned an unparseable body. |
| `a2a.transport_failure` | HTTP-level error (DNS, TLS, connection reset). |
| `a2a.http_4xx` / `a2a.http_5xx` | HTTP error not covered above. |
| `a2a.unknown_callee` | No `RemoteAgents` entry matches the envelope's `calleeAgentId`. |
| `a2a.unknown_transport` | Configured `Transport` value is invalid. |
| `a2a.auth_acquisition_failed` | Client-side JWT acquirer failed. |

Server-side exceptions are logged via structured logging and never
returned on the wire — error messages are nullable and used only for
human-readable diagnostics when safe.

## Identity propagation

The caller stamps `callerAgentId` from
`IAgentExecutionContext.AgentIdentity.Id`. The server reads it, runs the
auth provider, and establishes the **authoritative** caller id on its
own `IAgentExecutionContext` via `A2AIdentityPropagator`:

- **In-process transport**: the envelope's declared caller id is
  authoritative because both ends share the same trust boundary. The
  auth provider also checks that the envelope's declared id matches the
  ambient identity to catch scope-leak bugs.
- **Cross-process transport**: the JWT `sub` claim is authoritative.
  If the envelope's declared `callerAgentId` does not match the JWT
  `sub`, the server returns `a2a.auth_rejected` — a holder of a token
  for agent A cannot masquerade as agent B in the envelope.

The propagator sets the caller's `AgentIdentity` on the server-side
scope so all downstream tool calls in the handler are RBAC'd against
the original caller, not a synthetic "callee" identity.

## OpenTelemetry span linking

Both sides emit spans on `ActivitySource = "AgenticHarness.A2A"`.

| Attribute | Caller span | Callee span |
| --- | --- | --- |
| Span name | `a2a.client {calleeAgentId}` | `a2a.server {calleeSkill | calleeAgentId}` |
| Kind | `Client` | `Server` |
| `gen_ai.operation.name` | `invoke_a2a` | `invoke_a2a` |
| `gen_ai.a2a.caller.id` | yes | yes |
| `gen_ai.a2a.caller.kind` | yes | yes |
| `gen_ai.a2a.callee.id` | yes | yes |
| `gen_ai.a2a.callee.skill` | optional | optional |
| `gen_ai.a2a.correlation_id` | yes | yes |
| `gen_ai.a2a.transport` | `in_process` or `http` | `in_process` or `http` |
| `gen_ai.a2a.auth.scheme` | `ambient` or `mtls+jwt` | `ambient` or `mtls+jwt` |
| `gen_ai.a2a.envelope.version` | yes | yes |
| `gen_ai.a2a.error.code` | on failure | on failure |
| `error.type` | mirrors error code | mirrors error code |

The correlation id appears on both spans so log search joins the two
ends even when the W3C trace-context propagator is broken or stripped
by an intermediary. For cross-process calls, the caller also stamps
`traceparent` onto the envelope; the server extracts it onto the
inbound span as a standard parent link.

## Authentication

### In-process (`A2ATransport.InProcess`)

- Scheme name: `ambient`.
- Trusts `IAgentExecutionContext.AgentIdentity` because the call never
  leaves the process boundary.
- Server-side: validates the envelope's declared caller matches the
  ambient identity (when one is set). A mismatch is a scope-leak bug
  and surfaces as `a2a.auth_rejected`.

### Cross-process (`A2ATransport.Http`)

- Scheme name: `mtls+jwt`.
- **Layer 1 — mutual TLS**: Kestrel terminates the connection with peer
  certificate authentication. Untrusted certs are rejected before any
  harness code runs. The harness expects Kestrel to be configured per
  the security playbook (cert thumbprint allowlist; revocation check
  enabled).
- **Layer 2 — workload identity JWT**: the client attaches
  `Authorization: Bearer <jwt>` via the consumer-supplied
  `IA2ATokenAcquirer`. The server validates via the
  consumer-supplied `IA2ATokenValidator`:
  - signature against the configured key material;
  - `iss` against `A2ASurfaceConfig.ExpectedIssuer`;
  - `aud` against `A2ASurfaceConfig.ExpectedAudience`;
  - `exp` with clock skew = `A2ASurfaceConfig.ClockSkewSeconds`
    (defaults to `0` — never widen without a recorded design decision);
  - revocation status against any configured revocation list.
- **Layer 3 — envelope match**: the JWT's `sub` claim must equal the
  envelope's declared `callerAgentId`. JWT sub is authoritative.

Why mTLS + JWT instead of JWT alone? mTLS pins the connection
**workload** (which Kestrel can verify cryptographically) and JWT pins
the **agent identity** running inside that workload (which the
identity provider issues). Separately validated, separately revocable.
The harness expresses this as two distinct trust layers rather than
folding identity into the connection cert.

## Transport selection

`AppConfig.AI.A2A.Surface.Transport`:

- `InProcess` (default): same-process dispatch via the in-process
  bridge — no HTTP, no JWT acquisition. Example demos and unit tests
  use this transport.
- `Http`: cross-process dispatch. Requires the consumer to register an
  `IA2ATokenAcquirer` and `IA2ATokenValidator` and to set
  `ExpectedAudience` + `ExpectedIssuer`.

`A2ASurfaceConfig.MaxExtensionHeaders` (default `16`) bounds the
extension dictionary the server will deserialize — protects against
malicious payload inflation.

## Skill handler registration

The server resolves a handler via keyed DI:

```csharp
// Default skill for the agent
services.AddKeyedScoped<IA2ASkillHandler, MyAgentHandler>("sre-agent");

// Named skill
services.AddKeyedScoped<IA2ASkillHandler, MySearchHandler>("workspace-agent:search-tickets");
```

`HarnessA2AServer` looks up the keyed handler in this precedence:

1. `{calleeAgentId}:{calleeSkill}` if `CalleeSkill` is non-null;
2. `{calleeAgentId}` otherwise.

Missing handlers surface as `a2a.skill_not_found`.

## Version pin

`Infrastructure.AI.Tests/A2A/A2AVersionPinTests` asserts:

- The loaded core `Microsoft.Agents.AI` assemblies (1.24.0) do **not** expose a
  public A2A surface. This covers the core assemblies only; Microsoft's A2A
  and AG-UI hosting live in separate preview packages, covered next.
- `A2AEnvelope.CurrentSchemaVersion == 1` — bumping requires updating
  the test and writing a migration note.

`Infrastructure.AI.MafHostingCanary.Tests/MafHostingPackagesCanaryTests`
covers Microsoft's separate A2A and AG-UI packages. That small project
references the four packages (nothing else does; a conditional
`PackageVersion` in `Directory.Packages.props` makes a production reference
fail restore, and a test reads the project files for the same IDs), pinned
beside `Microsoft.Agents.AI`. For each it asserts that the package is still
a preview and that its public types are exactly the ones the evaluation
below read. A Microsoft.Agents.AI bump therefore cannot pass unnoticed: it
either changes the surface (the test fails and the evaluation is repeated)
or it does not (the decision stands). It reads what the *pinned* version
contains, so a stable release upstream is noticed when the pin next moves,
not before, and a Microsoft package it does not reference is not seen at all.

When the evaluation's answer changes:

1. The failing test makes the situation visible.
2. Repeat the comparison in the evaluation below, record the outcome here.
3. If wrapping wins, replace `HarnessA2AClient` / `HarnessA2AServer` with thin
   wrappers over the Microsoft types, keeping the harness `IA2AClient` /
   `IA2AServer` call-site shape unchanged.
4. Keep the envelope conventions — they encode harness-specific
   identity + correlation semantics that Microsoft's transport will not
   replicate.

## Evaluation: Microsoft's preview A2A hosting

Evaluated 2026-10-11 against three of the four Microsoft packages the canary
pins (the fourth, `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`, is covered in
[ag-ui-hosting-evaluation.md](ag-ui-hosting-evaluation.md)), all
1.24.0-preview.261006.1, all built on the community `A2A` SDK
1.0.0-preview2 (the `a2aproject` .NET SDK, not a Microsoft package):

| Package | Role | Public surface (types) |
|---|---|---|
| `Microsoft.Agents.AI.A2A` | **Client**: a remote A2A endpoint as an `AIAgent` | `A2AAgent`, `A2AAgentOptions`, `A2AAgentSession`, `AsAIAgent` / `GetAIAgentAsync` extensions |
| `Microsoft.Agents.AI.Hosting.A2A` | **Server registration**: publish a registered agent over A2A | `AddA2AServer`, `A2AServerRegistrationOptions`, `A2ARunDecisionContext`, `AgentRunMode`, `IsolationKeyScopedTaskStore` |
| `Microsoft.Agents.AI.Hosting.A2A.AspNetCore` | **Endpoint mapping** | `MapA2AHttpJson`, `MapA2AJsonRpc` |

(The first version of this evaluation read only the second package and
concluded there was no client; the security review caught the miss. The four
are now all pinned by the canary, and the canary's test project is the only
place they are referenced.)

**Decision: keep the harness-native transport for inter-skill dispatch. Do
not replace it with these packages.**

The reason is purpose, not missing features. Microsoft's packages model *a
remote agent behind the public A2A protocol*: an agent card, protocol
messages and tasks, a client that calls it, an endpoint that serves it. The
harness contract models *dispatch between skills of this system*: a
keyed-DI handler registry (`{agentId}:{skill}`), a schema-versioned envelope
carrying correlation and caller identity, one call shape whether the callee
is in the same process or another, and an in-process transport with no
network at all. Replacing it means mapping the envelope onto protocol
messages at both ends and giving up the in-process path or re-creating it
beside the package; the call-site shape (`IA2AClient` / `IA2AServer`) could be
kept, which is why this is a decision about the implementation, not the
contract.

What Microsoft's packages do **not** decide for us, because they are host
concerns the package leaves to the host, not things it forbids: mutual TLS
plus JWT between processes, propagating the calling agent's identity, and
linking OpenTelemetry spans across the hop. All three can be done around an
`HttpClient` and an ASP.NET Core pipeline, but they would be re-implemented
around the package rather than provided by it, so the harness's existing
implementations (`A2AIdentityPropagator`, `A2ASpanEmitter`, the
authentication providers) stay either way.

One thing in the package is genuinely relevant and not compared: per-caller
isolation. `IsolationKeyScopedTaskStore` (verified in the surface above)
scopes stored tasks by an isolation key. How that compares with the
harness's caller-refusal rule for conversations (`IConversationStore`
refuses a record owned by anyone else) was **not** examined.

What the packages would add that the harness transport deliberately does
not: **interoperability with agents outside this system**. If the harness
ever needs to call, or be called by, an outside A2A-speaking agent, the
natural route is `Microsoft.Agents.AI.A2A` for outbound (a remote agent
appears as an `AIAgent`, usable as a delegate or a Magentic participant) and
`AddA2AServer` plus `MapA2AJsonRpc` for inbound, added beside this contract,
not in place of it. That is a feature decision for when the need exists.

Not verified: a wire-level comparison (the harness envelope was not diffed
against the protocol's message shape), the meaning of `AgentRunMode` and
`A2ARunDecisionContext` beyond their names, and behaviour under load. The
decision rests on public-surface, dependency and purpose evidence, not on
running the packages. Package discovery used the NuGet search index and the
packages' own metadata, not a complete listing of everything Microsoft
publishes.
