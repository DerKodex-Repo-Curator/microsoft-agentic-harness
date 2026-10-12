using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Core;
using Infrastructure.AI.Caching;
using OpenAI;
using OpenAI.Responses;

namespace Infrastructure.AI.Helpers;

/// <summary>
/// Provides pre-configured client options for AI framework SDK clients.
/// Centralizes timeout, retry, telemetry, and user-agent settings.
/// </summary>
/// <remarks>
/// Lives in Infrastructure.AI because it depends on external SDK types
/// (<see cref="OpenAIClientOptions"/>).
/// Consumed by <see cref="Factories.ChatClientFactory"/> and DI registration.
/// </remarks>
public static class AgentFrameworkHelper
{
    private const string UserAgentValue = "AgenticHarness/1.0";
    private const int DefaultNetworkTimeoutSeconds = 300;

    /// <summary>
    /// DI key for the SDK client variants that perform no retries of their own, resolved by the
    /// provider fallback chain so the Polly pipeline is the only layer retrying.
    /// </summary>
    public const string NoProviderRetryClientKey = "no-provider-retry";

    /// <summary>
    /// DI key for the <see cref="ResponsesClient"/> targeting the bare AI Foundry resource
    /// endpoint (<see cref="Domain.Common.Config.AI.AIAgentFrameworkClientType.FoundryDirectResponses"/>).
    /// Keyed rather than resolved unkeyed because this is one of two variants (this one and
    /// <see cref="FoundryDirectResponsesNoRetryClientKey"/>) of the same client type, and a consumer
    /// may register other <see cref="ResponsesClient"/> instances with different endpoints or
    /// credentials that must never share a slot with it.
    /// </summary>
    public const string FoundryDirectResponsesClientKey = "foundry-direct-responses";

    /// <summary>
    /// DI key for the retry-disabled variant of <see cref="FoundryDirectResponsesClientKey"/>,
    /// resolved by the provider fallback chain — same reasoning as <see cref="NoProviderRetryClientKey"/>,
    /// kept separate because it must combine with the Foundry-direct endpoint/credential, not
    /// whichever provider <see cref="NoProviderRetryClientKey"/> would otherwise resolve.
    /// </summary>
    public const string FoundryDirectResponsesNoRetryClientKey = "foundry-direct-responses-no-retry";

    /// <summary>
    /// DI key for the <see cref="OpenAIClient"/> targeting an Azure OpenAI resource's <c>/openai/v1/</c>
    /// surface (<see cref="Domain.Common.Config.AI.AIAgentFrameworkClientType.AzureOpenAI"/>). Keyed rather
    /// than resolved by type because <see cref="OpenAIClient"/> is also what the plain
    /// <see cref="Domain.Common.Config.AI.AIAgentFrameworkClientType.OpenAI"/> type registers, and the
    /// Azure client must never answer for it (nor the other way round) when one is asked for by type.
    /// </summary>
    public const string AzureOpenAIClientKey = "azure-openai";

    /// <summary>
    /// DI key for the retry-disabled variant of <see cref="AzureOpenAIClientKey"/>, resolved by the
    /// provider fallback chain. Kept separate from <see cref="NoProviderRetryClientKey"/> for the same
    /// reason as <see cref="AzureOpenAIClientKey"/>: that slot is the plain OpenAI client's.
    /// </summary>
    public const string AzureOpenAINoRetryClientKey = "azure-openai-no-retry";

    /// <summary>
    /// Creates the OpenAI-protocol client for an Azure OpenAI resource, API-key authenticated, against the
    /// resource's GA <c>/openai/v1/</c> surface (<see cref="Domain.Common.Config.AI.AIAgentFrameworkClientType.AzureOpenAI"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built on the OpenAI SDK rather than on <c>AzureOpenAIClient</c> from Azure.AI.OpenAI. That package's
    /// newest published version (2.9.0-beta.1) is compiled against an older OpenAI library than the one
    /// Agent Framework 1.21+ requires, so any surface of it can throw <see cref="MissingMethodException"/>
    /// at runtime, a failure the compiler cannot see (the Responses surface already did). Microsoft's v1
    /// API removes the need for a separate Azure client: the deployment name is the request's <c>model</c>,
    /// there is no dated <c>api-version</c>, and the key is sent as a bearer credential.
    /// </para>
    /// <para>
    /// The same timeout and retry-suppression defaults apply as for every OpenAI-protocol client the
    /// harness builds (<see cref="ApplyPipelineDefaults"/>).
    /// </para>
    /// </remarks>
    /// <param name="resourceEndpoint">
    /// The Azure OpenAI resource endpoint, e.g. <c>https://my-resource.openai.azure.com</c>. A value that
    /// already ends in <c>/openai/v1</c> is used as given.
    /// </param>
    /// <param name="apiKey">The resource's API key.</param>
    /// <param name="disableProviderRetry">
    /// When true the SDK makes no retries of its own, leaving retry to the Polly pipeline.
    /// </param>
    public static OpenAIClient CreateAzureOpenAIV1Client(
        Uri resourceEndpoint,
        string apiKey,
        bool disableProviderRetry = false) =>
        CreateAzureOpenAIV1Client(resourceEndpoint, apiKey, disableProviderRetry, transport: null);

