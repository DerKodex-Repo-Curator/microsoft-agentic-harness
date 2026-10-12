using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.MafHostingCanary.Tests;

/// <summary>
/// Canary for the question <c>A2AVersionPinTests</c> could not ask: has Microsoft's A2A or AG-UI support for
/// Agent Framework changed enough that the harness should wrap it instead of carrying its own transport?
/// </summary>
/// <remarks>
/// <para>
/// Microsoft publishes both as separate <em>preview</em> packages next to the core ones: an A2A client
/// (<c>Microsoft.Agents.AI.A2A</c>), A2A server registration and endpoint mapping
/// (<c>Microsoft.Agents.AI.Hosting.A2A</c>, <c>.A2A.AspNetCore</c>) and AG-UI endpoint mapping
/// (<c>Microsoft.Agents.AI.Hosting.AGUI.AspNetCore</c>). The core canary only inspected core assemblies, so
/// it was structurally unable to notice any of them. This project references the four (it exists only to
/// hold them: they bring the ASP.NET Core framework with them, which would change an unrelated project's
/// runtime) and pins what the evaluation in <c>documentation/architecture/a2a-message-contract.md</c> and
/// <c>ag-ui-hosting-evaluation.md</c> was made against: that each is still a preview, and exactly which
/// public types it exposes.
/// </para>
/// <para>
/// A failure is not a defect. It means a bump changed the answer: re-read the package, repeat the
/// comparison in the evaluation, record the outcome there, then update the snapshot. That is the whole job
/// of this test. It reads what the <em>pinned</em> version contains, so it cannot see a release upstream
/// until the pin next moves, and it cannot see a Microsoft package it does not reference.
/// </para>
/// </remarks>
public sealed class MafHostingPackagesCanaryTests
{
    // The surface the 2026-10 evaluation was made against (all 1.24.0-preview.261006.1).
    private static readonly Dictionary<string, string[]> Surfaces = new()
    {
        ["Microsoft.Agents.AI.A2A"] =
        [
            "A2A.A2AAgentCardExtensions",
            "A2A.A2ACardResolverExtensions",
            "A2A.A2AClientExtensions",
            "Microsoft.Agents.AI.A2A.A2AAgent",
            "Microsoft.Agents.AI.A2A.A2AAgentOptions",
            "Microsoft.Agents.AI.A2A.A2AAgentSession",
            "Microsoft.Agents.AI.A2AJsonUtilities",
        ],
        ["Microsoft.Agents.AI.Hosting.A2A"] =
        [
            "Microsoft.Agents.AI.Hosting.A2A.A2AHostingJsonUtilities",
            "Microsoft.Agents.AI.Hosting.A2A.A2ARunDecisionContext",
            "Microsoft.Agents.AI.Hosting.A2A.A2AServerRegistrationOptions",
            "Microsoft.Agents.AI.Hosting.A2A.AgentRunMode",
            "Microsoft.Agents.AI.Hosting.A2A.IsolationKeyScopedTaskStore",
            "Microsoft.Extensions.DependencyInjection.A2AServerServiceCollectionExtensions",
        ],
        ["Microsoft.Agents.AI.Hosting.A2A.AspNetCore"] =
        [
            "Microsoft.AspNetCore.Builder.A2AEndpointRouteBuilderExtensions",
        ],
        ["Microsoft.Agents.AI.Hosting.AGUI.AspNetCore"] =
        [
            "Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.AGUIEndpointRouteBuilderExtensions",
            "Microsoft.Extensions.DependencyInjection.AGUIServerServiceCollectionExtensions",
        ],
    };




    public static TheoryData<string, string> Packages => new()
    {
        { "Microsoft.Agents.AI.A2A", "a2a-message-contract.md" },
        { "Microsoft.Agents.AI.Hosting.A2A", "a2a-message-contract.md" },
        { "Microsoft.Agents.AI.Hosting.A2A.AspNetCore", "a2a-message-contract.md" },
        { "Microsoft.Agents.AI.Hosting.AGUI.AspNetCore", "ag-ui-hosting-evaluation.md" },
    };

