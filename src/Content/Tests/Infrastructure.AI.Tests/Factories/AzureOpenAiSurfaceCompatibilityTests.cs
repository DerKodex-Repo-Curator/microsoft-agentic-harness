using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Azure.AI.Projects;
using FluentAssertions;
using Infrastructure.AI.Factories;
using Infrastructure.AI.Tests.Helpers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Factories;

/// <summary>
/// Constructs the Azure AI Projects surface the harness still uses and sends one stubbed request through
/// it. Azure.AI.Projects moved a major version with the Agent Framework upgrade, so this surface can throw
/// <see cref="MissingMethodException"/> at runtime once a dependency moves on, a failure no compiler check
/// and no DI container validation can see. Sending a request is the only check that exercises the
/// constructors.
/// </summary>
/// <remarks>
/// The Azure OpenAI chat and embedding surfaces used to be covered here, against Azure.AI.OpenAI's client.
/// They now run on the OpenAI SDK's <c>/openai/v1/</c> route and are pinned, per surface, by
/// <see cref="AzureOpenAiV1ClientTests"/>. The package-skew guard (<c>PackageSkewTests</c>) checks every
/// cross-assembly call in each host, which is the check that would have found the original break without
/// anyone writing a test for that surface.
/// </remarks>
public sealed class AzureOpenAiSurfaceCompatibilityTests
{
    [Fact]
    public async Task FoundryProjectAgent_ConstructsAndCompletesARequest()
    {
        // The Project-scoped FoundryResponses path: AIProjectClient -> AsAIAgent -> a Responses turn.
        var handler = new ResponsesHandler();
        var options = new AIProjectClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler))
        };
        var project = new AIProjectClient(
            new Uri("https://myresource.services.ai.azure.com/api/projects/my-project"),
            new StaticTokenCredential("fake-token"),
            options);
        var provider = new FoundryAgentProvider(
            project, NullLoggerFactory.Instance, new ServiceCollection().BuildServiceProvider());

        var agent = await provider.CreateAgentAsync(
            "my-deployment", new ChatClientAgentOptions { Name = "probe" }, client => client);
        var response = await agent.RunAsync("hi");

        response.Text.Should().Be("hello back");
        handler.Paths.Should().Contain(p => p.Contains("responses", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Answers every request with a minimal valid Responses body and records the paths that reached the wire.</summary>
    private sealed class ResponsesHandler : HttpMessageHandler
    {
        private const string ResponsesBody = """
            {"id":"resp_1","object":"response","created_at":1700000000,"status":"completed","model":"my-deployment",
             "output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant",
                        "content":[{"type":"output_text","text":"hello back","annotations":[]}]}],
             "usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}}
            """;

        private readonly List<string> _paths = [];

        public IReadOnlyList<string> Paths => _paths;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _paths.Add(request.RequestUri!.AbsolutePath);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponsesBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
