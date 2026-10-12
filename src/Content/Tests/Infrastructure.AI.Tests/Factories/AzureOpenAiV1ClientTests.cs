using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using FluentAssertions;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit;

namespace Infrastructure.AI.Tests.Factories;

/// <summary>
/// Drives the real OpenAI SDK client the AzureOpenAI client type is built on, against a captured
/// transport, to pin what reaches the wire for each surface the harness uses: the <c>/openai/v1/</c>
/// route, the API key, the deployment as the model, no dated <c>api-version</c>, and retry behaviour.
/// </summary>
/// <remarks>
/// <para>
/// The previous implementation, <c>AzureOpenAIClient</c> from Azure.AI.OpenAI, is compiled against an
/// older OpenAI library than the one Agent Framework needs (its newest published version is a beta). It
/// built fine and then threw <see cref="MissingMethodException"/> on a surface that moved, which no
/// compile-time check can see; the Responses surface already did. The OpenAI SDK against the resource's
/// <c>/openai/v1/</c> route is the direction Microsoft documents for the GA v1 API, and removes the
/// dependency instead of waiting for it to catch up. Each surface is constructed and sent through here
/// because that is the only check that exercises the constructors.
/// </para>
/// </remarks>
public sealed class AzureOpenAiV1ClientTests
{
    private static readonly Uri Resource = new("https://myresource.openai.azure.com");

    [Fact]
    public async Task Chat_GoesToTheV1Route_WithTheKeyAndDeploymentAsModel_AndNoApiVersion()
    {
        var handler = new CapturingHandler();
        var chat = Chat(handler);

        var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.Should().Be("https://myresource.openai.azure.com/openai/v1/chat/completions");
        request.Uri.Should().NotContain("api-version", "the v1 surface has no dated api-version");
        request.Authorization.Should().Be("Bearer fake-key");
        request.Body.Should().Contain("\"model\":\"my-deployment\"");
        response.Text.Should().Be("hello back");
    }

