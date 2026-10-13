using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Covers <see cref="FileSystemDiscoveryOverlapStartupValidator"/> (#824): the file-system tool must not be
/// able to write into a directory the skill or agent registry scans, because a file the model writes there
/// as <c>AGENT.md</c> or <c>SKILL.md</c> is registered as a live agent or skill.
/// </summary>
/// <remarks>
/// Like its sibling <see cref="FileSystemSandboxStartupValidatorTests"/>, the pairing that matters is that it
/// fires whenever the geometry is wrong and stays silent on the shipped default, because a guard that fires
/// on the default configuration gets turned off rather than heeded.
/// </remarks>
public sealed class FileSystemDiscoveryOverlapStartupValidatorTests : IDisposable
{
    private readonly string _root;

    public FileSystemDiscoveryOverlapStartupValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fs-discovery-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreateDirectory(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static FileSystemDiscoveryOverlapStartupValidator NewSut(
        IReadOnlyList<string> allowedBasePaths, Func<IReadOnlyList<string>> discoveryRoots) =>
        new(allowedBasePaths, discoveryRoots, NullLogger<FileSystemDiscoveryOverlapStartupValidator>.Instance);

    [Fact]
    public async Task StartAsync_AllowedBasePathEqualsADiscoveryRoot_Throws()
    {
        var agents = CreateDirectory("agents");

        var sut = NewSut([agents], () => [agents]);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        // The operator has to be able to act on this without reading the source.
        ex.Which.Message.Should().Contain(agents, "the message must name the offending path");
        ex.Which.Message.Should().Contain("AppConfig:Infrastructure:FileSystem:AllowedBasePaths",
            "the message must name the setting to change");
    }

    [Fact]
    public async Task StartAsync_DiscoveryRootInsideAnAllowedBasePath_Throws()
    {
        // The repository-root style grant: the tool may write anywhere under it, including skills/ and agents/.
        var skills = CreateDirectory("repo", "skills");

        var sut = NewSut([Path.Combine(_root, "repo")], () => [skills]);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartAsync_AllowedBasePathInsideADiscoveryRoot_Throws()
    {
        // The reverse nesting is just as exploitable: agents/team-a/ is itself scanned for AGENT.md.
        var agents = CreateDirectory("agents");
        var workspace = CreateDirectory("agents", "team-a", "workspace");

        var sut = NewSut([workspace], () => [agents]);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartAsync_ShippedDefaultLayout_DoesNotThrow()
    {
        // Mirrors the five hosts: AllowedBasePaths ["workspace"] (plus the logs directory) and the
        // skills/ and agents/ roots as siblings. If this ever throws, every host stops booting.
        var workspace = CreateDirectory("workspace");
        var logs = CreateDirectory("logs");
        var skills = CreateDirectory("skills");
        var agents = CreateDirectory("agents");

        var sut = NewSut([workspace, logs], () => [skills, agents]);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_SiblingWhoseNameSharesThePrefix_DoesNotThrow()
    {
        // The directory-boundary rule: "agents-archive" is a legitimate sibling of "agents". A plain
        // string-prefix check would fail the boot.
        var agents = CreateDirectory("agents");
        var lookalike = CreateDirectory("agents-archive");

        var sut = NewSut([lookalike], () => [agents]);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_NoDiscoveryRootsOrBlankEntries_DoesNotThrow()
    {
        var workspace = CreateDirectory("workspace");

        await NewSut([workspace], () => []).Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
        await NewSut([workspace], () => ["", "   "]).Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
        await NewSut(["", "   "], () => [workspace]).Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_ReadsTheDiscoveryRootsAtStartNotAtConstruction()
    {
        // Plugins append skill directories to the live configuration while the host starts, after the
        // container is built. A snapshot taken at construction would miss every one of them.
        var workspace = CreateDirectory("workspace");
        var pluginSkills = CreateDirectory("workspace", "plugin-skills");
        IReadOnlyList<string> roots = [];

        var sut = NewSut([workspace], () => roots);
        roots = [pluginSkills];

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
    }
}