    /// <summary>
    /// As <see cref="CreateAzureOpenAIV1Client(Uri, string, bool)"/> with a replaceable HTTP transport.
    /// Internal so the seam cannot be used downstream to wrap a transport that sees the credential;
    /// tests reach it through InternalsVisibleTo.
    /// </summary>
    internal static OpenAIClient CreateAzureOpenAIV1Client(
        Uri resourceEndpoint,
        string apiKey,
        bool disableProviderRetry,
        PipelineTransport? transport)
    {
        ArgumentNullException.ThrowIfNull(resourceEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var options = GetOpenAIClientOptions(
            AzureOpenAIV1Endpoint(resourceEndpoint).AbsoluteUri, disableProviderRetry: disableProviderRetry);
        if (transport is not null) options.Transport = transport;

        return new OpenAIClient(new ApiKeyCredential(apiKey), options);
    }

    // The v1 base URL for a resource endpoint, with one trailing slash. Accepts the bare resource endpoint
    // (what configuration holds today), one ending in /openai, or one already ending in /openai/v1, so a
    // consumer who pasted the base URL from Microsoft's docs does not end up with /openai/v1/openai/v1/. A
    // path prefix in front (a gateway) is kept.
    //
    // The key travels as a bearer credential, so the endpoint is checked once, here, at startup rather than
    // discovered as a 404 or a leak later: https only (loopback http is allowed for local stubs), no
    // credentials, query or fragment in the URL, and not a legacy per-deployment URL, which would build a
    // route that cannot exist.
    private static Uri AzureOpenAIV1Endpoint(Uri resourceEndpoint)
    {
        const string setting = "AppConfig:AI:AgentFramework:Endpoint";

        if (resourceEndpoint.Scheme != Uri.UriSchemeHttps && !resourceEndpoint.IsLoopback)
            throw new InvalidOperationException(
                $"{setting} must use https because the API key is sent as a bearer credential (http is allowed only for loopback).");

        if (!string.IsNullOrEmpty(resourceEndpoint.UserInfo))
            throw new InvalidOperationException($"{setting} must not contain credentials in the URL.");

        if (!string.IsNullOrEmpty(resourceEndpoint.Query) || !string.IsNullOrEmpty(resourceEndpoint.Fragment))
            throw new InvalidOperationException(
                $"{setting} must be the resource endpoint without a query string or fragment; the v1 surface takes no api-version.");

        var path = resourceEndpoint.AbsolutePath.TrimEnd('/');
        if (path.Contains("/deployments/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{setting} must be the resource endpoint, not a per-deployment URL: the deployment name is the model, not part of the path (found '{path}').");

        var v1Path = path switch
        {
            _ when path.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase) => path,
            _ when path.EndsWith("/openai", StringComparison.OrdinalIgnoreCase) => path + "/v1",
            _ => path + "/openai/v1"
        };

        return new UriBuilder(resourceEndpoint) { Path = v1Path + "/" }.Uri;
    }

    /// <summary>
    /// The Entra scope the direct Responses client requests a token for: the Azure OpenAI /
    /// Cognitive Services audience, the one the resource has always been asked to accept.
    /// </summary>
    private const string AzureEntraScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// Creates the OpenAI-native <see cref="ResponsesClient"/> for the Azure AI Foundry Responses API
    /// called directly against the bare resource endpoint
    /// (<see cref="Domain.Common.Config.AI.AIAgentFrameworkClientType.FoundryDirectResponses"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built on the OpenAI SDK against the resource's <c>/openai/v1/</c> surface, the same route
    /// <see cref="CreateAzureOpenAIV1Client(Uri, string, bool)"/> uses. The earlier route through
    /// Azure.AI.OpenAI's <c>GetResponsesClient()</c> built fine and threw
    /// <see cref="MissingMethodException"/> at runtime once the OpenAI library moved on, a failure
    /// the compiler cannot see.
    /// </para>
    /// <para>
    /// Timeout and retry suppression come from the same <see cref="ApplyPipelineDefaults"/> every
    /// OpenAI-protocol client the harness builds uses.
    /// </para>
    /// </remarks>
    /// <param name="resourceEndpoint">The bare resource endpoint, e.g. <c>https://my-project.services.ai.azure.com</c>.</param>
    /// <param name="credential">The Entra credential shared with the Project-scoped Foundry path.</param>
    /// <param name="disableProviderRetry">
    /// When true the SDK makes no retries of its own, leaving retry to the Polly pipeline.
    /// </param>
    public static ResponsesClient CreateFoundryDirectResponsesClient(
        Uri resourceEndpoint,
        TokenCredential credential,
        bool disableProviderRetry = false) =>
        CreateFoundryDirectResponsesClient(resourceEndpoint, credential, disableProviderRetry, transport: null);

    /// <summary>
    /// As <see cref="CreateFoundryDirectResponsesClient(Uri, TokenCredential, bool)"/> with a
    /// replaceable HTTP transport. Internal so the seam cannot be used downstream to wrap a transport
    /// that sees the <c>Authorization</c> header; tests reach it through InternalsVisibleTo.
    /// </summary>
    internal static ResponsesClient CreateFoundryDirectResponsesClient(
        Uri resourceEndpoint,
        TokenCredential credential,
        bool disableProviderRetry,
        PipelineTransport? transport)
    {
        ArgumentNullException.ThrowIfNull(resourceEndpoint);
        ArgumentNullException.ThrowIfNull(credential);

        // ResponsesClient takes its own options type (not OpenAIClientOptions).
        var options = new ResponsesClientOptions
        {
            Endpoint = new Uri(resourceEndpoint.AbsoluteUri.TrimEnd('/') + "/openai/v1/"),
            UserAgentApplicationId = UserAgentValue
        };

        ApplyPipelineDefaults(options, DefaultNetworkTimeoutSeconds, disableProviderRetry);
        if (transport is not null) options.Transport = transport;

        return new ResponsesClient(new BearerTokenPolicy(credential, AzureEntraScope), options);
    }

    // The settings every SDK client option type shares through ClientPipelineOptions: one place for
    // the timeout and for "retry suppressed means maxRetries: 0".
    private static void ApplyPipelineDefaults(
        ClientPipelineOptions options, int networkTimeoutSeconds, bool disableProviderRetry)
    {
        options.NetworkTimeout = TimeSpan.FromSeconds(networkTimeoutSeconds);

        if (disableProviderRetry)
            options.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);
    }

