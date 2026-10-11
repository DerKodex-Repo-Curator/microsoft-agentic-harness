namespace Tests.Common;

/// <summary>
/// A cross-assembly reference that is known not to resolve, and why that is accepted.
/// </summary>
/// <param name="Assembly">The assembly holding the reference, by simple name.</param>
/// <param name="MemberPrefix">The start of the referenced member, <c>Namespace.Type::Name</c>. A prefix, so
/// one entry covers one optional integration's whole namespace without listing each call.</param>
/// <param name="Reason">Why this is accepted rather than fixed: a package that is optional and not shipped,
/// a design-time tool that never loads in the host, or a skew that is tracked. A reason is required
/// because an unexplained entry is how a real break gets inherited.</param>
/// <param name="Kind">When set, the entry accepts only that kind of failure. An entry for a package that is
/// not shipped sets <see cref="MemberReferenceResolver.Kind.MissingAssembly"/>, so it stops excusing the
/// references the day the package does ship and a member of it turns out to be missing.</param>
public sealed record KnownSkew(
    string Assembly,
    string MemberPrefix,
    string Reason,
    MemberReferenceResolver.Kind? Kind = null);

/// <summary>
/// Asks one presentation host whether every call one of its assemblies makes into another still exists in
/// the version that host loads.
/// </summary>
/// <remarks>
/// <para>
/// The upgrade to Agent Framework 1.24 hit <see cref="MissingMethodException"/> at run time from package
/// skew that neither the compiler nor the container-validation sweep can see, and each break was then
/// guarded by a hand-written smoke test for that one surface. Those tests are a list someone has to extend;
/// adding a provider fails nothing. This checks the whole class from the member-reference tables
/// (<see cref="MemberReferenceResolver"/>), so a new break fails here without anyone having thought of it.
/// </para>
/// <para>
/// <strong>The allowlist is a ratchet in both directions.</strong> A finding with no entry fails, and an
/// entry that no longer matches any finding also fails, so a fixed skew has to be removed from the list
/// rather than left to excuse the next one.
/// </para>
/// <para>
/// <strong>It also fails when it goes blind.</strong> A scan that examined few references, or could not
/// bind many of them, would report no problems for the wrong reason, so those are checked too.
/// </para>
/// </remarks>
public static class PackageSkewGuard
{
    // A shipped host has hundreds of thousands of member references. A scan far below that has lost its
    // assemblies (a wrong output path, an empty load), not found a clean host.
    private const int MinimumReferencesExamined = 50_000;

    // References the resolver could not bind either way. A handful of exotic shapes is expected; a
    // meaningful share means the scan stopped answering the question.
    private const double MaximumUnverifiableShare = 0.01;

    private const int MaximumFindingsListed = 40;

    /// <summary>Scans <paramref name="hostProject"/> and returns every reason it should fail.</summary>
    /// <returns>An empty list when the host is clean.</returns>
    public static IReadOnlyList<string> Problems(string hostProject, IReadOnlyList<KnownSkew> known)
    {
        ArgumentNullException.ThrowIfNull(known);

        var result = MemberReferenceResolver.Scan(HostClosure.Load(hostProject));
        return Evaluate(result, known);
    }

    /// <summary>Applies the guard's rules to an already-completed scan.</summary>
    public static IReadOnlyList<string> Evaluate(MemberReferenceResolver.Result result, IReadOnlyList<KnownSkew> known)
    {
        var problems = new List<string>();

        if (result.Checked < MinimumReferencesExamined)
        {
            problems.Add(
                $"Only {result.Checked} member references were examined (expected at least "
                + $"{MinimumReferencesExamined}); the scan is not seeing the host's assemblies.");
        }

        if (result.Checked > 0 && (double)result.Unverifiable / result.Checked > MaximumUnverifiableShare)
        {
            problems.Add(
                $"{result.Unverifiable} of {result.Checked} member references could not be bound; the scan "
                + "is no longer answering the question for a meaningful share of the host.");
        }

        var unexplained = result.Unresolved.Where(f => !known.Any(k => Covers(k, f))).ToList();
        problems.AddRange(unexplained
            .Take(MaximumFindingsListed)
            .Select(f => $"{f.Assembly} -> {f.Member} [{f.Kind}]: {f.Detail}"));
        if (unexplained.Count > MaximumFindingsListed)
            problems.Add($"... and {unexplained.Count - MaximumFindingsListed} more unexplained references.");

        problems.AddRange(known
            .Where(k => !result.Unresolved.Any(f => Covers(k, f)))
            .Select(k => $"Stale entry, nothing matches it any more (remove it): {k.Assembly} -> {k.MemberPrefix}"));

        return problems;
    }

    private static bool Covers(KnownSkew entry, MemberReferenceResolver.Finding finding) =>
        entry.Assembly == finding.Assembly
        && finding.Member.StartsWith(entry.MemberPrefix, StringComparison.Ordinal)
        && (entry.Kind is null || entry.Kind == finding.Kind);
}
