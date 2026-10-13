namespace Domain.Common.Helpers;

/// <summary>
/// Resolves a path to the location the operating system would actually open, following symbolic links
/// and junctions on <em>every</em> segment — the parent chain first, then the segment itself.
/// </summary>
/// <remarks>
/// <para>
/// .NET has no <c>realpath</c>, and the built-in <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> only
/// inspects the final segment. Resolving the leaf alone leaves an obvious evasion open: a <em>directory</em>
/// link sitting above a path, pointing anywhere on disk. Every path under it then compares as the literal
/// string while opening something else, so a containment or overlap comparison made on the literal form
/// proves nothing.
/// </para>
/// <para>
/// Extracted from <c>EvalDatasetPathGuard</c> so the one tested implementation is shared by every guard that
/// needs it, rather than each hand-writing a second copy of a subtle walk. The caller must treat
/// <c>exhausted</c> as "unknown": it means the walk gave up with a link still unresolved, and a path that
/// might still be a link must never be admitted on the strength of where it looked like it pointed.
/// </para>
/// </remarks>
public static class LinkResolver
{
    /// <summary>
    /// Total symbolic links followed while resolving one path, across every segment.
    /// </summary>
    /// <remarks>
    /// A budget rather than a per-segment limit: without one, a chain of links each pointing at a path
    /// containing further links would multiply out instead of terminating.
    /// </remarks>
    public const int MaxLinkHops = 16;

    /// <summary>
    /// Resolves <paramref name="path"/> to the file or directory the operating system would actually open.
    /// </summary>
    /// <param name="path">An absolute path.</param>
    /// <param name="exhausted">
    /// <see langword="true"/> when the walk ran out of link budget, or met a link whose stored target is
    /// malformed, so the returned path may still be (or sit beneath) an unresolved link.
    /// </param>
    /// <returns>The resolved path. Meaningful only when <paramref name="exhausted"/> is <see langword="false"/>.</returns>
    public static string Resolve(string path, out bool exhausted)
    {
        var walk = new LinkWalk();
        var resolved = ResolveSegments(path, walk);
        exhausted = walk.Exhausted;
        return resolved;
    }

    /// <summary>Mutable state threaded through one path walk: links followed, and whether we ran out.</summary>
    /// <remarks>
    /// <see cref="Exhausted"/> exists so running out of budget is distinguishable from finishing. Both
    /// leave a path behind, but only one of them is a path we actually resolved — and an unresolved link
    /// that happens to sit inside an allowed root would sail through containment.
    /// </remarks>
    private sealed class LinkWalk
    {
        /// <summary>Links followed so far, across every segment of the path.</summary>
        public int Hops { get; set; }

        /// <summary>Whether the walk hit <see cref="MaxLinkHops"/> with a link still unresolved.</summary>
        public bool Exhausted { get; set; }
    }

    private static string ResolveSegments(string path, LinkWalk walk)
    {
        var parent = Path.GetDirectoryName(path);

        // No parent means a volume root ("C:\", "/"), which terminates the walk.
        if (string.IsNullOrEmpty(parent))
            return path;

        var current = Path.Combine(ResolveSegments(parent, walk), Path.GetFileName(path));

        string? linkTarget;
        try
        {
            // The raw target string, not ResolveLinkTarget's FileSystemInfo: a relative target has to be
            // resolved against the link's own directory, and reconstructing an absolute path from a
            // FileSystemInfo resolves it against the process's current directory instead.
            linkTarget = Directory.Exists(current)
                ? new DirectoryInfo(current).LinkTarget
                : new FileInfo(current).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as escaping. Return what we have; the containment check and the
            // File.Exists probe that follow both fail closed.
            return current;
        }

        if (linkTarget is null)
            return current;

        if (walk.Hops >= MaxLinkHops)
        {
            walk.Exhausted = true;
            return current;
        }

        walk.Hops++;

        // The target may itself sit beneath further links, so it goes through the same walk rather than
        // being trusted as final. This is also what bounds a link cycle: each turn costs a hop.
        string absoluteTarget;
        try
        {
            var linkDirectory = Path.GetDirectoryName(current);
            absoluteTarget = string.IsNullOrEmpty(linkDirectory)
                ? Path.GetFullPath(linkTarget)
                : Path.GetFullPath(linkTarget, linkDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A reparse point whose stored target is malformed. Unresolvable, so treat it exactly like a
            // budget exhaustion: we do not know where this leads, and every other failure mode here
            // refuses rather than guessing. Letting it throw would surface as a 500 instead.
            walk.Exhausted = true;
            return current;
        }

        return ResolveSegments(absoluteTarget, walk);
    }
}
