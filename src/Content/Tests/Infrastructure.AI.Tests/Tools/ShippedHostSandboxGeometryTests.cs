using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Skills;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Runs <see cref="FileSystemDiscoveryOverlapStartupValidator"/> against each host's COMMITTED
/// <c>appsettings.json</c> (#824), so the claim "the shipped layout passes" is checked against the real files
/// rather than a hand-built imitation.
/// </summary>
/// <remarks>
/// A validator that refuses to boot the default configuration gets switched off rather than heeded, and a
/// default that drifts into an overlap (a new <c>AdditionalPaths</c> entry, a widened allowed path) would
/// otherwise be found only when someone starts that host. FoundryHost relocates its log directory at runtime;
/// that override is not in the committed file and is not modelled here.
/// </remarks>
public sealed class ShippedHostSandboxGeometryTests
{
    public static TheoryData<string> Hosts => new()
    {
        "Presentation.AgentHub",
        "Presentation.ConsoleUI",
        "Presentation.EvalRunner",
        "Presentation.ExecutionApi",
        "Presentation.FoundryHost",
    };

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task CommittedAppSettings_DoNotLetTheFileToolReachSkillOrAgentContent(string host)
    {
        var path = RepoRoot.Combine("src", "Content", "Presentation", host, "appsettings.json");
        File.Exists(path).Should().BeTrue($"the committed settings for {host} must exist for this check to mean anything");

        var config = new ConfigurationBuilder().AddJsonFile(path).Build()
            .GetSection("AppConfig").Get<AppConfig>();
        config.Should().NotBeNull();

        // Mirrors AddInfrastructureAIDependencies: the sandbox paths plus the logs directory.
        var allowed = config!.Infrastructure.FileSystem.AllowedBasePaths
            .Append(config.Logging.LogsBasePath ?? string.Empty)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(SkillContentRoots.Resolve)
            .ToList();

        allowed.Should().NotBeEmpty("a host with no allowed paths would make this vacuous");
        SkillContentRoots.ConfiguredDiscoveryPaths(config).Should().NotBeEmpty(
            "a host with no skill or agent roots would make this vacuous");

        var sut = new FileSystemDiscoveryOverlapStartupValidator(
            allowed,
            () => SkillContentRoots.ConfiguredAll(config),
            NullLogger<FileSystemDiscoveryOverlapStartupValidator>.Instance);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
    }
}
