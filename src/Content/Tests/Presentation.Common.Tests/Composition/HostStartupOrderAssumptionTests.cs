using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Pins the one host-startup assumption <c>FileSystemDiscoveryOverlapStartupValidator</c> depends on (#824):
/// hosted services start one after another, in registration order.
/// </summary>
/// <remarks>
/// The validator is registered after <c>PluginStartupLoader</c> because plugins append their skill directories
/// to the live configuration there, and it reads the content roots at <c>StartAsync</c>. Under
/// <c>HostOptions.ServicesStartConcurrently</c> (available since .NET 8, off by default) the two would start
/// together and the validator could check a root set that is missing every plugin directory, silently passing an
/// overlap. Rather than leave that as a comment, this fails the moment any production source opts in, so whoever
/// does it has to confront the ordering first (for example by making the validator wait on the plugin loader).
/// </remarks>
public sealed class HostStartupOrderAssumptionTests
{
    [Fact]
    public void NoProductionHostStartsItsHostedServicesConcurrently()
    {
        var contentRoot = RepoRoot.Combine("src", "Content");

        var sources = SourceScan.ReadProductionSources(contentRoot);
        sources.Should().NotBeEmpty("the production source this reads must exist for its verdict to mean anything");

        var offenders = sources
            .Where(s => s.Code.Contains("ServicesStartConcurrently", StringComparison.Ordinal))
            .Select(s => s.Path)
            .ToArray();

        offenders.Should().BeEmpty(
            "FileSystemDiscoveryOverlapStartupValidator must start after PluginStartupLoader to see plugin-added "
            + "skill directories; concurrent hosted-service start removes that guarantee. Offenders: "
            + string.Join(", ", offenders));
    }
}
