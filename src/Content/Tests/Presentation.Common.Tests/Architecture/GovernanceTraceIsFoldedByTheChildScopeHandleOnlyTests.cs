using System.Text.RegularExpressions;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Architecture guard: a child governance scope's trace is folded into its parent turn's by exactly one
/// thing — <c>GovernedChildScope.DisposeAsync</c>. Source-scans the production tree and fails when anything
/// else calls <c>IGovernanceTraceRecorder.Absorb</c>.
/// </summary>
/// <remarks>
/// <para>
/// A child that is governed as its own agent records into its own DI scope's recorder, which is gone when the
/// scope is disposed. A surface that opened one and forgot to fold silently dropped every decision the child
/// made, and a tool the child was denied vanished from the turn's reported <c>Governance</c> trace — found at
/// delegation (#771) and again at Magentic participants (#769) before the fold became a property of the
/// handle. A hand-written <c>Absorb</c> at a call site is how that comes back: it is easy to place in the
/// wrong spot (before the work finishes, on only the success path) or to fold twice, and the trace is
/// cumulative, so folding twice double-counts.
/// </para>
/// <para>
/// Modelled on <see cref="AttributionIsPublishedByTheExecutionContextOnlyTests"/>.
/// </para>
/// </remarks>
public sealed class GovernanceTraceIsFoldedByTheChildScopeHandleOnlyTests
{
    // Any member access named Absorb, call or method group, whatever the spacing. Reflection and replaying
    // the child's decisions into the parent through Record/RecordEscalation are not visible to a text scan.
    private static readonly Regex Member = new(@"\.\s*Absorb\b", RegexOptions.Compiled);

    private const string TheFolder = "Application/Application.AI.Common/Services/Governance/GovernedChildScope.cs";

    /// <summary>
    /// Files permitted to name the member, each with the reason it is not a violation. Add here — with a
    /// reason — rather than deleting the check. Matched on the full relative path.
    /// </summary>
    private static readonly (string RelativePath, string Reason)[] Exemptions =
    [
        (TheFolder, "THE caller: folds the child's trace once, on dispose, before releasing the scope."),
    ];

    [Fact]
    public void Absorb_IsCalledOnlyByTheGovernedChildScope()
    {
        var violations = ReadProductionSources()
            .Where(s => Member.IsMatch(s.Code))
            .Select(s => s.Path.Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !Exemptions.Any(e => path.EndsWith("/" + e.RelativePath, StringComparison.Ordinal)))
            .ToList();

        if (violations.Count > 0)
        {
            Assert.Fail(
                "IGovernanceTraceRecorder.Absorb was called outside the governed child scope handle:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, violations.Select(v => "  " + v)) +
                Environment.NewLine + Environment.NewLine +
                "A child governance scope's trace is folded into its parent's by GovernedChildScope when it " +
                "is disposed. Open the child with GovernanceArmer.OpenChild and pass the parent turn's " +
                "recorder; do not fold by hand. A hand-written fold is how a trace is dropped (the child's " +
                "denials vanish from the turn's Governance trace, #771), folded before the work finished, " +
                "or folded twice (the trace is cumulative, so that double-counts).");
        }
    }

    [Theory]
    [InlineData("recorder.Absorb(trace);")]
    [InlineData("recorder. Absorb(trace);")]
    [InlineData("recorder.Absorb (trace);")]
    [InlineData("recorder?.Absorb(trace);")]
    [InlineData("Action<GovernanceTrace> fold = recorder.Absorb;")]
    public void TheMemberPattern_CatchesEveryShapeOfCallingAbsorb(string code)
    {
        Member.IsMatch(code).Should().BeTrue();
    }

    [Theory]
    [InlineData("var absorbed = 1;")]
    [InlineData("recorder.AbsorbAll(trace);")]
    public void TheMemberPattern_IgnoresUnrelatedNames(string code)
    {
        Member.IsMatch(code).Should().BeFalse();
    }

    [Fact]
    public void TheGovernedChildScope_StillFoldsTheTrace()
    {
        // The other direction: a guard that only forbids other callers passes happily when the fold is
        // deleted from the one caller it protects, which is the unguarded state itself.
        var folder = ReadProductionSources()
            .Where(s => s.Path.Replace(Path.DirectorySeparatorChar, '/')
                .EndsWith("/" + TheFolder, StringComparison.Ordinal))
            .ToList();

        folder.Should().ContainSingle($"{TheFolder} is the folder; if it moved, update TheFolder and Exemptions");
        folder[0].Code.Should().Contain(
            "Absorb(Pipeline.GetTrace())",
            "without this call a child scope's decisions are lost with its scope (#771)");
    }

    [Fact]
    public void EveryExemptedFile_StillExists()
    {
        var paths = ReadProductionSources()
            .Select(s => s.Path.Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        Exemptions
            .Where(e => !paths.Any(p => p.EndsWith("/" + e.RelativePath, StringComparison.Ordinal)))
            .Select(e => e.RelativePath)
            .Should().BeEmpty("an exemption for a file that no longer exists is a permission waiting to be inherited");
    }

    private static IReadOnlyList<(string Path, string Code)> ReadProductionSources()
    {
        var sources = SourceScan.ReadProductionSources(Path.Combine(RepoRoot.Path, "src", "Content"));

        sources.Should().HaveCountGreaterThan(2000,
            "the scan must cover the production tree; a much smaller scan means the root lookup or the "
            + "filter broke, and every assertion built on it is then meaningless");

        return sources;
    }
}
