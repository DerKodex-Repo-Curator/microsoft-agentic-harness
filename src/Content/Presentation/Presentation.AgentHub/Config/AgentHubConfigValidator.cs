using FluentValidation;

namespace Presentation.AgentHub.Config;

/// <summary>
/// Validates <see cref="AgentHubConfig"/>, asserting that every bound the section declares is a usable one.
/// </summary>
/// <remarks>
/// <para>
/// Every rule is unconditional. The class defaults are all valid, so a host that omits the
/// <c>AppConfig:AgentHub</c> section binds defaults and boots unchanged; a rule only bites when an operator
/// supplies an explicit bad value.
/// </para>
/// <para>
/// Bound through <c>AddOptions&lt;AgentHubConfig&gt;().ValidateFluentValidation&lt;,&gt;().ValidateOnStart()</c>
/// in <see cref="DependencyInjection"/>, like the other config sections in this repo. This validator lives in
/// <c>Presentation.AgentHub</c> rather than <c>Application.Core/Validation</c> because
/// <see cref="AgentHubConfig"/> is a presentation-layer type that Application cannot reference.
/// </para>
/// <para>
/// <see cref="AgentHubConfig.SignalRMaxReceiveMessageSizeBytes"/> is additionally checked eagerly while the
/// host's services are composed (see <c>DependencyInjection.ResolveSignalRMaxMessageSizeBytes</c>), because
/// SignalR needs the value synchronously at registration time, before <c>ValidateOnStart()</c> fires. That
/// eager check runs this validator's rule for that one property, so the rule exists in one place.
/// </para>
/// </remarks>
public sealed class AgentHubConfigValidator : AbstractValidator<AgentHubConfig>
{
    /// <summary>Initializes the rule set.</summary>
    public AgentHubConfigValidator()
    {
        RuleFor(x => x.MaxHistoryMessages)
            .GreaterThan(0)
            .WithMessage("MaxHistoryMessages must be > 0 — a non-positive limit dispatches no conversation history to the agent.");

        RuleFor(x => x.SessionIdleTimeoutMinutes)
            .GreaterThan(0)
            .WithMessage("SessionIdleTimeoutMinutes must be > 0 — a non-positive timeout completes every session as soon as the cleanup service next runs.");

        RuleFor(x => x.ClientToolTimeoutSeconds)
            .GreaterThan(0)
            .WithMessage("ClientToolTimeoutSeconds must be > 0 — a non-positive timeout fails every client round-trip tool call.");

        RuleFor(x => x.SignalRMaxReceiveMessageSizeBytes)
            .GreaterThan(0)
            .When(x => x.SignalRMaxReceiveMessageSizeBytes.HasValue)
            .WithMessage("AppConfig:AgentHub:SignalRMaxReceiveMessageSizeBytes must be a positive number of bytes when set. "
                + "Leave it unset to keep SignalR's 32KB default — 0 is not 'unlimited' here, SignalR would apply it "
                + "literally and reject every hub invocation.");
    }
}