    /// <summary>
    /// Gets configured options for <see cref="Azure.AI.Inference.ChatCompletionsClient"/>.
    /// </summary>
    /// <param name="disableProviderRetry">
    /// When true, turns off the SDK's own retry policy, leaving retry entirely to the caller.
    /// </param>
    /// <returns>Configured <see cref="Azure.AI.Inference.AzureAIInferenceClientOptions"/>.</returns>
    public static Azure.AI.Inference.AzureAIInferenceClientOptions GetAzureAIInferenceClientOptions(
        bool disableProviderRetry = false)
    {
        var options = new Azure.AI.Inference.AzureAIInferenceClientOptions();

        if (disableProviderRetry)
            options.Retry.MaxRetries = 0;

        return options;
    }

    /// <summary>
    /// Gets configured options for <see cref="OpenAIClient"/>.
    /// </summary>
    /// <param name="endpoint">
    /// Optional base endpoint for an OpenAI-compatible gateway (e.g. OpenRouter at
    /// <c>https://openrouter.ai/api/v1</c>). When null/blank/invalid, the SDK default
    /// (<c>https://api.openai.com/v1</c>) is used.
    /// </param>
    /// <param name="enablePromptCaching">
    /// When true, adds the <see cref="PromptCachingPipelinePolicy"/> so each chat-completions
    /// request stamps an Anthropic prompt-cache breakpoint on its system prefix. Intended for
    /// Claude-via-OpenRouter; harmless against providers that ignore <c>cache_control</c>.
    /// </param>
    /// <param name="networkTimeoutSeconds">Network timeout in seconds. Default: 300.</param>
    /// <param name="disableProviderRetry">
    /// When true, turns off the SDK's own retry policy, leaving retry entirely to the caller.
    /// </param>
    /// <returns>Configured <see cref="OpenAIClientOptions"/>.</returns>
    public static OpenAIClientOptions GetOpenAIClientOptions(
        string? endpoint = null,
        bool enablePromptCaching = false,
        int networkTimeoutSeconds = DefaultNetworkTimeoutSeconds,
        bool disableProviderRetry = false)
    {
        var options = new OpenAIClientOptions { UserAgentApplicationId = UserAgentValue };

        ApplyPipelineDefaults(options, networkTimeoutSeconds, disableProviderRetry);

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            // Fail loud on a malformed endpoint rather than silently falling back to the default
            // OpenAI endpoint — a dropped OpenRouter URL would otherwise send the OpenRouter key to
            // api.openai.com and 401 with no indication the endpoint was ignored. Leave blank for
            // the default OpenAI endpoint.
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
            {
                throw new InvalidOperationException(
                    $"OpenAI-compatible endpoint '{endpoint}' is not a valid absolute URI. " +
                    "Use e.g. https://openrouter.ai/api/v1, or leave it blank for the default OpenAI endpoint.");
            }

            options.Endpoint = endpointUri;
        }

        if (enablePromptCaching)
        {
            options.AddPolicy(new PromptCachingPipelinePolicy(), PipelinePosition.PerCall);
        }

        return options;
    }
}
