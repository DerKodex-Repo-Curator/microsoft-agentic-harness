using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Services;
using Xunit;

namespace Presentation.AgentHub.Tests.Services;

/// <summary>
/// Tests for the failure containment in <see cref="SessionIdleCleanupService"/> (#721 code-review).
/// </summary>
/// <remarks>
/// <see cref="AgentHubConfig"/> is validated by the options pipeline, so a hot-reloaded invalid value makes
/// <c>IOptionsMonitor&lt;AgentHubConfig&gt;.CurrentValue</c> throw <see cref="OptionsValidationException"/>.
/// Before the validator existed a bad live edit was silently accepted; with it, an exception escaping a
/// <c>BackgroundService</c> would stop the whole host (the default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c>). One failed tick must be logged and retried instead.
/// </remarks>
public sealed class SessionIdleCleanupServiceTests
{
    [Fact]
    public async Task RunTickAsync_ConfigReadThrowsValidationException_DoesNotEscape()
    {
        var monitor = new Mock<IOptionsMonitor<AgentHubConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Throws(
            new OptionsValidationException(
                Options.DefaultName, typeof(AgentHubConfig), ["SessionIdleTimeoutMinutes must be > 0"]));
        var sut = CreateSut(monitor.Object);

        var act = () => sut.RunTickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunTickAsync_ValidConfigNoSessions_Completes()
    {
        // Control: the same call with a readable config completes, so the test above proves the
        // exception was contained rather than that the method never reads the config.
        var monitor = new Mock<IOptionsMonitor<AgentHubConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(new AgentHubConfig());
        var sut = CreateSut(monitor.Object);

        var act = () => sut.RunTickAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        monitor.VerifyGet(m => m.CurrentValue, Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunTickAsync_Cancelled_StillPropagatesCancellation()
    {
        // Containment must not swallow shutdown: ExecuteAsync relies on OperationCanceledException.
        var monitor = new Mock<IOptionsMonitor<AgentHubConfig>>();
        monitor.SetupGet(m => m.CurrentValue).Throws(new OperationCanceledException());
        var sut = CreateSut(monitor.Object);

        var act = () => sut.RunTickAsync(new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static SessionIdleCleanupService CreateSut(IOptionsMonitor<AgentHubConfig> monitor)
    {
        var tracker = new Mock<IConnectionTracker>();
        tracker.Setup(t => t.GetAll()).Returns([]);

        return new SessionIdleCleanupService(
            Mock.Of<IServiceScopeFactory>(),
            tracker.Object,
            monitor,
            NullLogger<SessionIdleCleanupService>.Instance);
    }
}