    [Theory]
    [MemberData(nameof(Packages))]
    public void Package_IsStillAPreview_WithTheSurfaceTheEvaluationRead(string assemblyName, string evaluation)
    {
        var assembly = Assembly.Load(assemblyName);

        (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
            .Should().Contain("-preview",
                $"the evaluation judged a preview; a stable release is a new question, see {evaluation}");
        AssemblySurface.PublicTypes(assembly).Should().Equal(Surfaces[assemblyName],
            $"the public surface the evaluation read has changed; re-read it, record the outcome in {evaluation}, "
            + "then update the snapshot");
    }

    [Fact]
    public void NoProductionProject_ReferencesAnA2AOrAguiPackage()
    {
        // These packages are here to be read, not used. Referencing one from production is a decision the
        // evaluation did not take, and should be taken on purpose with the docs updated. Two layers: the
        // conditional PackageVersion in Directory.Packages.props makes a direct reference fail restore
        // (NU1010), and this reads the project files for the same IDs, case-insensitively, across project and
        // props/targets files. Neither sees a package pulled in transitively through another package.
        var content = Path.Combine(RepoRoot.Path, "src", "Content");
        var files = Directory.EnumerateFiles(content, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            .Where(p => !SourceScan.IsExcluded(p, content)
                        && !p.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar))
            .ToList();

        files.Count.Should().BeGreaterThan(10, "the scan must actually be looking at the production projects");

        var offenders = files
            .Where(p => MentionsTheFamilies(File.ReadAllText(p)))
            .Where(p => XDocument.Load(p).Descendants()
                .Where(e => e.Name.LocalName is "PackageReference" or "GlobalPackageReference")
                .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"))
                .Any(IsA2AOrAguiPackage))
            .Select(p => Path.GetRelativePath(content, p))
            .ToList();

        offenders.Should().BeEmpty(
            "adopting a preview A2A or AG-UI package is a decision to take with the evaluation in front of you");
    }

    [Theory]
    [InlineData("Microsoft.Agents.AI.Hosting.A2A", true)]
    [InlineData("microsoft.agents.ai.hosting.a2a.aspnetcore", true)]
    [InlineData("Microsoft.Agents.AI.A2A", true)]
    [InlineData("Microsoft.Agents.AI.AGUI", true)]
    [InlineData("Microsoft.Agents.AI.Hosting.AGUI.AspNetCore", true)]
    [InlineData("A2A", true)]
    [InlineData("A2A.AspNetCore", true)]
    [InlineData("AGUI.Server", true)]
    [InlineData("Microsoft.Agents.AI", false)]
    [InlineData("Microsoft.Agents.AI.Workflows", false)]
    [InlineData("Microsoft.Agents.AI.Hosting.OpenAI", false)]
    [InlineData("A2AExtras", false)]
    public void TheReferencePattern_MatchesTheFamilies_AndOnlyThose(string id, bool expected)
    {
        IsA2AOrAguiPackage(id).Should().Be(expected);
    }

    // Package ID prefixes of the families, matched case-insensitively (NuGet IDs are). The bare A2A and AGUI
    // names match exactly or as a dotted prefix, so "A2AExtras" is not caught.
    private static readonly string[] FamilyPrefixes =
    [
        "Microsoft.Agents.AI.A2A", "Microsoft.Agents.AI.AGUI",
        "Microsoft.Agents.AI.Hosting.A2A", "Microsoft.Agents.AI.Hosting.AGUI",
    ];

    private static readonly string[] FamilyRoots = ["A2A", "AGUI"];

    private static bool IsA2AOrAguiPackage(string? id) =>
        id is not null
        && (FamilyPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || FamilyRoots.Any(root => id.Equals(root, StringComparison.OrdinalIgnoreCase)
                                       || id.StartsWith(root + ".", StringComparison.OrdinalIgnoreCase)));

    // A cheap text test before parsing XML: nearly no project file mentions either family.
    private static bool MentionsTheFamilies(string text) =>
        text.Contains("A2A", StringComparison.OrdinalIgnoreCase)
        || text.Contains("AGUI", StringComparison.OrdinalIgnoreCase);
}
