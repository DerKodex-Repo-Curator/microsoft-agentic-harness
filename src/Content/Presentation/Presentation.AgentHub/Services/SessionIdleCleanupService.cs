using System.Diagnostics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Observability.Models;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Interfaces;

namespace Presentation.AgentHub.Services;

/// <summary>
/// Periodically checks for sessions that have been idle longer than the configured timeout
/// and ends them. Prevents sessions from staying "active" indefinitely when the user stops
/// interacting but keeps the browser tab open.
/// </summary>
internal sealed class SessionIdleCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionTracker _connectionTracker;
    private readonly IOptionsMonitor<AgentHubConfig> _config;
    private readonly ILogger<SessionIdleCleanupService> _logger;

    public SessionIdleCleanupService(
        IServiceScopeFactory scopeFactory,
        IConnectionTracker connectionTracker,
        IOptionsMonitor<AgentHubConfig> config,
        ILogger<SessionIdleCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionTracker = connectionTracker;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                await RunTickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down — expected.
        }
    }

    /// <summary>
    /// Runs one cleanup pass, containing any failure so a single bad tick cannot stop the host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An exception escaping <see cref="ExecuteAsync"/> triggers the default
    /// <c>BackgroundServiceExceptionBehavior.StopHost</c>. The realistic trigger is a hot-reloaded
    /// <c>AppConfig:AgentHub</c> value that fails <see cref="AgentHubConfigValidator"/>: the options
    /// monitor then throws <see cref="OptionsValidationException"/> from <c>CurrentValue</c>. A failed pass
    /// is logged, and sessions it had not yet processed are picked up on the next tick. A session whose
    /// end-of-session write itself failed was already untracked, so it is not retried.
    /// </para>
    /// <para>
    /// Only cancellation requested through <paramref name="ct"/> is let through, because that is how
    /// shutdown reaches the loop. A timeout-driven <see cref="OperationCanceledException"/> from the store
    /// is an ordinary failure and is contained like any other.
    /// </para>
    /// </remarks>
    internal async Task RunTickAsync(CancellationToken ct)
    {
        try
        {
            await CleanupIdleSessionsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(
                ex, "Idle session cleanup failed; sessions not yet processed are retried on the next tick.");
        }
    }

    private async Task CleanupIdleSessionsAsync(CancellationToken ct)
    {
        var timeout = TimeSpan.FromMinutes(_config.CurrentValue.SessionIdleTimeoutMinutes);
        var now = DateTimeOffset.UtcNow;
        var expired = new List<(string ConnectionId, ActiveConversationInfo Info)>();

        foreach (var (connectionId, info) in _connectionTracker.GetAll())
        {
            if (now - info.LastActivityAt > timeout)
                expired.Add((connectionId, info));
        }

        if (expired.Count == 0) return;

        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IObservabilityStore>();

        foreach (var (connectionId, info) in expired)
        {
            if (_connectionTracker.Untrack(connectionId) is null)
                continue;

            OrchestrationMetrics.ConnectionsActive.Add(-1,
                new TagList { { AgentConventions.Name, info.AgentName } });

            if (info.TurnCount > 0)
            {
                var elapsed = now - info.StartedAt;
                var agentTag = new KeyValuePair<string, object?>(AgentConventions.Name, info.AgentName);
                OrchestrationMetrics.ConversationDuration.Record(elapsed.TotalMilliseconds, agentTag);
                OrchestrationMetrics.TurnsPerConversation.Record(info.TurnCount, agentTag);
            }

            await store.EndSessionAsync(
                info.ObservabilitySessionId, SessionStatus.Completed, cancellationToken: ct);

            _logger.LogInformation(
                "Idle timeout: ended session {SessionId} for agent {AgentName} after {IdleMinutes}m idle.",
                info.ObservabilitySessionId, info.AgentName, timeout.TotalMinutes);
        }
    }
}
