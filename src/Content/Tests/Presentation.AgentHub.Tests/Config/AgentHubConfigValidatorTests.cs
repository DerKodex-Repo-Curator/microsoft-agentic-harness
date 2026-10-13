using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.Tests.Hubs;
using Xunit;

namespace Presentation.AgentHub.Tests.Config;

/// <summary>
/// Tests for <see cref="AgentHubConfigValidator"/> and its binding into the options pipeline (#721).
/// </summary>
/// <remarks>
/// Every rule is unconditional, so the defaults-are-valid test is what keeps the rest honest: if a shipped
/// default violated a rule, every host that omits the <c>AppConfig:AgentHub</c> section would fail to start.
/// The wiring tests exist because a validator nothing binds enforces nothing, however well its own tests pass.
/// </remarks>
public sealed class AgentHubConfigValidatorTests
{
    private readonly AgentHubConfigValidator _sut = new();

    [Fact]
    public void Validate_ShippedDefaults_IsValid()
    {
        _sut.Validate(new AgentHubConfig()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveMaxHistoryMessages_IsRefused(int value)
    {
        var result = _sut.Validate(new AgentHubConfig { MaxHistoryMessages = value });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.PropertyName.Should().Be(nameof(AgentHubConfig.MaxHistoryMessages));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveSessionIdleTimeout_IsRefused(int value)
    {
        var result = _sut.Validate(new AgentHubConfig { SessionIdleTimeoutMinutes = value });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.PropertyName.Should().Be(nameof(AgentHubConfig.SessionIdleTimeoutMinutes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveClientToolTimeout_IsRefused(int value)
    {
        var result = _sut.Validate(new AgentHubConfig { ClientToolTimeoutSeconds = value });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.PropertyName.Should().Be(nameof(AgentHubConfig.ClientToolTimeoutSeconds));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Validate_NonPositiveSignalRMaxMessageSize_IsRefused(long value)
    {
        var result = _sut.Validate(new AgentHubConfig { SignalRMaxReceiveMessageSizeBytes = value });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("positive");
    }

    [Fact]
    public void Validate_UnsetSignalRMaxMessageSize_IsValid()
    {
        // Unset keeps SignalR's own 32KB default; the rule must not read null as zero.
        _sut.Validate(new AgentHubConfig { SignalRMaxReceiveMessageSizeBytes = null }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void AddAgentHubServices_InvalidSection_FailsWhenOptionsResolve()
    {
        // Proves the validator is bound into the options pipeline: delete the
        // ValidateFluentValidation call in DependencyInjection and this test goes red.
        using var provider = AgentHubTestServiceProviderFactory.Build(
            new Dictionary<string, string?> { ["AppConfig:AgentHub:MaxHistoryMessages"] = "0" });

        var act = () => provider.GetRequiredService<IOptions<AgentHubConfig>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*MaxHistoryMessages*");
    }

    [Fact]
    public void AddAgentHubServices_OmittedSection_ResolvesValidDefaults()
    {
        using var provider = AgentHubTestServiceProviderFactory.Build();

        var config = provider.GetRequiredService<IOptions<AgentHubConfig>>().Value;

        config.MaxHistoryMessages.Should().Be(20);
    }
}
