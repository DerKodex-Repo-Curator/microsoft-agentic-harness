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
        IReadOnlyList<string> allowedBasePaths, Func<IEnumerable<string>> discoveryRoots) =>
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

    [SkippableFact]
    public async Task StartAsync_DiscoveryRootReachedThroughASymlinkedParent_Throws()
    {
        // The same directory under two spellings: alias -> real. The root is configured through the alias,
        // the allowed path through the real location. Resolving only the final segment (the old behaviour of
        // the shared canonicalizer) compared two different strings and reported no overlap.
        var real = CreateDirectory("real");
        CreateDirectory("real", "repo", "skills");
        var alias = Path.Combine(_root, "alias");
        SandboxLinkFactory.CreateDirectoryLink(alias, real);

        var sut = NewSut([Path.Combine(real, "repo")], () => [Path.Combine(alias, "repo", "skills")]);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
    }

    [SkippableFact]
    public async Task StartAsync_AllowedPathReachedThroughASymlinkedParent_Throws()
    {
        // The mirror image: the allowed path goes through the alias, the root through the real location.
        var real = CreateDirectory("real");
        var skills = CreateDirectory("real", "repo", "skills");
        var alias = Path.Combine(_root, "alias");
        SandboxLinkFactory.CreateDirectoryLink(alias, real);

        var sut = NewSut([Path.Combine(alias, "repo")], () => [skills]);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
    }

    [SkippableFact]
    public async Task StartAsync_ALinkChainLongerThanTheHopBudget_RefusesInsteadOfGuessing()
    {
        // A path that cannot be fully resolved cannot be shown not to overlap, so it is a refusal. The chain is
        // built tail-first so each link points at one that already exists.
        var current = CreateDirectory("chain-real");
        for (var i = 0; i <= Application.Common.Helpers.LinkResolver.MaxLinkHops; i++)
        {
            var link = Path.Combine(_root, $"hop-{i}");
            SandboxLinkFactory.CreateDirectoryLink(link, current);
            current = link;
        }

        var skills = CreateDirectory("skills");

        var sut = NewSut([current], () => [skills]);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("could not be fully resolved");
    }

    [SkippableFact]
    public async Task StartAsync_ExtendedLengthSpellingOfTheSameDirectory_Throws()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Extended-length (\\\\?\\) paths exist only on Windows.");
        var agents = CreateDirectory("agents");

        var sut = NewSut([@"\\?\" + agents], () => [agents]);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartAsync_AMalformedEntry_IsSkippedNotFatal_AndARealOverlapIsStillFound()
    {
        // The registries log and skip a path they cannot resolve; this must not turn that into a crash. Both
        // halves matter: the bad entry is tolerated, and a genuine overlap listed next to it is not masked.
        var agents = CreateDirectory("agents");

        await NewSut([_root], () => ["\0not-a-valid-path", Path.Combine(_root, "unrelated-elsewhere")])
            .Invoking(s => s.StartAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>(
                "the allowed path _root contains the second (valid) root");

        await NewSut([CreateDirectory("somewhere-else")], () => ["\0not-a-valid-path", agents])
            .Invoking(s => s.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_AWholeVolumeGrant_Throws()
    {
        // The broadest overlap there is: the file-system tool may write anywhere on the volume. A volume
        // root trims to a string ("C:" or "") that Path.GetFullPath cannot read back, so re-normalizing it
        // compared the roots against the wrong directory (or threw) and let exactly this grant through.
        var volume = Path.GetPathRoot(_root)!;
        var agents = CreateDirectory("agents");

        var sut = NewSut([volume], () => [agents]);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("overlaps", "the refusal must be the overlap message, not a stray parse error");
    }

    [Fact]
    public async Task StartAsync_AnUnparseableAllowedBasePath_Refuses_RatherThanSkipping()
    {
        // Tolerating a bad content root is right (the registries skip it). Tolerating a bad ALLOWED path is a
        // hole: the file-system tool may still reach it, so leaving it out of the check leaves a writable
        // location unexamined.
        var agents = CreateDirectory("agents");

        var sut = NewSut(["\0not-a-valid-path"], () => [agents]);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
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
