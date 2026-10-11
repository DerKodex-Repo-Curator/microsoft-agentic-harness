using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// The rules <see cref="PackageSkewGuard"/> applies to a scan: an unexplained break fails, an allowlist entry
/// that no longer matches fails, and a scan that has gone blind fails instead of reporting clean.
/// </summary>
public sealed class PackageSkewGuardTests
{
    private const int Plenty = 300_000;

    private static MemberReferenceResolver.Finding Finding(string assembly, string member) =>
        new(assembly, member, MemberReferenceResolver.Kind.MissingMember, "Method not found");

    private static MemberReferenceResolver.Result Scan(int examined, int unverifiable, params MemberReferenceResolver.Finding[] findings) =>
        new(examined, findings, unverifiable);

    [Fact]
    public void Evaluate_ACleanScan_HasNoProblems()
    {
        PackageSkewGuard.Evaluate(Scan(Plenty, 0), []).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_AnUnexplainedFinding_IsAProblem()
    {
        var problems = PackageSkewGuard.Evaluate(Scan(Plenty, 0, Finding("Some.Lib", "Other.Type::Gone")), []);

        problems.Should().ContainSingle().Which.Should().Contain("Some.Lib -> Other.Type::Gone");
    }

    [Fact]
    public void Evaluate_AFindingCoveredByAnEntry_IsAccepted()
    {
        var known = new[] { new KnownSkew("Some.Lib", "Other.Type::", "optional integration") };

        PackageSkewGuard.Evaluate(Scan(Plenty, 0, Finding("Some.Lib", "Other.Type::Gone")), known).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_AnEntryIsMatchedByTheReferencingAssemblyToo_NotJustTheMember()
    {
        // The same member broken in a different assembly is a different break.
        var known = new[] { new KnownSkew("Some.Lib", "Other.Type::", "optional integration") };

        var problems = PackageSkewGuard.Evaluate(Scan(Plenty, 0, Finding("Another.Lib", "Other.Type::Gone")), known);

        problems.Should().Contain(p => p.Contains("Another.Lib -> Other.Type::Gone"));
    }

    [Fact]
    public void Evaluate_AnEntryForAnAbsentPackage_DoesNotExcuseAMissingMemberOnceThePackageShips()
    {
        // MissingAssembly is accepted while the package is not there. The day it ships at a mismatched
        // version the failure becomes a missing member, which the entry must not excuse.
        var known = new[]
        {
            new KnownSkew("Some.Lib", "Other.", "optional, not shipped", MemberReferenceResolver.Kind.MissingAssembly),
        };

        var problems = PackageSkewGuard.Evaluate(Scan(Plenty, 0, Finding("Some.Lib", "Other.Type::Gone")), known);

        problems.Should().Contain(p => p.Contains("Some.Lib -> Other.Type::Gone"));
    }

    [Fact]
    public void Evaluate_AnEntryNothingMatchesAnyMore_IsAProblem()
    {
        // The skew was fixed: the entry has to go, or it excuses the next break in that namespace.
        var known = new[] { new KnownSkew("Some.Lib", "Other.Type::", "optional integration") };

        var problems = PackageSkewGuard.Evaluate(Scan(Plenty, 0), known);

        problems.Should().ContainSingle().Which.Should().Contain("Stale entry").And.Contain("Some.Lib");
    }

    [Fact]
    public void Evaluate_AScanThatExaminedTooLittle_IsAProblem()
    {
        // A wrong output path or an empty load would otherwise read as a clean host.
        PackageSkewGuard.Evaluate(Scan(1_200, 0), []).Should().ContainSingle().Which.Should().Contain("1200 member references");
    }

    [Fact]
    public void Evaluate_AScanThatCouldNotBindMuchOfTheHost_IsAProblem()
    {
        PackageSkewGuard.Evaluate(Scan(Plenty, Plenty / 20), []).Should().ContainSingle().Which.Should().Contain("could not be bound");
    }

    [Fact]
    public void Evaluate_ALongListOfUnexplainedFindings_IsCappedButCounted()
    {
        var many = Enumerable.Range(0, 100).Select(i => Finding("Some.Lib", $"Type::Member{i}")).ToArray();

        var problems = PackageSkewGuard.Evaluate(Scan(Plenty, 0, many), []);

        problems.Should().HaveCount(41);
        problems[^1].Should().Contain("60 more");
    }
}
