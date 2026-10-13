using Domain.Common.Helpers;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Tools;

/// <summary>
/// One-shot startup validator that refuses to boot when the file-system tool may write into a directory
/// that skill or agent content is loaded from (#824).
/// </summary>
/// <remarks>
/// <para>
/// <b>The exposure.</b> The agent registry hot-reloads <c>AGENT.md</c> files (<c>WatchForChanges</c> is on by
/// default) and the skill registry does the same for <c>SKILL.md</c>; even with watching off, the next refresh
/// or restart picks them up. If an allowed base path overlaps one of those roots, a file the model writes with
/// the file-system tool becomes a registered agent or skill, with whatever tool list its front matter claims.
/// <c>ManifestSecurityGate</c> scans that content, but it is the only barrier, and a content scan is not a
/// substitute for keeping the model's write surface away from the registry. This is a runtime writer of agent
/// <em>content</em>, not of agent roots, which is why <c>AgentsConfig</c> being configuration-only does not
/// cover it. The bundle staging root is included for the same reason: skill content is read from it at run
/// time (<see cref="SkillContentRoots.All"/>), and <c>BundleStagingService</c> only keeps it apart from the
/// discovery roots, not from the file-system tool's allowed paths.
/// </para>
/// <para>
/// <b>Either direction counts.</b> An allowed base path that contains a root (the repository-root style grant)
/// and one that sits inside a root (<c>agents/team-a/workspace</c>) are both exploitable, because the
/// registries scan nested directories.
/// </para>
/// <para>
/// <b>Links are resolved on every segment.</b> Both sides go through <see cref="LinkResolver"/>, not just the
/// final path component, because a symlinked <em>parent</em> (macOS <c>/var</c> to <c>/private/var</c>, a
/// symlinked app directory in a container) makes the same directory appear under two spellings. A path whose
/// links cannot be fully resolved is a refusal, not a pass. Extended-length prefixes (<c>\\?\</c>) are
/// stripped so they compare equal to the plain form.
/// </para>
/// <para>
/// <b>One malformed entry does not crash the host.</b> The registries log and skip a configured path they
/// cannot resolve; this validator does the same, so a bad entry that was tolerated before is still tolerated.
/// </para>
/// <para>
/// <b>Roots are read when the host starts, and this validator is registered after
/// <c>PluginStartupLoader</c>.</b> Plugins append their skill directories to the live configuration there.
/// Hosted services start in registration order under the Generic Host's default (sequential) start, which every
/// host in this repo uses, as does the sibling <c>PluginToolBoundaryStartupValidator</c>. A host that opts into
/// <c>HostOptions.ServicesStartConcurrently</c> would lose that guarantee.
/// </para>
/// <para>
/// <b>Not covered, stated rather than implied:</b> UNC aliases of a local drive (<c>\\localhost\C$\...</c>) and
/// <c>SUBST</c>/mapped drives, which no path comparison can see through; a case-only difference on a
/// case-insensitive file system that is not Windows (macOS), because the comparison there is ordinal; and a
/// configuration edited after boot, the same boundary the sibling <see cref="FileSystemSandboxStartupValidator"/>
/// has.
/// </para>
/// </remarks>
public sealed class FileSystemDiscoveryOverlapStartupValidator : IHostedService
{
    private readonly IReadOnlyList<string> _allowedBasePaths;
    private readonly Func<IEnumerable<string>> _rootPaths;
    private readonly ILogger<FileSystemDiscoveryOverlapStartupValidator> _logger;

