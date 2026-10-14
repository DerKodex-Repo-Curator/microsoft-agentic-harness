using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Infrastructure.AI.Skills;
using Infrastructure.AI.Tests.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

/// <summary>
/// Pins that <see cref="AgentMetadataRegistry"/>'s directory walk and <c>AGENT.md</c> read run through the
/// sandboxed <see cref="SkillFileReader"/> the way skill discovery does (#822), using the REAL reader over a
/// real configuration rather than a stub.
/// </summary>
/// <remarks>
/// A stub reader would prove only that the registry calls the interface; whether a junction planted inside
/// an agent root is refused is a property of the reader's guard, so these tests build the genuine one. The
/// sandbox here includes the agent root itself (<c>SkillContentRoots.All</c> adds agent roots), which is why
/// the interesting cases are the ones that lead OUT of the root: a link, and a file too large to read.
/// </remarks>
public sealed class AgentMetadataRegistryWalkSandboxTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), $"agent-walk-{Guid.NewGuid():N}");

    public AgentMetadataRegistryWalkSandboxTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp folder; a lingering handle must not fail the test.
        }
    }

    [SkippableFact]
    public void GetAll_LinkInsideRootPointingOutside_DoesNotRegisterTheAgentItLeadsTo()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "agents")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_temp, "elsewhere", "evil")).FullName;
        WriteManifest(root, "good", "good");
        File.WriteAllText(Path.Combine(outside, "AGENT.md"), Manifest("evil"));
        SandboxLinkFactory.CreateDirectoryLink(Path.Combine(root, "planted"), outside);

        var registry = CreateRegistry(new AgentsConfig { BasePath = root });

        var ids = registry.GetAll().Select(a => a.Id).ToList();

        ids.Should().Contain("good", "control: an ordinary agent beside the link is still found");
        ids.Should().NotContain("evil", "a link planted in an agent root must not carry the walk outside the sandbox");
    }

    [Fact]
    public void GetAll_ManifestOverTheReadSizeLimit_IsSkippedRatherThanLoaded()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "agents")).FullName;
        WriteManifest(root, "good", "good");

        var hugeDir = Directory.CreateDirectory(Path.Combine(root, "huge")).FullName;
        using (var stream = File.Create(Path.Combine(hugeDir, "AGENT.md")))
        {
            var head = System.Text.Encoding.UTF8.GetBytes(Manifest("huge"));
            stream.Write(head);
            stream.SetLength(11L * 1024 * 1024);
        }

        var registry = CreateRegistry(new AgentsConfig { BasePath = root });

        var ids = registry.GetAll().Select(a => a.Id).ToList();

        ids.Should().Contain("good");
        ids.Should().NotContain("huge", "the sandboxed reader enforces a 10 MB cap; the raw read did not");
    }

    [Fact]
    public void GetAll_RootWithShellMetacharacters_DoesNotAbortDiscoveryOfTheOtherRoots()
    {
        var good = Directory.CreateDirectory(Path.Combine(_temp, "agents")).FullName;
        // ';' is a shell metacharacter the sandbox's input validation refuses (a lone '&' is not), yet a legal
        // folder name on every platform, so this is a real directory the reader will throw on.
        var odd = Directory.CreateDirectory(Path.Combine(_temp, "R;D", "agents")).FullName;
        WriteManifest(good, "alpha", "alpha");
        WriteManifest(odd, "beta", "beta");

        var registry = CreateRegistry(new AgentsConfig { BasePath = good, AdditionalPaths = [odd] });

        var act = () => registry.GetAll();

        act.Should().NotThrow("one root the sandbox's input validation rejects must not end discovery of the rest");
        registry.GetAll().Select(a => a.Id).Should().Contain("alpha");
    }

    [Fact]
    public void GetAll_OneAgentManifestRefused_DoesNotTakeDownTheOtherAgents()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "agents")).FullName;
        WriteManifest(root, "good", "good");
        WriteManifest(root, "bad", "bad");
        var registry = CreateRegistry(
            new AgentsConfig { BasePath = root }, real => new RefusableReader(real) { RefuseProbesUnder = "bad" });

        var act = () => registry.GetAll();

        act.Should().NotThrow("one refused path must not become a registry-wide outage");
        registry.GetAll().Select(a => a.Id).Should().BeEquivalentTo(["good"]);
    }

    [SkippableFact]
    public void GetAll_AgentWhoseOwnSkillsDirectoryLeavesTheRoots_IsNotRegistered()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "agents")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_temp, "elsewhere", "skills")).FullName;
        WriteManifest(root, "good", "good");
        WriteManifest(root, "alpha", "alpha");
        SandboxLinkFactory.CreateDirectoryLink(Path.Combine(root, "alpha", "skills"), outside);

        var registry = CreateRegistry(new AgentsConfig { BasePath = root });

        // An agent that lost its own skills would be rebuilt from a same-id GLOBAL skill, so it is absent,
        // not registered half-loaded; the unrelated agent is unaffected.
        registry.GetAll().Select(a => a.Id).Should().BeEquivalentTo(["good"]);
    }

    [Fact]
    public void Refresh_DirectoryRejectedByInputValidation_DoesNotStopDeletionsBeingReconciled()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "agents")).FullName;
        WriteManifest(root, "stays", "stays");
        WriteManifest(root, "goes", "goes");
        Directory.CreateDirectory(Path.Combine(root, "R;D"));
        var registry = CreateRegistry(new AgentsConfig { BasePath = root });
        registry.GetAll().Select(a => a.Id).Should().BeEquivalentTo(["stays", "goes"]);

        Directory.Delete(Path.Combine(root, "goes"), recursive: true);
        var summary = registry.Refresh();

        // The rejection is deterministic, so it says nothing about whether the scan was complete. Treating
        // it as an enumeration error would keep a really-deleted agent registered forever.
        summary.Removed.Should().BeEquivalentTo(["goes"]);
    }

    private AgentMetadataRegistry CreateRegistry(
        AgentsConfig agents, Func<ISkillFileReader, ISkillFileReader>? decorate = null)
    {
        var appConfig = new AppConfig { AI = new AIConfig { Agents = agents } };
        var monitor = new OptionsMonitorStub(appConfig);
        ISkillFileReader reader = new SkillFileReader(monitor, NullLogger<SkillFileReader>.Instance);
        if (decorate is not null)
            reader = decorate(reader);

        return new AgentMetadataRegistry(
            NullLogger<AgentMetadataRegistry>.Instance,
            monitor,
            new AgentMetadataParser(
                NullLogger<AgentMetadataParser>.Instance,
                reader,
                TestMcpSecurityScanner.AlwaysSafe(),
                TestMcpSecurityScanner.DefaultConfig()),
            new SkillMetadataParser(
                NullLogger<SkillMetadataParser>.Instance,
                reader,
                TestMcpSecurityScanner.AlwaysSafe(),
                TestMcpSecurityScanner.DefaultConfig(),
                TestMcpSecurityScanner.RealEgressValidator()),
            reader,
            new AgentOwnedSkillStore());
    }

    private static void WriteManifest(string root, string folder, string id)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, folder)).FullName;
        File.WriteAllText(Path.Combine(dir, "AGENT.md"), Manifest(id));
    }

    private static string Manifest(string id) => $"---\nid: {id}\nname: {id}\n---\nInstructions for {id}.\n";

    /// <summary>Delegates to the real reader, except that it reports a manifest probe under a chosen path as a sandbox refusal.</summary>
    private sealed class RefusableReader(ISkillFileReader inner) : ISkillFileReader
    {
        /// <summary>When set, a manifest probe for any path containing this text is reported as refused.</summary>
        public string? RefuseProbesUnder { get; set; }

        public string ReadText(string path) => inner.ReadText(path);

        public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
            inner.ReadTextAsync(path, cancellationToken);

        public bool FileExists(string path) => RefuseProbesUnder is not null && path.Contains(RefuseProbesUnder, StringComparison.Ordinal)
            ? throw new SkillPathRefusedException("Refused for the test.", new UnauthorizedAccessException())
            : inner.FileExists(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public IReadOnlyList<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);
    }

    private sealed class OptionsMonitorStub(AppConfig value) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue => value;

        public AppConfig Get(string? name) => value;

        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }
}
