using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Tests.Common;

/// <summary>
/// The public type names an assembly declares, read from its metadata.
/// </summary>
/// <remarks>
/// Read from metadata rather than with <see cref="Assembly.GetExportedTypes"/> because that loads every
/// public type, and so every assembly those types mention. A package that hosts into ASP.NET Core would
/// then need the ASP.NET Core framework in the test process just to be asked what it exposes. This is for
/// canary tests that pin the shape of a package someone else owns: it changes only when the package's
/// public surface does.
/// </remarks>
public static class AssemblySurface
{
    /// <summary>The full names of the assembly's public top-level and nested public types, sorted.</summary>
    public static IReadOnlyList<string> PublicTypes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        return reader.TypeDefinitions
            .Select(reader.GetTypeDefinition)
            .Where(t => IsPublic(reader, t))
            .Select(t => FullName(reader, t))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsPublic(MetadataReader reader, TypeDefinition type)
    {
        var visibility = type.Attributes & TypeAttributes.VisibilityMask;
        if (visibility == TypeAttributes.Public)
            return true;

        return visibility == TypeAttributes.NestedPublic
            && IsPublic(reader, reader.GetTypeDefinition(type.GetDeclaringType()));
    }

    private static string FullName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
            return FullName(reader, reader.GetTypeDefinition(declaring)) + "+" + name;

        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
}
