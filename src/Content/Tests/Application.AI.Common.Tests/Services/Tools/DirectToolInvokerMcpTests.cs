using System.Text.Json;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Application.AI.Common.Tests.Governance;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.Common.Config.AI.DirectToolInvocation;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Fast, in-process tests for <see cref="DirectToolInvoker.InvokeMcpToolAsync"/> (#546), the MCP twin of
/// <see cref="DirectToolInvokerTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// Until now the MCP path was covered only by <c>McpControllerTests</c>, a full HTTP suite whose harness
/// wires no approval routing. Two properties therefore survived mutation untouched there, and both are
/// asserted here:
/// </para>
/// <list type="bullet">
///   <item><description>
///   The execution report names its surface. Swapping the MCP constant <c>direct-invocation-mcp</c> for the
///   keyed-DI one <c>direct-invocation</c> misattributes every audit record from this surface.
///   </description></item>
///   <item><description>
///   A caller who disconnects surfaces as a cancellation, not a timeout. Passing
///   <see cref="CancellationToken.None"/> into the shared arming skeleton instead of the caller's token
///   would make a vanished client look like a slow tool.
///   </description></item>
/// </list>
/// <para>
/// The container is assembled the way <see cref="DirectToolInvokerTests"/> assembles it (real admission
/// chain, test-double governor, sanitizer and reporter) rather than extracted into a shared fixture, so
/// that suite stays untouched; a later change could fold the two registrations into one helper.
/// </para>
/// </remarks>
public sealed class DirectToolInvokerMcpTests
{
    private const string Caller = "caller-1";
    private const string Server = "srv";
    private const string ToolName = "echo";
    private const string McpReportedBy = "direct-invocation-mcp";

    private readonly GovernorRecord _governor = new();
    private readonly DirectToolInvocationConfig _config = new() { McpEnabled = true };
    private readonly Mock<IApprovalExecutionReporter> _reporter = new();
    private readonly Mock<IMcpToolProvider> _provider = new();

    // ---- execution reporting (the surface-attribution property) ------------------------------------

    [Fact]
    public async Task ApprovedCall_ThatSucceeds_ReportsSucceededUnderTheMcpSurfaceName()
    {
        var call = ApprovedCall();
        _governor.Decision = ToolInvocationDecision.Allow(call);

        await Invoke(Request(), Function());

        _reporter.Verify(r => r.ReportSucceededAsync(call, McpReportedBy, CancellationToken.None), Times.Once);
        _reporter.Verify(r => r.ReportSucceededAsync(
            It.IsAny<ApprovedCall>(), "direct-invocation", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnapprovedCall_ThatSucceeds_ReportsNothing()
    {
        await Invoke(Request(), Function());

        _reporter.Verify(r => r.ReportSucceededAsync(
            It.IsAny<ApprovedCall>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApprovedCall_WhoseToolReportsIsError_ReportsFailedUnderTheMcpSurfaceName()
    {
        var call = ApprovedCall();
        _governor.Decision = ToolInvocationDecision.Allow(call);

        var outcome = await Invoke(Request(), Function(_ => Task.FromResult<object?>(IsErrorResult("disk full"))));

        outcome.Status.Should().Be(DirectToolInvocationStatus.ToolFailed);
        _reporter.Verify(r => r.ReportFailedAsync(
            call, It.IsAny<PreparedFailureText>(), McpReportedBy, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task ApprovedCall_WhoseToolThrows_ReportsFailedUnderTheMcpSurfaceName_AndFaults()
    {
        var call = ApprovedCall();
        _governor.Decision = ToolInvocationDecision.Allow(call);

        var outcome = await Invoke(
            Request(), Function(_ => throw new InvalidOperationException("boom")));

        // Reporting closes the approval loop but must never swallow or replace the caller-facing result.
        outcome.Status.Should().Be(DirectToolInvocationStatus.Faulted);
        _reporter.Verify(r => r.ReportFailedAsync(
            call, It.IsAny<PreparedFailureText>(), McpReportedBy, CancellationToken.None), Times.Once);
    }

    // ---- cancellation and deadlines ------------------------------------------------------------------

    [Fact]
    public async Task ACallerWhoDisconnects_Cancels_RatherThanTimingOut()
    {
        // Nobody is waiting for an answer, so the invocation unwinds instead of manufacturing one.
        using var caller = new CancellationTokenSource();
        var function = Function(async ct =>
        {
            await caller.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return null;
        });

        var act = async () => await Invoke(Request(), function, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AToolThatOutrunsItsDeadline_IsReportedAsATimeout_NotAFault()
    {
        _config.InvocationTimeout = TimeSpan.FromMilliseconds(50);
        var function = Function(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return null;
        });

        var outcome = await Invoke(Request(), function);

        outcome.Status.Should().Be(DirectToolInvocationStatus.TimedOut);
    }

    [Fact]
    public async Task ASlowAuthorization_HitsTheDeadlineToo_AndTheToolNeverRuns()
    {
        _config.InvocationTimeout = TimeSpan.FromMilliseconds(50);
        _governor.Delay = TimeSpan.FromSeconds(20);
        var executions = 0;
        var function = Function(_ =>
        {
            executions++;
            return Task.FromResult<object?>("ok");
        });

        var outcome = await Invoke(Request(), function);

        outcome.Status.Should().Be(DirectToolInvocationStatus.TimedOut);
        executions.Should().Be(0);
    }

    [Fact]
    public async Task ARequestedTimeout_ShorterThanTheCeiling_IsHonoured()
    {
        var function = Function(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return null;
        });

        var outcome = await Invoke(
            Request() with { RequestedTimeout = TimeSpan.FromMilliseconds(50) }, function);

        outcome.Status.Should().Be(DirectToolInvocationStatus.TimedOut);
    }

    // ---- pre-flight refusals: the tool never runs and the right servers are (not) contacted ----------

    [Fact]
    public async Task WhileMcpInvocationIsDisabled_EveryCallIsRefused_AndNoServerIsContacted()
    {
        _config.McpEnabled = false;

        var outcome = await Invoke(Request(), Function());

        outcome.Status.Should().Be(DirectToolInvocationStatus.Disabled);
        _provider.Verify(p => p.GetToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AToolTheEnvelopeDoesNotGrant_IsReportedAsAbsent_BeforeAnyServerIsContacted()
    {
        var envelope = new CapabilityEnvelope { AllowedTools = ["other"], AllowedMcpServers = [Server] };

        var outcome = await Invoke(Request(envelope), Function());

        outcome.Status.Should().Be(DirectToolInvocationStatus.NotFound);
        _provider.Verify(p => p.GetToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AServerTheEnvelopeDoesNotGrant_IsNeverContacted()
    {
        // An ungranted server must cost the caller a NotFound, not a side-effect connection or a schema
        // disclosure — the SSRF-safe property the resolver exists to keep.
        var envelope = new CapabilityEnvelope { AllowedTools = [ToolName], AllowedMcpServers = ["granted-only"] };
        var function = Function();
        _provider.Setup(p => p.GetToolsAsync("granted-only", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<AITool>)new List<AITool>());
        _provider.Setup(p => p.GetToolsAsync(Server, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<AITool>)new List<AITool> { function });

        var outcome = await InvokeWithProvider(Request(envelope));

        outcome.Status.Should().Be(DirectToolInvocationStatus.NotFound);
        _provider.Verify(p => p.GetToolsAsync(Server, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AGrantedServerThatCannotBeReached_CostsOnlyThatServer()
    {
        var envelope = new CapabilityEnvelope { AllowedTools = [ToolName], AllowedMcpServers = ["down", Server] };
        _provider.Setup(p => p.GetToolsAsync("down", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"));
        _provider.Setup(p => p.GetToolsAsync(Server, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<AITool>)new List<AITool> { Function() });

        var outcome = await InvokeWithProvider(Request(envelope));

        outcome.Status.Should().Be(DirectToolInvocationStatus.Succeeded);
    }

    [Fact]
    public async Task AToolName_IsMatchedWithoutRegardToCase()
    {
        var envelope = new CapabilityEnvelope { AllowedTools = ["ECHO"], AllowedMcpServers = [Server] };

        var outcome = await Invoke(Request(envelope) with { ToolName = "ECHO" }, Function());

        outcome.Status.Should().Be(DirectToolInvocationStatus.Succeeded);
    }

    [Fact]
    public async Task ACallerWhoseIdentityCannotBeAPermissionSubject_IsRefused()
    {
        var outcome = await Invoke(Request() with { OwnerId = "who am i?" }, Function());

        outcome.Status.Should().Be(DirectToolInvocationStatus.IdentityUnusable);
    }

    [Fact]
    public async Task TooManyArguments_AreRejected_AsInvalid()
    {
        _config.MaxParameterCount = 1;
        var arguments = new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 };

        var outcome = await Invoke(Request() with { Arguments = arguments }, Function());

        outcome.Status.Should().Be(DirectToolInvocationStatus.Invalid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3600)]
    public async Task ARequestedTimeout_OutsideTheAllowedRange_IsRejected_AsInvalid(int seconds)
    {
        var outcome = await Invoke(
            Request() with { RequestedTimeout = TimeSpan.FromSeconds(seconds) }, Function());

        outcome.Status.Should().Be(DirectToolInvocationStatus.Invalid);
    }

    // ---- governance arming ------------------------------------------------------------------------------

    [Fact]
    public async Task ArmsTheCallersEnvelope_AroundTheToolCall()
    {
        // Arming it is what switches the governor's enforcement on; without it the call runs ungoverned.
        CapabilityEnvelope? observed = null;
        var envelope = new CapabilityEnvelope { AllowedTools = [ToolName], AllowedMcpServers = [Server] };
        var function = Function(_ =>
        {
            observed = CapabilityEnvelopeAccessor.Current;
            return Task.FromResult<object?>("ok");
        });

        await Invoke(Request(envelope), function);

        observed.Should().BeSameAs(envelope);
    }

    [Fact]
    public async Task EstablishesACallerIdentity_BeforeGovernanceRuns_AndKeepsTwoCallersApart()
    {
        await Invoke(Request() with { OwnerId = "alice" }, Function());
        var alice = _governor.AgentIdWhenAuthorizing;

        await Invoke(Request() with { OwnerId = "bob" }, Function());

        alice.Should().NotBeNullOrWhiteSpace();
        _governor.AgentIdWhenAuthorizing.Should().NotBeNullOrWhiteSpace().And.NotBe(alice);
    }

    [Fact]
    public async Task ADeniedCall_NeverRunsTheTool()
    {
        _governor.Decision = ToolInvocationDecision.Deny("not permitted");
        var executions = 0;
        var function = Function(_ =>
        {
            executions++;
            return Task.FromResult<object?>("ok");
        });

        var outcome = await Invoke(Request(), function);

        outcome.Status.Should().Be(DirectToolInvocationStatus.Denied);
        executions.Should().Be(0);
    }

    [Fact]
    public async Task AToolThatSucceeds_ReturnsItsOutput()
    {
        // The companion to the refusal tests: each would also pass against an invoker that refused everything.
        var outcome = await Invoke(Request(), Function(_ => Task.FromResult<object?>("hello")));

        outcome.Status.Should().Be(DirectToolInvocationStatus.Succeeded);
        outcome.Output.Should().Contain("hello");
    }

    // ---- fixture ------------------------------------------------------------------------------------------

    private static ApprovedCall ApprovedCall() =>
        new(Guid.NewGuid(), new ApprovalFailureKey("conv-1", Caller, ToolName));

    private static JsonElement IsErrorResult(string text) =>
        JsonDocument.Parse(
                $$"""{"isError":true,"content":[{"type":"text","text":"{{text}}"}]}""")
            .RootElement.Clone();

    private static DirectMcpToolInvocationRequest Request(CapabilityEnvelope? envelope = null) =>
        new()
        {
            ToolName = ToolName,
            OwnerId = Caller,
            Envelope = envelope ?? new CapabilityEnvelope { AllowedTools = [ToolName], AllowedMcpServers = [Server] },
        };

    /// <summary>An MCP-published function whose body the test dictates; defaults to returning "ok".</summary>
    private static AIFunction Function(Func<CancellationToken, Task<object?>>? body = null) =>
        AIFunctionFactory.Create(
            async (CancellationToken ct) => await (body?.Invoke(ct) ?? Task.FromResult<object?>("ok")),
            ToolName,
            "MCP-published fake");

    private Task<DirectToolInvocationOutcome> Invoke(
        DirectMcpToolInvocationRequest request, AIFunction function, CancellationToken cancellationToken = default)
    {
        _provider.Setup(p => p.GetToolsAsync(Server, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<AITool>)new List<AITool> { function });

        return InvokeWithProvider(request, cancellationToken);
    }

    /// <summary>
    /// Builds the invoker over a real container so the scope, the scoped execution context and the
    /// admission chain are the production ones, then runs the request. The provider's setups are the
    /// caller's responsibility, so a test can describe several servers.
    /// </summary>
    private async Task<DirectToolInvocationOutcome> InvokeWithProvider(
        DirectMcpToolInvocationRequest request, CancellationToken cancellationToken = default)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddScoped<IToolInvocationGovernor>(sp =>
            new RecordingGovernor(sp.GetRequiredService<IAgentExecutionContext>(), _governor));

        // Every gate is registered unconditionally: the chain requires all of them, and an absent gate
        // and a permissive one must never be confusable at runtime.
        services.AddSingleton(AdmissionHarness.PermissiveClassificationGate());
        services.AddSingleton<IToolCallObserverChain>(new NoObservers());
        services.AddSingleton(AdmissionHarness.PermissiveProgressEvaluator());
        services.AddSingleton<IGovernanceTraceRecorder>(AdmissionHarness.TraceRecorder());
        services.AddSingleton(AdmissionHarness.PermissiveAuthorizationGate());
        services.AddSingleton(_reporter.Object);
        services.AddSingleton<ICompositeResponseSanitizer>(new PassThroughSanitizer());
        services.AddSingleton<IContentRedactionFilter>(TestRedactionFilter.Instance);
        services.AddSingleton<IOptionsMonitor<Domain.Common.Config.AppConfig>>(
            new StaticOptionsMonitor<Domain.Common.Config.AppConfig>(new Domain.Common.Config.AppConfig()));
        services.AddSingleton(AdmissionHarness.StubResultStore());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddToolCallAdmissionChain();

        var provider = services.BuildServiceProvider();

        var sut = new DirectToolInvoker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ToolCatalog(provider, [], NullLogger<ToolCatalog>.Instance),
            new StaticOptionsMonitor<DirectToolInvocationConfig>(_config),
            NullLogger<DirectToolInvoker>.Instance,
            _provider.Object);

        return await sut.InvokeMcpToolAsync(request, cancellationToken);
    }

    private sealed class GovernorRecord
    {
        public ToolInvocationDecision Decision { get; set; } = ToolInvocationDecision.Allow();
        public string? AgentIdWhenAuthorizing { get; set; }
        public TimeSpan Delay { get; set; }
    }

    private sealed class RecordingGovernor(IAgentExecutionContext executionContext, GovernorRecord record)
        : IToolInvocationGovernor
    {
        public async ValueTask<ToolInvocationDecision> AuthorizeAsync(
            string toolName, CancellationToken cancellationToken,
            IReadOnlyDictionary<string, object?>? arguments = null,
            ToolCompositionTaint? composition = null,
            Domain.AI.Sandbox.ToolCallResourceRequest? resourceRequest = null)
        {
            record.AgentIdWhenAuthorizing = executionContext.AgentId;

            if (record.Delay > TimeSpan.Zero)
                await Task.Delay(record.Delay, cancellationToken);

            return record.Decision;
        }
    }

    private sealed class NoObservers : IToolCallObserverChain
    {
        public bool HasObservers => false;

        public ValueTask<ToolInvocationDecision> EvaluateAsync(
            string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ToolInvocationDecision.Allow());
    }

    private sealed class PassThroughSanitizer : ICompositeResponseSanitizer
    {
        public SanitizationResult Sanitize(string content, string? toolName = null) =>
            SanitizationResult.Clean(content);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
