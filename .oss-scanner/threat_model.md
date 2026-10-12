# Threat model

Microsoft Agentic Harness is a C# / .NET 10 template for building LLM agent hosts on the Microsoft Agent Framework. Consumers clone it and extend it, so the security controls (governance, sandboxing, egress filtering, tenant isolation) are the product as much as the features. The project's own security guide is in `documentation/security/` (start with `01-threat-model.html`); this file summarises it for the scanner.

## What this project does and where untrusted input enters

Treat as adversarial:

- **LLM output.** Model responses, including the tool calls and arguments the model chooses, are untrusted. Controls must hold even when the model is fully attacker-steered (prompt injection).
- **Anything the agent reads.** Web pages, retrieved documents (RAG), tool results, MCP server responses, MCP tool definitions, stored memory and knowledge-graph content can all carry planted instructions.
- **HTTP and WebSocket callers** of the hosts: `Presentation.ExecutionApi` (bundle/workflow API), `Presentation.AgentHub` (REST, SignalR, AG-UI streaming), `Presentation.FoundryHost`, and `Infrastructure.AI.MCPServer` (MCP server over HTTP with JWT auth). Callers may be authenticated but hostile to other tenants.
- **Externally authored agents, skills and plugins** (`AGENT.md`, `SKILL.md`, plugin manifests, bundles). These files are parsed and can declare tools, permissions and MCP servers.
- **Remote MCP servers** the harness connects to as a client.

## Components that matter most

Highest value, in order:

1. **Tool-call chokepoint and governance**: tool permission/denial rules, plugin `AllowedTools`/`DeniedTools` boundaries, autonomy levels, admission pipeline, call-once policy, `GovernanceArmer`. Any path that lets a tool run without passing every gate is a finding.
2. **Sandbox execution**: `ProcessSandboxExecutor`, `DockerSandboxExecutor`, attestation (HMAC), the closed-by-default capability model, IaC sandbox runner. Escapes, capability widening, and forged or unchecked attestations matter.
3. **Egress and SSRF defense**: outbound HTTP, MCP client connections, URL/redirect/DNS-rebinding handling. See `documentation/security/ssrf-defense.md`.
4. **Identity, tenancy and ownership**: `ClaimsPrincipalExtensions.GetUserIdOrNull()`, `KnowledgeScopeMiddleware`, `IConversationStore` ownership checks, `TenantIsolatedGraphStore`, `PlannerScopeFilter`. A null owner or scope means world-readable, so an authenticated request that proceeds unscoped is a cross-tenant leak.
5. **Authentication and authorization** on every host endpoint and on the MCP server (JWT validation, policy providers).
6. **Content safety, redaction and logging**: secret/PII redaction in telemetry and logs, tool-output sanitisation.
7. **Persistence**: SQLite/EF Core stores, Postgres, JSONL audit stores, path handling (state paths must not resolve outside the application base directory).

Lower priority: console UI (`Presentation.ConsoleUI`, `Presentation.LoggerUI`), eval runner, and example code. Still in scope, but they run with the operator's own privileges.

## How to exercise it

- Everything under `src/Content/` builds in the image at `/src/src/Content`. Hosts are the `Presentation.*` web projects; libraries are under `Application/`, `Domain/`, `Infrastructure/`.
- Tests are in `src/Content/Tests/`, built in Release. Some (Postgres-backed observability tests, Docker sandbox tests) need services the offline scanner does not have, so they cannot run there.
- Example agent, skill and plugin definitions are in `agents/`, `skills/` and `plugins/`.
- Hosts are standard ASP.NET Core apps and can be started from their built output with `dotnet run --project <host csproj> -c Release --no-build`. Agent turns that call a model need credentials the offline scanner does not have; the request-handling, auth and governance code is reviewable and testable without them.

## How you rate severity

- **Critical**: unauthenticated remote code execution; sandbox escape; bypass of tool governance that lets a model-chosen or attacker-supplied tool call run with no admission check; authentication bypass on a host or the MCP server.
- **High**: cross-tenant or cross-user read or write of conversations, knowledge-graph or planner data (including any path where an authenticated request runs unscoped); SSRF reaching internal or metadata addresses; path traversal outside the intended directory; forged or unverified sandbox attestation; secret disclosure in logs, telemetry or error responses.
- **Medium**: denial of service reachable by an authenticated caller; redaction gaps for a single secret shape; missing validation with limited impact.
- **Low**: hardening gaps with no demonstrated exploit path.

A finding that requires the operator to already hold the host's configuration secrets or filesystem access is out of scope unless it crosses a documented boundary (for example, a plugin escaping its declared `AllowedTools`).

## Reports

Include the file and symbol, the attacker-controlled input, the boundary crossed, and a proof of concept (a failing xUnit test against the real code path is ideal). Prefer a minimal patch that demonstrates the fix over a large refactor. Group findings by root cause: several call sites that share one missing check are one finding.

## Anything to leave alone

- `node_modules/`, `bin/`, `obj/` and generated files.
- Documentation sites under `documentation/` (static HTML) except where they embed secrets.
- Test fixtures and `eval-datasets/` content that is deliberately adversarial (prompt-injection corpora); do not report the payload strings themselves.
- The default `NotConfigured*` implementations that throw on first use; this is intentional.
- Development-only settings (`appsettings.Development.json`, `ASPNETCORE_ENVIRONMENT=Development` behaviour, localhost defaults) unless they leak into the Container or Production configuration.
