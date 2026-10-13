using Domain.Common.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Tools;

/// <summary>
/// One-shot startup validator that refuses to boot when the file-system tool may write into a directory the
/// skill or agent registry scans (#824).
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
/// cover it.
/// </para>
/// <para>
/// <b>Either direction counts.</b> An allowed base path that contains a discovery root (the repository-root
/// style grant) and one that sits inside a discovery root (<c>agents/team-a/workspace</c>) are both exploitable,
/// because the registries scan nested directories.
/// </para>
/// <para>
/// <b>Roots are read when the host starts, and this validator is registered after
/// <c>PluginStartupLoader</c>.</b> Plugins append their skill directories to the live configuration during host
/// start. Hosted services start in registration order, so a validator registered earlier would check a root set
/// that is missing every plugin directory.
/// </para>
/// <para>
/// Not covered: the bundle staging root, which <c>BundleStagingService</c> already refuses to overlap with the
/// discovery roots, and a configuration edited after boot, which is the same boundary the sibling
/// <see cref="FileSystemSandboxStartupValidator"/> has.
/// </para>
/// </remarks>
public sealed class FileSystemDiscoveryOverlapStartupValidator : IHostedService
{
    private readonly IReadOnlyList<string> _allowedBasePaths;
    private readonly Func<IReadOnlyList<string>> _discoveryRoots;
    private readonly ILogger<FileSystemDiscoveryOverlapStartupValidator> _logger;

    /// <summary>Initializes a new <see cref="FileSystemDiscoveryOverlapStartupValidator"/>.</summary>
    /// <param name="allowedBasePaths">
    /// The base paths the file-system tool may reach, exactly as registered with
    /// <c>FileSystemService</c>, so the assertion covers the paths the service enforces.
    /// </param>
    /// <param name="discoveryRoots">
    /// Returns the skill and agent discovery roots. A function rather than a list because it is evaluated at
    /// <see cref="StartAsync"/>, after plugins have extended the configuration.
    /// </param>
    /// <param name="logger">Logger for the validation outcome.</param>
    public FileSystemDiscoveryOverlapStartupValidator(
        IReadOnlyList<string> allowedBasePaths,
        Func<IReadOnlyList<string>> discoveryRoots,
        ILogger<FileSystemDiscoveryOverlapStartupValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(allowedBasePaths);
        ArgumentNullException.ThrowIfNull(discoveryRoots);
        ArgumentNullException.ThrowIfNull(logger);

        _allowedBasePaths = allowedBasePaths;
        _discoveryRoots = discoveryRoots;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// An allowed base path overlaps a skill or agent discovery root.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var overlaps = FindOverlaps();
        if (overlaps.Count == 0)
            return Task.CompletedTask;

        // Logged structurally as well as thrown: the exception message is one string for a human, while
        // these fields are what a log query can group and alert on.
        _logger.LogError(
            "Refusing to start: file-system tool allowed base path {AllowedBasePath} overlaps the skill/agent " +
            "discovery root {DiscoveryRoot} ({OverlapCount} overlap(s) total).",
            overlaps[0].AllowedBasePath,
            overlaps[0].DiscoveryRoot,
            overlaps.Count);

        throw new InvalidOperationException(BuildFailureMessage(overlaps));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private readonly record struct Overlap(string AllowedBasePath, string DiscoveryRoot);

    /// <summary>
    /// Returns every (allowed base path, discovery root) pair where either is the same as, or nested under,
    /// the other. Both sides are canonicalized with the resolver the file-system service applies at runtime,
    /// so an overlap reached through a symlinked or 8.3-aliased parent is still found.
    /// </summary>
    private List<Overlap> FindOverlaps()
    {
        var bases = Canonicalize(_allowedBasePaths);
        var roots = Canonicalize(_discoveryRoots());

        return (from allowed in bases
                from root in roots
                where PathScope.IsSameOrUnderNormalized(allowed, root)
                      || PathScope.IsSameOrUnderNormalized(root, allowed)
                select new Overlap(allowed, root))
            .ToList();
    }

    private static List<string> Canonicalize(IEnumerable<string> paths) =>
        paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => SandboxPathCanonicalizer.Canonicalize(PathScope.Normalize(p)))
            .Distinct(PathScope.Comparer)
            .ToList();

    private static string BuildFailureMessage(IReadOnlyList<Overlap> overlaps)
    {
        var detail = string.Join(
            "; ",
            overlaps.Select(o => $"allowed base path '{o.AllowedBasePath}' overlaps discovery root '{o.DiscoveryRoot}'"));

        return
            $"{overlaps.Count} file-system tool allowed base path{(overlaps.Count == 1 ? " overlaps" : "s overlap")} " +
            $"a directory the skill or agent registry scans: {detail}. A file the model writes there as AGENT.md " +
            "or SKILL.md would be registered as a live agent or skill with whatever tools its front matter claims, " +
            "leaving the manifest content scan as the only barrier. " +
            "Fix: narrow AppConfig:Infrastructure:FileSystem:AllowedBasePaths so it neither contains nor sits " +
            "inside AppConfig:AI:Skills / AppConfig:AI:Agents paths (the shipped default, 'workspace', already " +
            "satisfies this).";
    }
}
