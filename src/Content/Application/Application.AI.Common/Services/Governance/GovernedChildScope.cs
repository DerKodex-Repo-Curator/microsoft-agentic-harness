using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Services.Governance;

/// <summary>
/// A child DI scope armed as its own governed agent, owning its whole lifecycle: opened and armed by
/// <see cref="GovernanceArmer.OpenChild"/>, its pipeline published with <see cref="Publish"/> around the
/// work, and on dispose its trace folded into the parent turn's before the scope goes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why one handle.</strong> The closing half of "arm a child scope" — fold the child's governance
/// trace into the parent's, then dispose the scope — used to be hand-written at every surface that opened
/// one. The child's recorder lives and dies with its scope, so a surface that forgot the fold silently
/// dropped the decisions the child made, and a tool the child was denied vanished from the turn's reported
/// <c>Governance</c> trace (#771). It was found twice before this handle (#769, #771). Folding is now a
/// property of the object, not of each caller's memory.
/// </para>
/// <para>
/// <strong>Order on dispose is load-bearing:</strong> the trace is read first (it lives in the scope), then
/// the scope is disposed, and the scope is disposed even if the fold throws. Disposal is idempotent and
/// folds once, because the pipeline's trace is cumulative.
/// </para>
/// <para>
/// A surface with no turn trace to report into (a direct tool invocation logs its own) opens the child
/// with no parent recorder; nothing is folded and the scope is still released.
/// </para>
/// </remarks>
public sealed class GovernedChildScope : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;
    private readonly IGovernanceTraceRecorder? _parentTrace;
    private int _disposed;

    internal GovernedChildScope(
        AsyncServiceScope scope,
        string agentId,
        IAgentExecutionContext context,
        IToolCallAdmissionPipeline pipeline,
        IGovernanceTraceRecorder? parentTrace)
    {
        _scope = scope;
        AgentId = agentId;
        Context = context;
        Pipeline = pipeline;
        _parentTrace = parentTrace;
    }

    /// <summary>The agent this child governs as.</summary>
    public string AgentId { get; }

    /// <summary>The child scope's execution context, initialised for <see cref="AgentId"/>.</summary>
    public IAgentExecutionContext Context { get; }

    /// <summary>The child scope's admission pipeline, reset and ready.</summary>
    public IToolCallAdmissionPipeline Pipeline { get; }

    /// <summary>
    /// Publishes this child's pipeline, with its agent id, for the work the caller is about to run. Dispose
    /// the result when that work ends. Streamed work publishes around each step rather than once, because
    /// ambient state set inside an async iterator does not survive a <c>yield</c>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// The handle was disposed: its trace has been folded and its scope released, so decisions recorded by
    /// new work under this pipeline would be lost.
    /// </exception>
    public IDisposable Publish()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return ToolAdmissionAccessor.Begin(Pipeline, AgentId);
    }

    /// <summary>
    /// Releases something opened for a child that failed to arm, without letting a disposal failure replace
    /// the arming failure the caller is about to rethrow. Disposed asynchronously and waited on, because a
    /// sync <c>Dispose</c> throws when any service in a scope is async-disposable only; the scope holds
    /// in-memory state, so the wait completes synchronously in practice.
    /// </summary>
    internal static void DisposeQuietly(IAsyncDisposable disposable)
    {
        try
        {
            disposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Deliberately swallowed: the original exception is the one that matters.
        }
    }

    /// <summary>
    /// Folds the child's trace into the parent's (once), then disposes the scope. A failed fold still
    /// releases the scope and is rethrown.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _parentTrace?.Absorb(Pipeline.GetTrace());
        }
        finally
        {
            await _scope.DisposeAsync().ConfigureAwait(false);
        }
    }
}