    /// <summary>Initializes a new <see cref="FileSystemDiscoveryOverlapStartupValidator"/>.</summary>
    /// <param name="allowedBasePaths">
    /// The base paths the file-system tool may reach, exactly as registered with
    /// <c>FileSystemService</c>, so the assertion covers the paths the service enforces.
    /// </param>
    /// <param name="rootPaths">
    /// Returns the paths skill or agent content is loaded from, as configured (relative entries are resolved
    /// here, one at a time, so a malformed entry can be skipped). A function rather than a list because it is
    /// evaluated at <see cref="StartAsync"/>, after plugins have extended the configuration.
    /// </param>
    /// <param name="logger">Logger for the validation outcome.</param>
    public FileSystemDiscoveryOverlapStartupValidator(
        IReadOnlyList<string> allowedBasePaths,
        Func<IEnumerable<string>> rootPaths,
        ILogger<FileSystemDiscoveryOverlapStartupValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(allowedBasePaths);
        ArgumentNullException.ThrowIfNull(rootPaths);
        ArgumentNullException.ThrowIfNull(logger);

        _allowedBasePaths = allowedBasePaths;
        _rootPaths = rootPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// An allowed base path overlaps a root skill or agent content is loaded from, or a path's links could
    /// not be fully resolved.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bases = ResolveAll(_allowedBasePaths, "allowed base path");
        var roots = ResolveAll(_rootPaths(), "skill/agent root");

        var overlaps = (from allowed in bases
                        from root in roots
                        where PathScope.IsSameOrUnderNormalized(allowed, root)
                              || PathScope.IsSameOrUnderNormalized(root, allowed)
                        select new Overlap(allowed, root))
            .ToList();

        if (overlaps.Count == 0)
            return Task.CompletedTask;

        // Logged structurally as well as thrown: the exception message is one string for a human, while
        // these fields are what a log query can group and alert on.
        _logger.LogError(
            "Refusing to start: file-system tool allowed base path {AllowedBasePath} overlaps the skill/agent " +
            "content root {ContentRoot} ({OverlapCount} overlap(s) total).",
            overlaps[0].AllowedBasePath,
            overlaps[0].ContentRoot,
            overlaps.Count);

        throw new InvalidOperationException(BuildFailureMessage(overlaps));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private readonly record struct Overlap(string AllowedBasePath, string ContentRoot);

    /// <summary>
    /// Resolves each configured path to the directory it really names, skipping (with a warning) an entry the
    /// runtime cannot parse, and refusing an entry whose links cannot be fully followed.
    /// </summary>
    private List<string> ResolveAll(IEnumerable<string> paths, string kind)
    {
        var resolved = new List<string>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string normalized;
            try
            {
                normalized = PathScope.Normalize(SkillContentRoots.Resolve(StripExtendedPrefix(path)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // The registries skip a path they cannot resolve; so does this, rather than turning a
                // tolerated bad entry into a failed boot with an unrelated stack trace.
                _logger.LogWarning(ex, "Skipping unresolvable {Kind} '{Path}' in the discovery-overlap check", kind, path);
                continue;
            }

            var real = LinkResolver.Resolve(normalized, out var exhausted);
            if (exhausted)
            {
                // Not knowing where a path leads is a refusal: an unresolved link could point into (or out of)
                // a content root.
                throw new InvalidOperationException(
                    $"The {kind} '{path}' contains symbolic links that could not be fully resolved within " +
                    $"{LinkResolver.MaxLinkHops} hops, so it cannot be shown not to overlap the skill/agent content " +
                    "roots. Point the setting at a directory without a long or cyclic link chain.");
            }

            resolved.Add(PathScope.Normalize(real));
        }

        return [.. resolved.Distinct(PathScope.Comparer)];
    }

    /// <summary>
    /// Removes the Windows extended-length (<c>\\?\</c>, <c>\\.\</c>) and <c>\\?\UNC\</c> prefixes so the same
    /// directory compares equal however it was spelled. A no-op on other platforms and on plain paths.
    /// </summary>
    private static string StripExtendedPrefix(string path)
    {
        if (!OperatingSystem.IsWindows())
            return path;

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[@"\\?\UNC\".Length..];

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path[4..];

        return path;
    }

    private static string BuildFailureMessage(IReadOnlyList<Overlap> overlaps)
    {
        var detail = string.Join(
            "; ",
            overlaps.Select(o => $"allowed base path '{o.AllowedBasePath}' overlaps content root '{o.ContentRoot}'"));

        return
            $"{overlaps.Count} file-system tool allowed base path{(overlaps.Count == 1 ? " overlaps" : "s overlap")} " +
            $"a directory skill or agent content is loaded from: {detail}. A file the model writes there as " +
            "AGENT.md or SKILL.md would be registered as a live agent or skill with whatever tools its front " +
            "matter claims, leaving the manifest content scan as the only barrier. " +
            "Fix: narrow AppConfig:Infrastructure:FileSystem:AllowedBasePaths so it neither contains nor sits " +
            "inside AppConfig:AI:Skills / AppConfig:AI:Agents paths or the bundle staging root " +
            "(AppConfig:AI:BundleExecution:TempRoot); the shipped default, 'workspace', already satisfies this.";
    }
}
