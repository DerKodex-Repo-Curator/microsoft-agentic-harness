using System.Text.Json;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Tools;
using Application.AI.Common.Tests.Governance;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.Common.Config.AI.DirectToolInvocation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// The container and test doubles behind <see cref="DirectToolInvokerMcpTests"/>, split out to keep each
/// file within the repo's size convention.
/// </summary>
public sealed partial class DirectToolInvokerMcpTests
{
    private const string Caller = "caller-1";
    private const string Server = "srv";
    private const string ToolName = "echo";
    private const string McpReportedBy = "direct-invocation-mcp";

    private readonly GovernorRecord _governor = new();
    private readonly DirectToolInvocationConfig _config = new() { McpEnabled = true };
    private readonly Mock<IApprovalExecutionReporter> _reporter = new();
    private readonly Mock<IMcpToolProvider> _provider = new();

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

    /// <summary>Asserts that no server was contacted by any of the provider's three lookup methods.</summary>
    private void AssertNoServerContacted()
    {
        _provider.Verify(p => p.GetToolsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _provider.Verify(p => p.GetAllToolsAsync(It.IsAny<CancellationToken>()), Times.Never);
        _provider.Verify(p => p.GetToolByNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

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
    /// caller's responsibility, so a test can describe several servers; <paramref name="withProvider"/>
    /// <see langword="false"/> builds the invoker with no MCP provider at all.
    /// </summary>
    private async Task<DirectToolInvocationOutcome> InvokeWithProvider(
        DirectMcpToolInvocationRequest request,
        CancellationToken cancellationToken = default,
        bool withProvider = true)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAgentExecutionContext, AgentExecutionContext>();
        services.AddScoped<IToolInvocationGovernor>(sp =>
            new RecordingGovernor(sp.GetRequiredService<IAgentExecutionContext>(), _governor));

        // Every gate is registered unconditionally: the chain requires all of them, and an absent gate
        // and a permissive one must never be confusable at runtime.
        services.AddSingleton(AdmissionHarness.PermissiveClassificationGate());
        // A mock reports HasObservers == false, so the chain never asks it for a decision.
        services.AddSingleton(Mock.Of<IToolCallObserverChain>());
        services.AddSingleton(AdmissionHarness.PermissiveProgressEvaluator());
        services.AddSingleton<IGovernanceTraceRecorder>(AdmissionHarness.TraceRecorder());
        services.AddSingleton(AdmissionHarness.PermissiveAuthorizationGate());
        services.AddSingleton(_reporter.Object);
        services.AddSingleton(AdmissionHarness.PermissiveSanitizer());
        services.AddSingleton<IContentRedactionFilter>(TestRedactionFilter.Instance);
        services.AddSingleton(AdmissionHarness.Config());
        services.AddSingleton(AdmissionHarness.StubResultStore());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddToolCallAdmissionChain();

        await using var provider = services.BuildServiceProvider();

        var sut = new DirectToolInvoker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ToolCatalog(provider, [], NullLogger<ToolCatalog>.Instance),
            new StaticOptionsMonitor<DirectToolInvocationConfig>(_config),
            NullLogger<DirectToolInvoker>.Instance,
            withProvider ? _provider.Object : null);

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

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
