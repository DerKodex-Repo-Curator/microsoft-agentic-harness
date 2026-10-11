using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace Tests.Common;

/// <summary>
/// Loads the assemblies a presentation host actually ships, bound the way that host binds them.
/// </summary>
/// <remarks>
/// <para>
/// A test project's own output folder is not that set: it adds the test runner and its helpers, and where
/// the host and the test project resolve a package to different versions the test project's win. Package
/// skew is a property of one host's dependency graph, so the question has to be asked of that graph. This
/// loads the host's own build output into a private <see cref="AssemblyLoadContext"/> that resolves
/// through the host's <c>deps.json</c>, so a reference between two of its assemblies binds to the version
/// the host would pick, and a framework reference falls through to the shared framework as it would at run
/// time.
/// </para>
/// <para>
/// The host project must already be built in the same configuration as the test project; a project
/// reference from the test project guarantees that.
/// </para>
/// </remarks>
public static class HostClosure
{
    /// <summary>Loads every managed assembly in the named host's build output.</summary>
    /// <param name="hostProject">The host project name, e.g. <c>Presentation.FoundryHost</c>.</param>
    /// <exception cref="InvalidOperationException">The host's build output cannot be found.</exception>
    public static IReadOnlyList<Assembly> Load(string hostProject)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostProject);

        var testOutput = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var hostOutput = Path.Combine(
            RepoRoot.Path, "src", "Content", "Presentation", hostProject,
            "bin", testOutput.Parent!.Name, testOutput.Name);
        var hostAssembly = Path.Combine(hostOutput, hostProject + ".dll");

        if (!File.Exists(hostAssembly))
        {
            throw new InvalidOperationException(
                $"{hostAssembly} does not exist. The host must be built in the test project's configuration "
                + "(a project reference from the test project does that).");
        }

        ThrowIfStale(hostAssembly, hostProject);

        var context = new HostLoadContext(hostAssembly);
        var loaded = new List<Assembly>();
        foreach (var path in Directory.EnumerateFiles(hostOutput, "*.dll"))
        {
            try
            {
                // By name, through the context's own resolution, so a copy the framework supersedes is
                // loaded from the framework exactly as it would be at run time.
                var assembly = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(path));

                // Only what the host ships is scanned; a superseded copy is the framework's, not ours.
                if (assembly.Location.StartsWith(hostOutput, StringComparison.OrdinalIgnoreCase))
                    loaded.Add(assembly);
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileNotFoundException or FileLoadException)
            {
                // A native library that shares the extension, or a stray file the host's deps.json does not
                // list and so would never load at run time: nothing to scan.
            }
        }

        return loaded;
    }

    // The host is built into its own folder under the test project's configuration. Where the two
    // configurations differ (a solution configuration that maps them apart) the folder derived above may
    // hold an old build, and scanning it would pass for the wrong reason. The test project's own copy of the
    // host is the build that was just made, so the two must be the same build.
    private static void ThrowIfStale(string hostAssembly, string hostProject)
    {
        var current = Path.Combine(AppContext.BaseDirectory, hostProject + ".dll");
        if (File.Exists(current) && ModuleVersionId(current) != ModuleVersionId(hostAssembly))
        {
            throw new InvalidOperationException(
                $"{hostAssembly} is not the build this test project references ({current}); it is an old output. "
                + "Build the host in the test project's configuration.");
        }
    }

    private static Guid ModuleVersionId(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return reader.GetGuid(reader.GetModuleDefinition().Mvid);
    }

    // The test process's trusted platform assemblies: the frameworks it runs on and, unhelpfully, its own
    // packages. Callers keep only the framework folder they want.
    private static IEnumerable<string> PlatformAssembliesUnder(string frameworkFolder)
    {
        var marker = Path.DirectorySeparatorChar + frameworkFolder + Path.DirectorySeparatorChar;
        return ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    // The base class library, by simple name: the one thing a host does not ship and does not need a
    // deps.json entry for. Everything else must come from the host's own graph or the ASP.NET Core framework.
    private static readonly Lazy<IReadOnlySet<string>> BaseClassLibrary = new(() =>
        PlatformAssembliesUnder("Microsoft.NETCore.App")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    // The ASP.NET Core shared framework's assemblies, by simple name: ASP.NET Core itself plus the
    // Microsoft.Extensions.* libraries it bundles.
    private static readonly Lazy<IReadOnlyDictionary<string, string>> AspNetCoreFramework = new(() =>
    {
        var directory = PlatformAssembliesUnder("Microsoft.AspNetCore.App").Select(Path.GetDirectoryName).FirstOrDefault();

        return directory is null
            ? new Dictionary<string, string>()
            : Directory.EnumerateFiles(directory, "*.dll")
                .ToDictionary(p => Path.GetFileNameWithoutExtension(p), p => p, StringComparer.OrdinalIgnoreCase);
    });

    private sealed class HostLoadContext(string hostAssembly) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(hostAssembly))
    {
        private readonly AssemblyDependencyResolver _resolver = new(hostAssembly);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var appPath = _resolver.ResolveAssemblyToPath(assemblyName);

            // The runtime host keeps the higher version when the app and a shared framework both supply an
            // assembly, so a stale app-local copy (an old netstandard shim, a lower Microsoft.Extensions.*)
            // never wins over the framework's. The framework copy is loaded into this context rather than
            // left to the default one, so the host's graph does not mix in the test process's own packages.
            if (assemblyName.Name is not null
                && AspNetCoreFramework.Value.TryGetValue(assemblyName.Name, out var frameworkPath)
                && (appPath is null
                    || AssemblyName.GetAssemblyName(frameworkPath).Version >= AssemblyName.GetAssemblyName(appPath).Version))
            {
                return LoadFromAssemblyPath(frameworkPath);
            }

            if (appPath is not null)
                return LoadFromAssemblyPath(appPath);

            // The base class library is the default context's. Nothing else falls through to it: the default
            // context holds the test project's own packages, and a host reference to something the host does
            // not ship must fail here as it would at run time, not bind to a copy only the tests have.
            return assemblyName.Name is not null && BaseClassLibrary.Value.Contains(assemblyName.Name)
                ? null
                : throw new FileNotFoundException(
                    $"Could not load file or assembly '{assemblyName.FullName}'. The host does not ship it.");
        }
    }
}
