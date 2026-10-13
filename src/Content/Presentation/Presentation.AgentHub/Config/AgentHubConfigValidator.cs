using FluentValidation;

namespace Presentation.AgentHub.Config;

/// <summary>
/// Validates <see cref="AgentHubConfig"/>, asserting that each numeric bound the section declares is usable.
/// </summary>
/// <remarks>
/// <para>
/// Only the numeric bounds are checked. <see cref="AgentHubConfig.DefaultAgentName"/> is deliberately free
/// (blank means no default agent) and the <see cref="AgentHubConfig.Cors"/> origin list is consumed by the
/// CORS policy at composition. The documented relationship between
/// <see cref="AgentHubConfig.ClientToolTimeoutSeconds"/> and the agent turn timeout spans two configs and is
/// not enforced here.
/// </para>
/// <para>
/// Every rule is unconditional. The class defaults are all valid, so a host that omits the
/// <c>AppConfig:AgentHub</c> section binds defaults and boots unchanged; a rule only bites when an operator
/// supplies an explicit bad value.
/// </para>
/// <para>
/// Lives here rather than in <c>Application.Core/Validation</c> because <see cref="AgentHubConfig"/> is a
/// presentation-layer type that Application cannot reference. It is bound in <see cref="DependencyInjection"/>.
/// <see cref="AgentHubConfig.SignalRMaxReceiveMessageSizeBytes"/> is also checked early, while services are
/// composed, because SignalR needs the value before <c>ValidateOnStart()</c> fires; that check runs this
/// validator's rule for that one property so the rule exists once.
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
            .WithMessage("AppConfig:AgentHub:SignalRMaxReceiveMessageSizeBytes must be a positive number of bytes when set (got {PropertyValue}). "
                + "Leave it unset to keep SignalR's 32KB default — 0 is not 'unlimited' here, SignalR would apply it "
                + "literally and reject every hub invocation.");
    }
}
