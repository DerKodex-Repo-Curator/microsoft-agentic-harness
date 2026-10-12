using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Architecture guard: no production code calls <c>GetResponsesClient</c>. The Foundry Responses client is
/// built by <c>AgentFrameworkHelper.CreateFoundryDirectResponsesClient</c> on the OpenAI SDK directly,
/// because <c>AzureOpenAIClient.GetResponsesClient()</c> in Azure.AI.OpenAI 2.9.0-beta.1 constructs a
/// <c>ResponsesClient</c> through a constructor the OpenAI package no longer has: it compiles, and throws
/// <see cref="MissingMethodException"/> the first time it runs.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the matching <c>KnownHostSkews</c> entry safe to carry. The package-skew guard cannot
/// tell a call to a method that exists but is itself broken inside its package from a healthy one, so the
/// only thing keeping that break off a production path is that nothing calls it. Remove this test and its
/// entry together when the client type moves off Azure.AI.OpenAI (#799).
/// </para>
/// <para>
/// It matches the method name on any receiver, because a text scan cannot see the receiver's type. A
/// legitimate call on the plain OpenAI client belongs in <see cref="Exemptions"/>, with the reason.
/// </para>
/// </remarks>
public sealed class NoProductionCodeCallsGetResponsesClientTests
{
    private static readonly Regex Call = new(@"\.\s*GetResponsesClient\b", RegexOptions.Compiled);

    /// <summary>Relative paths of files allowed to call it, each with the reason it is not a violation.</summary>
    private static readonly (string RelativePath, string Reason)[] Exemptions = [];

    [Theory]
    [InlineData("var client = azure.GetResponsesClient();")]
    [InlineData("var client = azure. GetResponsesClient (deployment);")]
    [InlineData("Func<ResponsesClient> f = azure.GetResponsesClient;")]
    public void TheCallPattern_CatchesEveryShapeOfCallingIt(string code)
    {
        Call.IsMatch(code).Should().BeTrue();
    }

    [Fact]
    public void TheCallPattern_IgnoresTheHelpersThatAvoidIt()
    {
        Call.IsMatch("AgentFrameworkHelper.CreateFoundryDirectResponsesClient(options);").Should().BeFalse();
    }

    [Fact]
    public void NoProductionCode_CallsGetResponsesClient()
    {
        var sources = SourceScan.ReadProductionSources(Path.Combine(RepoRoot.Path, "src", "Content"));
        sources.Should().HaveCountGreaterThan(2000, "a much smaller scan means the root lookup or the filter broke");

        var violations = sources
            .Where(s => Call.IsMatch(s.Code))
            .Select(s => s.Path.Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !Exemptions.Any(e => path.EndsWith("/" + e.RelativePath, StringComparison.Ordinal)))
            .ToList();

        violations.Should().BeEmpty(
            "AzureOpenAIClient.GetResponsesClient() breaks at run time against the loaded OpenAI package; "
            + "use AgentFrameworkHelper.CreateFoundryDirectResponsesClient");
    }
}
