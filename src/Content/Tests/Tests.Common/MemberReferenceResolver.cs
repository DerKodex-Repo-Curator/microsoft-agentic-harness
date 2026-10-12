using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Tests.Common;

/// <summary>
/// Resolves every cross-assembly member reference (method, constructor, field) in a set of assemblies
/// against the assembly versions actually loaded, the way the runtime does on first use, and reports the
/// ones that no longer resolve.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it catches.</strong> Package skew: assembly A was compiled against version 1 of B, but
/// version 2 is what loads, and a member A calls was removed or changed shape. Neither the compiler (it
/// only sees what it compiled against) nor a container-validation sweep (it only builds object graphs)
/// can see it; it surfaces as a <see cref="MissingMethodException"/> the first time that call is made,
/// possibly in production. A hand-written smoke test guards only the surfaces someone thought to test;
/// this asks the question of every reference at once.
/// </para>
/// <para>
/// <strong>How.</strong> <see cref="System.Reflection.Metadata"/> enumerates the member-reference table.
/// A reference on a plain type is bound by <see cref="Module.ResolveMember(int)"/>, which asks the
/// runtime's own resolver, so there is no signature matching of our own to get subtly wrong. A reference
/// on a generic instantiation (<c>List&lt;T&gt;</c>, <c>Task&lt;T&gt;</c>) cannot be bound without
/// instantiating the type, and instantiating with stand-in arguments fails generic constraints for
/// reasons unrelated to skew. Those are checked against the open generic definition instead: the type
/// must resolve, and a member of that name with that many parameters and that generic arity must exist.
/// That is weaker than the runtime's own match (a parameter whose type changed but whose count did not
/// is missed) and the weakness is deliberate and visible, not hidden.
/// </para>
/// <para>
/// <strong>Type loading.</strong> The other common symptom of skew is a type that will not load: its base
/// type or an interface gained an abstract member, or lives in an assembly that is not there. Nothing calls
/// into such a type, so no member reference names it. Each scanned assembly's declared types are loaded for
/// that reason, and the ones the runtime refuses are reported.
/// </para>
/// <para>
/// <strong>What it cannot see.</strong> Behaviour changes (a member that still exists but does something
/// else); references inside the assembly's own module; a type that is only named (<c>typeof</c>, a cast, a
/// <c>catch</c> clause) rather than called or derived from; and the few shapes it cannot bind, counted as
/// <see cref="Result.Unverifiable"/> so the size of the blind spot is always on the table. It also checks
/// only the platform and framework it runs on: assets under <c>runtimes/&lt;other-rid&gt;/</c> (a Linux-only
/// build of a native-adjacent library, when run on Windows) are not scanned, and a local run with a roll-forward
/// override binds the framework of the newer runtime, not the one the host targets. CI runs the target runtime.
/// </para>
/// </remarks>
public static class MemberReferenceResolver
{
    /// <summary>Why a reference did not resolve.</summary>
    public enum Kind
    {
        /// <summary>The assembly that should hold the member is not in the loaded set at all.</summary>
        MissingAssembly,

        /// <summary>The assembly loaded, but the type is not in it, or cannot be loaded.</summary>
        MissingType,

        /// <summary>The type loaded, but no member matches: removed, renamed or reshaped.</summary>
        MissingMember,
    }

    /// <summary>One member reference that did not resolve.</summary>
    /// <param name="Assembly">The assembly containing the reference.</param>
    /// <param name="Member">The referenced member as <c>Namespace.Type::Name</c>.</param>
    /// <param name="Kind">Why it did not resolve.</param>
    /// <param name="Detail">The runtime's own message.</param>
    public sealed record Finding(string Assembly, string Member, Kind Kind, string Detail);

    /// <summary>The outcome of a scan.</summary>
    /// <param name="Checked">Member references examined.</param>
    /// <param name="Unresolved">The ones that did not resolve.</param>
    /// <param name="Unverifiable">The ones this scan could not bind either way.</param>
    public sealed record Result(int Checked, IReadOnlyList<Finding> Unresolved, int Unverifiable);

    private const BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>Scans each assembly's member-reference table.</summary>
    public static Result Scan(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var unresolved = new List<Finding>();
        var unverifiable = 0;
        var examined = 0;

        foreach (var assembly in assemblies)
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                continue;

            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                continue;

            var reader = pe.GetMetadataReader();
            var name = assembly.GetName().Name ?? assembly.Location;

            foreach (var handle in reader.MemberReferences)
            {
                var reference = reader.GetMemberReference(handle);
                if (reference.Parent.Kind is not (HandleKind.TypeReference or HandleKind.TypeSpecification))
                    continue; // Same-module or module-level reference: compiled together, cannot skew.

                examined++;
                var outcome = Check(assembly.ManifestModule, reader, handle, reference, out var failure);
                if (outcome == Outcome.Unverifiable)
                    unverifiable++;
                else if (outcome == Outcome.Failed)
                    unresolved.Add(failure! with { Assembly = name });
            }

            unverifiable += AddTypeLoadFailures(assembly, name, unresolved);
        }

