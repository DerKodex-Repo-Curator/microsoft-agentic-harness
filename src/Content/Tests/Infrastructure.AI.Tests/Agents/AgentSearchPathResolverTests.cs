using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Skills;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

/// <summary>
/// Unit tests for <see cref="AgentSearchPathResolver"/>, split out of
/// <see cref="AgentMetadataRegistryTests"/> so the resolver's own contract — including the
/// malformed-path hardening added on code review for issue #705 — is verified independently of the
/// full registry.
/// </summary>
public sealed class AgentSearchPathResolverTests
{
    [Fact]
    public void Resolve_OneEntryIsMalformed_SkipsItAndStillResolvesTheValidOnes()
    {
        // Code-review finding on #705: Path.GetFullPath is not guaranteed to succeed for an arbitrary
        // configured string (an embedded NUL is rejected on every supported platform). Before this
        // fix, one bad AdditionalPaths entry would throw out of Resolve entirely, aborting discovery
        // of every OTHER configured path too — the same "one bad entry breaks everything" failure
        // shape the existing not-found branch already guards against for a merely-missing directory.
        var validPath = Path.Combine(Path.GetTempPath(), $"agents-resolver-valid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(validPath);
        try
        {
            var config = new AgentsConfig
            {
                BasePath = validPath,
                AdditionalPaths = ["\0not-a-valid-path"]
            };

            var resolved = AgentSearchPathResolver.Resolve(config, new UnsandboxedSkillFileReader(), NullLogger.Instance);

            resolved.Should().ContainSingle().Which.Should().Be(validPath);
        }
        finally
        {
            Directory.Delete(validPath, recursive: true);
        }
    }

    [Fact]
    public void Resolve_NoPathsConfigured_ReturnsEmpty()
    {
        var resolved = AgentSearchPathResolver.Resolve(
            new AgentsConfig(), new UnsandboxedSkillFileReader(), NullLogger.Instance);

        resolved.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_NullConfig_ReturnsEmpty()
    {
        var resolved = AgentSearchPathResolver.Resolve(null, new UnsandboxedSkillFileReader(), NullLogger.Instance);

        resolved.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ConfiguredPathDoesNotExist_SkipsItWithoutThrowing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"agents-resolver-missing-{Guid.NewGuid():N}");

        var resolved = AgentSearchPathResolver.Resolve(
            new AgentsConfig { BasePath = missingPath }, new UnsandboxedSkillFileReader(), NullLogger.Instance);

        resolved.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_ChecksExistenceThroughTheSandboxedReader_NotRawDirectoryExists()
    {
        // #710: agent discovery must consult the same sandboxed reader as skill discovery, so a later
        // hardening of the reader's path rules reaches agent roots instead of stopping at the skill side.
        var realPath = Path.Combine(Path.GetTempPath(), $"agents-resolver-sandboxed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(realPath);
        try
        {
            var act = () => AgentSearchPathResolver.Resolve(
                new AgentsConfig { BasePath = realPath }, new AlwaysRefusingReader(), NullLogger.Instance);

            act.Should().Throw<SkillPathRefusedException>(
                "the resolver must consult the sandboxed reader for existence, not bypass it via raw Directory.Exists");
        }
        finally
        {
            Directory.Delete(realPath, recursive: true);
        }
    }

    private sealed class AlwaysRefusingReader : ISkillFileReader
    {
        public string ReadText(string path) => throw new SkillPathRefusedException(path);
        public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
            throw new SkillPathRefusedException(path);
        public bool FileExists(string path) => throw new SkillPathRefusedException(path);
        public bool DirectoryExists(string path) => throw new SkillPathRefusedException(path);
        public IReadOnlyList<string> EnumerateDirectories(string path) => throw new SkillPathRefusedException(path);
    }
}
