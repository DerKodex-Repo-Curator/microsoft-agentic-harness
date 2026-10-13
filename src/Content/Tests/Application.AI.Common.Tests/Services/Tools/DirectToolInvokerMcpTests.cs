using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Tools;
using Domain.AI.Bundles;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;

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
/// chain, test-double governor and reporter) and lives in the <c>Fixture</c> partial. It is deliberately
/// not shared with that suite yet, so that suite stays untouched; the two copies of the gate registration
/// should be folded into one helper in a follow-up, or a new required gate will break only one of them.
/// </para>
/// </remarks>
public sealed partial class DirectToolInvokerMcpTests
{
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

    [Fact]
    public async Task ADeniedCall_NeverRunsTheTool_AndReportsNothing()
    {
        // A refusal must not write an execution record for a call that never executed.
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
        _reporter.Verify(r => r.ReportSucceededAsync(
            It.IsAny<ApprovedCall>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _reporter.Verify(r => r.ReportFailedAsync(
            It.IsAny<ApprovedCall>(), It.IsAny<PreparedFailureText>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
        AssertNoServerContacted();
    }

    [Fact]
    public async Task AToolTheEnvelopeDoesNotGrant_IsReportedAsAbsent_BeforeAnyServerIsContacted()
    {
        var envelope = new CapabilityEnvelope { AllowedTools = ["other"], AllowedMcpServers = [Server] };

        var outcome = await Invoke(Request(envelope), Function());

        outcome.Status.Should().Be(DirectToolInvocationStatus.NotFound);
        AssertNoServerContacted();
    }

    [Fact]
    public async Task ABlankToolName_IsRejectedAsInvalid_BeforeAnyServerIsContacted()
    {
        var outcome = await Invoke(Request() with { ToolName = "  " }, Function());

        outcome.Status.Should().Be(DirectToolInvocationStatus.Invalid);
        AssertNoServerContacted();
    }

    [Fact]
    public async Task WithNoMcpProviderOnTheHost_AGrantedToolIsReportedAsAbsent()
    {
        var outcome = await InvokeWithProvider(Request(), withProvider: false);

        outcome.Status.Should().Be(DirectToolInvocationStatus.NotFound);
    }

    [Fact]
    public async Task AServerTheEnvelopeDoesNotGrant_IsNeverContacted_ByAnyLookupMethod()
    {
        // An ungranted server must cost the caller a NotFound, not a side-effect connection or a schema
        // disclosure. GetToolByNameAsync and GetAllToolsAsync search every configured server regardless of
        // grant, so they are checked as well: an unconfigured loose mock answers them with null, which
        // would otherwise let a widening to either one pass unnoticed.
        var envelope = new CapabilityEnvelope { AllowedTools = [ToolName], AllowedMcpServers = ["granted-only"] };
        _provider.Setup(p => p.GetToolsAsync("granted-only", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<AITool>)new List<AITool>());
        _provider.Setup(p => p.GetToolsAsync(Server, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IList<AITool>)new List<AITool> { Function() });

        var outcome = await InvokeWithProvider(Request(envelope));

        outcome.Status.Should().Be(DirectToolInvocationStatus.NotFound);
        _provider.Verify(p => p.GetToolsAsync(Server, It.IsAny<CancellationToken>()), Times.Never);
        _provider.Verify(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()), Times.Never);
        _provider.Verify(p => p.GetToolByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

    // ---- governance arming and output shaping ---------------------------------------------------------

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
    public async Task AToolThatSucceeds_ReturnsItsOutput()
    {
        // The companion to the refusal tests: each would also pass against an invoker that refused everything.
        var outcome = await Invoke(Request(), Function(_ => Task.FromResult<object?>("hello")));

        outcome.Status.Should().Be(DirectToolInvocationStatus.Succeeded);
        outcome.Output.Should().Contain("hello");
    }

    [Fact]
    public async Task OutputBeyondTheCeiling_IsTruncated_WithTheMarkerCountedAgainstIt()
    {
        _config.MaxOutputCharacters = 40;

        var outcome = await Invoke(
            Request(), Function(_ => Task.FromResult<object?>(new string('x', 500))));

        outcome.Output!.Length.Should().BeLessThanOrEqualTo(40);
        outcome.OutputTruncated.Should().BeTrue();
    }
}
