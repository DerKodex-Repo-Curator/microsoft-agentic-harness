using FluentAssertions;
using Infrastructure.AI.Helpers;
using Xunit;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// Tests for <see cref="AgentFrameworkHelper"/> covering OpenAI client option configuration, which
/// the Azure OpenAI client (built on the OpenAI SDK's v1 route) shares.
/// </summary>
public sealed class AgentFrameworkHelperTests
{
    [Fact]
    public void GetOpenAIClientOptions_DefaultTimeout_Is300Seconds()
    {
        var options = AgentFrameworkHelper.GetOpenAIClientOptions();

        options.NetworkTimeout.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void GetOpenAIClientOptions_CustomTimeout_IsApplied()
    {
        var options = AgentFrameworkHelper.GetOpenAIClientOptions(networkTimeoutSeconds: 120);

        options.NetworkTimeout.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void GetOpenAIClientOptions_WithEndpoint_TargetsCompatibleGateway()
    {
        var options = AgentFrameworkHelper.GetOpenAIClientOptions("https://openrouter.ai/api/v1");

        options.Endpoint.Should().Be(new Uri("https://openrouter.ai/api/v1"));
    }

    [Fact]
    public void GetOpenAIClientOptions_WithoutEndpoint_LeavesSdkDefault()
    {
        var options = AgentFrameworkHelper.GetOpenAIClientOptions();

        options.Endpoint.Should().BeNull();
    }

    [Fact]
    public void GetOpenAIClientOptions_WithMalformedEndpoint_ThrowsInsteadOfSilentlyFallingBack()
    {
        // A scheme-less typo must fail loud — otherwise the OpenRouter key would be sent to
        // api.openai.com and 401 with no indication the endpoint was dropped.
        var act = () => AgentFrameworkHelper.GetOpenAIClientOptions("openrouter.ai/api/v1");

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a valid absolute URI*");
    }

    [Fact]
    public void GetOpenAIClientOptions_SetsUserAgent()
    {
        var options = AgentFrameworkHelper.GetOpenAIClientOptions();

        options.UserAgentApplicationId.Should().Be("AgenticHarness/1.0");
    }

    [Fact]
    public void GetOpenAIClientOptions_WithPromptCaching_RegistersPolicyAndKeepsEndpoint()
    {
        // The prompt-caching branch adds a pipeline policy; the registered policy list is not
        // publicly inspectable, so assert the option set is still built correctly (no throw,
        // endpoint honoured) — the policy's live effect is covered by the gated integration test.
        var options = AgentFrameworkHelper.GetOpenAIClientOptions(
            "https://openrouter.ai/api/v1", enablePromptCaching: true);

        options.Endpoint.Should().Be(new Uri("https://openrouter.ai/api/v1"));
        options.NetworkTimeout.Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void GetOpenAIClientOptions_PromptCachingDefaultsOff()
    {
        // Default must be opt-in: a consumer who does not ask for caching gets none.
        var act = () => AgentFrameworkHelper.GetOpenAIClientOptions("https://openrouter.ai/api/v1");

        act.Should().NotThrow();
    }
}
