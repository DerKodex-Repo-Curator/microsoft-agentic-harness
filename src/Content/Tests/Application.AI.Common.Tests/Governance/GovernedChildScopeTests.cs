using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Governance;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Application.AI.Common.Tests.Governance;

/// <summary>
/// The governed child scope handle owns the whole lifecycle of a child governance scope: open and arm it,
/// publish its pipeline, and on the way out fold its trace into the parent's before the scope goes. The
/// closing half used to be hand-written at each surface, and a surface that forgot the fold re-created #771
/// (a tool the child was denied vanishing from the turn's reported trace).
/// </summary>
public sealed class GovernedChildScopeTests : IAsyncDisposable
{
    private const string Child = "child-agent";

    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _parentScope;
    private readonly IAgentExecutionContext _parentContext;
    private readonly GovernanceTraceRecorder _parentTrace = new(
        Mock.Of<IOptionsMonitor<GovernanceConfig>>(m => m.CurrentValue == new GovernanceConfig()),
        Mock.Of<IToolRiskClassifier>());
    private readonly List<Mock<IToolCallAdmissionPipeline>> _pipelines = [];
    private readonly List<ScopeMarker> _markers = [];
    private Func<GovernanceTrace> _childTrace = () => GovernanceTrace.Empty;
    private Action? _onGetTrace;
    private bool _pipelineResolutionFails;

    public GovernedChildScopeTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopeMarker>();
        // Resolving the marker here means every scope - the parent's, then each child's - records its marker
        // as the context is created, which arming does before it reaches the pipeline.
        services.AddScoped<IAgentExecutionContext>(sp =>
        {
            _markers.Add(sp.GetRequiredService<ScopeMarker>());
            return new AgentExecutionContext();
        });
        services.AddScoped(sp =>
        {
            if (_pipelineResolutionFails)
                throw new InvalidOperationException("pipeline not registered");

            var pipeline = new Mock<IToolCallAdmissionPipeline>();
            pipeline.Setup(p => p.GetTrace()).Returns(() =>
            {
                _onGetTrace?.Invoke();
                return _childTrace();
            });
            _pipelines.Add(pipeline);
            return pipeline.Object;
        });
        _provider = services.BuildServiceProvider();

        _parentScope = _provider.CreateAsyncScope();
        _parentContext = _parentScope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
        _parentContext.Initialize("parent-agent", "conv-1", 3, "conv-1");
    }

    public async ValueTask DisposeAsync()
    {
        await _parentScope.DisposeAsync();
        await _provider.DisposeAsync();
    }

    /// <summary>The first marker belongs to the parent's scope; the child's is the next one created.</summary>
    private ScopeMarker ChildMarker => _markers[1];

    private GovernedChildScope Open(IGovernanceTraceRecorder? parentTrace = null) => GovernanceArmer.OpenChild(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        Child,
        GovernanceArmingPolicy.Delegation,
        _parentContext,
        "fallback-scope",
        parentTrace);

    private static GovernanceTrace Trace(string reason) => new()
    {
        EnforcementEnabled = true,
        ToolDecisions =
        [
            new ToolDecisionRecord(
                "file_system", ToolDecisionOutcome.Denied, reason, BlastRadius.Low,
                RequiredApproval: false, ApprovalGranted: false, Enforced: true),
        ],
    };

    [Fact]
    public async Task Open_ArmsTheChildAsItsOwnAgent_WithAResetPipeline()
    {
        await using var child = Open();

        child.AgentId.Should().Be(Child);
        child.Context.AgentId.Should().Be(Child);
        child.Context.ConversationId.Should().Be("conv-1", "the Delegation policy inherits the parent's session");
        child.Pipeline.Should().BeSameAs(_pipelines.Single().Object);
        _pipelines.Single().Verify(p => p.Reset(), Times.Once);
    }

    [Fact]
    public async Task Publish_PutsTheChildsPipelineAndIdAmbient_AndRestoresTheEnclosingOne()
    {
        var outer = Mock.Of<IToolCallAdmissionPipeline>();
        await using var child = Open();

        using (ToolAdmissionAccessor.Begin(outer))
        {
            using (child.Publish())
            {
                ToolAdmissionAccessor.Current.Should().BeSameAs(child.Pipeline);
                ToolAdmissionAccessor.CurrentAgentId.Should().Be(Child);
            }

            ToolAdmissionAccessor.Current.Should().BeSameAs(outer);
        }
    }

    [Fact]
    public async Task Publish_AfterDispose_IsRefused()
    {
        // A released scope's pipeline governing new work would record decisions nobody folds.
        var child = Open(_parentTrace);
        await child.DisposeAsync();

        var act = () => child.Publish();

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Dispose_FoldsTheChildsTraceIntoTheParents_ThenDisposesTheScope()
    {
        _childTrace = () => Trace("child-denial");
        var child = Open(_parentTrace);
        var scopeDisposedWhenFolded = (bool?)null;
        _onGetTrace = () => scopeDisposedWhenFolded = ChildMarker.Disposed;

        await child.DisposeAsync();

        _parentTrace.Snapshot().ToolDecisions.Should().ContainSingle()
            .Which.Reason.Should().Be("child-denial");
        scopeDisposedWhenFolded.Should().BeFalse("the trace lives in the scope, so it is read before the scope goes");
        ChildMarker.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndFoldsOnce()
    {
        _childTrace = () => Trace("once");
        var child = Open(_parentTrace);

        await child.DisposeAsync();
        await child.DisposeAsync();

        _parentTrace.Snapshot().ToolDecisions.Should().ContainSingle();
    }

    [Fact]
    public async Task Dispose_WhenTheFoldThrows_StillDisposesTheScope()
    {
        _childTrace = () => throw new InvalidOperationException("trace unavailable");
        var child = Open(_parentTrace);

        var act = async () => await child.DisposeAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        ChildMarker.Disposed.Should().BeTrue("a failed fold must not leak the scope");
    }

    [Fact]
    public async Task WithNoParentTrace_NothingIsFolded_AndDisposeStillReleasesTheScope()
    {
        // A surface with no turn trace to report into (a direct invocation logs its own).
        var child = Open(parentTrace: null);

        await child.DisposeAsync();

        _pipelines.Single().Verify(p => p.GetTrace(), Times.Never);
        ChildMarker.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Open_WhenArmingThrows_DisposesTheScopeBeforeRethrowing()
    {
        // Nothing has handed the handle back yet, so nobody else will dispose the scope - and with it the
        // attribution Initialize just published.
        _pipelineResolutionFails = true;

        var act = () => Open();

        act.Should().Throw<InvalidOperationException>().WithMessage("pipeline not registered");
        ChildMarker.Disposed.Should().BeTrue("the scope opened for the child must not outlive a failed arming");
    }

    /// <summary>Resolved inside each child scope; reports whether that scope has been disposed.</summary>
    private sealed class ScopeMarker : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