        return new Result(examined, unresolved, unverifiable);
    }

    // A member reference only exists where code calls something. The other common symptom of skew is a type
    // that will not load at all: its base type or an interface it implements gained an abstract member, or
    // lost one, or lives in an assembly that is not there. Nothing calls into that type, so no reference
    // names it; asking the runtime to load every type the assembly declares is the only way to see it.
    // Loading a type runs no static constructor.
    private static int AddTypeLoadFailures(Assembly assembly, string name, List<Finding> unresolved)
    {
        try
        {
            assembly.GetTypes();
            return 0;
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var loaderException in ex.LoaderExceptions.OfType<Exception>().DistinctBy(e => e.Message))
            {
                // Named by what could not be found, so an allowlist entry for an absent package matches it the
                // same way it matches that package's member references.
                var (kind, missing) = loaderException switch
                {
                    FileNotFoundException { FileName: { } file } => (Kind.MissingAssembly, new AssemblyName(file).Name + ".<missing assembly>"),
                    FileLoadException { FileName: { } file } => (Kind.MissingAssembly, new AssemblyName(file).Name + ".<missing assembly>"),
                    TypeLoadException { TypeName: { } typeName } => (Kind.MissingType, typeName),
                    _ => (Kind.MissingType, "<type>"),
                };
                unresolved.Add(new Finding(name, missing + "::<type load>", kind, loaderException.Message));
            }

            return 0;
        }
        catch (Exception ex) when (ex is NotSupportedException or BadImageFormatException)
        {
            return 1; // Could not enumerate this assembly's types: counted as a blind spot, not a pass.
        }
    }

    private enum Outcome { Resolved, Failed, Unverifiable }

    private static Outcome Check(
        Module module, MetadataReader reader, MemberReferenceHandle handle, MemberReference reference, out Finding? failure)
    {
        failure = null;

        // Only a failure needs the member's name, and there are about three hundred thousand references
        // per host, so it is built on that path alone.
        try
        {
            if (reference.Parent.Kind == HandleKind.TypeReference)
            {
                module.ResolveMember(MetadataTokens.GetToken(handle));
                return Outcome.Resolved;
            }

            return CheckGenericInstance(module, reader, reference, out failure);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
        {
            failure = new Finding("", Describe(reader, reference), Kind.MissingAssembly, ex.Message);
        }
        catch (TypeLoadException ex)
        {
            failure = new Finding("", Describe(reader, reference), Kind.MissingType, ex.Message);
        }
        catch (MissingMemberException ex)
        {
            failure = new Finding("", Describe(reader, reference), Kind.MissingMember, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or BadImageFormatException)
        {
            return Outcome.Unverifiable;
        }

        return Outcome.Failed;
    }

    // A reference on a generic instantiation: the type must resolve, and the open definition must hold a
    // member of that name with that shape. Anything that is not a plain generic instantiation (an array's
    // synthesized accessors, a type parameter) is runtime-provided and cannot be skewed.
    private static Outcome CheckGenericInstance(
        Module module, MetadataReader reader, MemberReference reference, out Finding? failure)
    {
        failure = null;
        var spec = reader.GetTypeSpecification((TypeSpecificationHandle)reference.Parent);
        var blob = reader.GetBlobReader(spec.Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
            return Outcome.Unverifiable;

        blob.ReadByte(); // class / valuetype marker
        var definitionHandle = blob.ReadTypeHandle();
        if (definitionHandle.Kind != HandleKind.TypeReference)
            return Outcome.Resolved; // Defined in this module: compiled together.

        var definition = module.ResolveType(MetadataTokens.GetToken(definitionHandle));
        var signature = reader.GetBlobReader(reference.Signature);
        var header = signature.ReadSignatureHeader();
        var isField = header.Kind == SignatureKind.Field;
        var genericArity = !isField && header.Attributes.HasFlag(SignatureAttributes.Generic)
            ? signature.ReadCompressedInteger()
            : 0;
        var parameterCount = isField ? 0 : signature.ReadCompressedInteger();
        var memberName = reader.GetString(reference.Name);

        if (Exists(definition, memberName, isField, genericArity, parameterCount))
            return Outcome.Resolved;

        failure = new Finding(
            "",
            Describe(reader, reference),
            Kind.MissingMember,
            $"No member '{memberName}' with {parameterCount} parameter(s) on {definition.FullName}.");
        return Outcome.Failed;
    }

    private static bool Exists(Type type, string name, bool isField, int genericArity, int parameterCount)
    {
        // Interfaces do not list inherited interface members, and a reference may name the derived type.
        var types = new[] { type }.Concat(type.IsInterface ? type.GetInterfaces() : []);
        foreach (var candidateType in types)
        {
            if (isField)
            {
                if (candidateType.GetField(name, AllMembers | BindingFlags.FlattenHierarchy) is not null)
                    return true;

                continue;
            }

            MethodBase[] methods = name == ".ctor"
                ? candidateType.GetConstructors(AllMembers)
                : candidateType.GetMethods(AllMembers | BindingFlags.FlattenHierarchy)
                    .Where(m => m.Name == name && (m.IsGenericMethodDefinition ? m.GetGenericArguments().Length : 0) == genericArity)
                    .ToArray();

            if (methods.Any(m => m.GetParameters().Length == parameterCount))
                return true;
        }

        return false;
    }

    private static string Describe(MetadataReader reader, MemberReference reference)
    {
        var parent = reference.Parent.Kind switch
        {
            HandleKind.TypeReference => TypeName(reader, (TypeReferenceHandle)reference.Parent),
            HandleKind.TypeSpecification => SpecName(reader, (TypeSpecificationHandle)reference.Parent),
            _ => reference.Parent.Kind.ToString(),
        };

        return parent + "::" + reader.GetString(reference.Name);
    }

    private static string SpecName(MetadataReader reader, TypeSpecificationHandle handle)
    {
        var blob = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
            return "<type spec>";

        blob.ReadByte();
        var inner = blob.ReadTypeHandle();
        return inner.Kind == HandleKind.TypeReference
            ? TypeName(reader, (TypeReferenceHandle)inner) + "<>"
            : "<type spec>";
    }

    private static string TypeName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }
}