    [Fact]
    public async Task Chat_Streaming_ConstructsAndCompletes()
    {
        // The agent loop streams; the streaming path uses different SDK members than a one-shot call.
        var chat = Chat(new CapturingHandler());

        var text = new StringBuilder();
        await foreach (var update in chat.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            text.Append(update.Text);

        text.ToString().Should().Be("hello back");
    }

    [Fact]
    public async Task Embeddings_GoToTheV1Route_WithTheDeploymentAsModel()
    {
        var handler = new CapturingHandler();
        var embeddings = Client(handler).GetEmbeddingClient("my-embedding").AsIEmbeddingGenerator();

        var result = await embeddings.GenerateAsync(["hello"]);

        result.Should().ContainSingle().Which.Vector.Length.Should().Be(2);
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Uri.Should().Be("https://myresource.openai.azure.com/openai/v1/embeddings");
        request.Authorization.Should().Be("Bearer fake-key");
        request.Body.Should().Contain("\"model\":\"my-embedding\"");
    }

    [Theory]
    [InlineData("https://myresource.openai.azure.com")]
    [InlineData("https://myresource.openai.azure.com/")]
    [InlineData("https://myresource.openai.azure.com/openai/v1")]
    [InlineData("https://myresource.openai.azure.com/openai/v1/")]
    public async Task Endpoint_InAnyOfTheCommonForms_ResolvesToTheSingleV1Route(string endpoint)
    {
        // Existing configuration holds the bare resource endpoint; a consumer who already pasted the v1
        // base URL from the docs must not end up with /openai/v1/openai/v1/.
        var handler = new CapturingHandler();

        await Chat(handler, endpoint: new Uri(endpoint)).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        handler.Requests.Should().ContainSingle()
            .Which.Uri.Should().Be("https://myresource.openai.azure.com/openai/v1/chat/completions");
    }

    [Theory]
    [InlineData("https://myresource.openai.azure.com/openai", "https://myresource.openai.azure.com/openai/v1/")]
    [InlineData("https://gw.contoso.com/aoai", "https://gw.contoso.com/aoai/openai/v1/")]
    [InlineData("https://gw.contoso.com/aoai/openai/v1", "https://gw.contoso.com/aoai/openai/v1/")]
    [InlineData("http://localhost:5000", "http://localhost:5000/openai/v1/")]
    [InlineData("http://127.0.0.1:5000/", "http://127.0.0.1:5000/openai/v1/")]
    public async Task Endpoint_BaseUrlForms_ResolveToTheV1Route(string endpoint, string expectedBase)
    {
        // A resource-style base ending in /openai, a gateway path prefix, and loopback http for local stubs.
        var handler = new CapturingHandler();

        await Chat(handler, endpoint: new Uri(endpoint)).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        handler.Requests.Should().ContainSingle()
            .Which.Uri.Should().Be(expectedBase + "chat/completions");
    }

    [Theory]
    [InlineData("http://myresource.openai.azure.com", "https")]
    [InlineData("https://user:pw@myresource.openai.azure.com", "credentials")]
    [InlineData("https://myresource.openai.azure.com/?api-version=2024-10-21", "query")]
    [InlineData("https://myresource.openai.azure.com/#frag", "fragment")]
    [InlineData("https://myresource.openai.azure.com/openai/deployments/gpt4o", "deployments")]
    public void Endpoint_ThatCouldMisdirectTheKeyOrBuildAWrongRoute_IsRefusedAtConstruction(string endpoint, string reason)
    {
        // The key is a bearer credential, so it must only travel over https (loopback aside), and a legacy
        // per-deployment URL or a pasted query string would silently produce a route that 404s.
        var act = () => AgentFrameworkHelper.CreateAzureOpenAIV1Client(new Uri(endpoint), "fake-key");

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(reason);
    }

    [Fact]
    public async Task RateLimit_DefaultClient_RetriesInternally()
    {
        var handler = new CapturingHandler(HttpStatusCode.TooManyRequests);

        await Attempt(Chat(handler));

        handler.Requests.Count.Should().BeGreaterThan(
            1, "the bare client is the right place for SDK retry; nothing else wraps it");
    }

    [Fact]
    public async Task RateLimit_RetrySuppressedClient_MakesExactlyOneRequest()
    {
        var handler = new CapturingHandler(HttpStatusCode.TooManyRequests);

        await Attempt(Chat(handler, disableProviderRetry: true));

        handler.Requests.Count.Should().Be(
            1, "inside the fallback chain the Polly pipeline is the only layer that may retry");
    }

    [Fact]
    public void BlankKey_IsRefusedAtConstruction_NotSentAsAnEmptyCredential()
    {
        var act = () => AgentFrameworkHelper.CreateAzureOpenAIV1Client(Resource, " ");

        act.Should().Throw<ArgumentException>();
    }

    private static OpenAIClient Client(
        CapturingHandler handler, bool disableProviderRetry = false, Uri? endpoint = null) =>
        AgentFrameworkHelper.CreateAzureOpenAIV1Client(
            endpoint ?? Resource,
            "fake-key",
            disableProviderRetry,
            new HttpClientPipelineTransport(new HttpClient(handler)));

    private static IChatClient Chat(
        CapturingHandler handler, bool disableProviderRetry = false, Uri? endpoint = null) =>
        Client(handler, disableProviderRetry, endpoint).GetChatClient("my-deployment").AsIChatClient();

    /// <summary>Issues one call and swallows the expected provider failure; the measurement is the request count.</summary>
    private static async Task Attempt(IChatClient chat)
    {
        try
        {
            await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        }
        catch
        {
            // The stub always fails.
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string? Authorization, string Body);

    /// <summary>
    /// Answers chat completions (one-shot or streamed) and embeddings with a minimal valid body, by path,
    /// or fails every request with a fixed status, and records what reached the wire.
    /// </summary>
    private sealed class CapturingHandler(HttpStatusCode? failWith = null) : HttpMessageHandler
    {
        private const string ChatBody = """
            {"id":"c1","object":"chat.completion","created":1700000000,"model":"my-deployment",
             "choices":[{"index":0,"message":{"role":"assistant","content":"hello back"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
            """;

        private const string ChatStream =
            """data: {"id":"c1","object":"chat.completion.chunk","created":1700000000,"model":"my-deployment","choices":[{"index":0,"delta":{"role":"assistant","content":"hello back"},"finish_reason":null}]}"""
            + "\n\n"
            + """data: {"id":"c1","object":"chat.completion.chunk","created":1700000000,"model":"my-deployment","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""
            + "\n\ndata: [DONE]\n\n";

        private const string EmbeddingBody = """
            {"object":"list","model":"my-embedding",
             "data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],
             "usage":{"prompt_tokens":1,"total_tokens":1}}
            """;

        private const string ErrorBody = """{"error":{"code":"stub","message":"stub failure"}}""";

        private readonly List<CapturedRequest> _requests = [];

        public IReadOnlyList<CapturedRequest> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            lock (_requests)
                _requests.Add(new CapturedRequest(
                    request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));

            if (failWith is { } status)
                return Json(status, ErrorBody);

            if (request.RequestUri!.AbsolutePath.Contains("embeddings", StringComparison.OrdinalIgnoreCase))
                return Json(HttpStatusCode.OK, EmbeddingBody);

            return body.Contains("\"stream\":true", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ChatStream, Encoding.UTF8, "text/event-stream")
                }
                : Json(HttpStatusCode.OK, ChatBody);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
