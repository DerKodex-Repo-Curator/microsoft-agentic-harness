using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// The package-skew scanner finds a call into a member that no longer exists, and only that. Proven against
/// a skew built here rather than a real one, so the proof does not rot when a real package is fixed: a
/// consumer is compiled against version 1 of a library and then run beside version 2 of it, which has lost
/// a plain method and a method on a generic type.
/// </summary>
public sealed class MemberReferenceResolverTests : IDisposable
{
    private const string LibraryName = "SkewFixtureLibrary";
    private const string ConsumerName = "SkewFixtureConsumer";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "skew-" + Guid.NewGuid().ToString("N"));

    public MemberReferenceResolverTests()
    {
        foreach (var version in new[] { "v1", "v2", "v3" })
            Directory.CreateDirectory(Path.Combine(_root, version));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A loaded assembly stays locked by its load context until the process exits. The folder is a
            // few kilobytes under the temp directory, so a failed cleanup is not worth failing a test for.
        }
    }

    [Fact]
    public void Scan_WhenTheLibraryStillHasEveryMemberTheConsumerCalls_FindsNothing()
    {
        BuildFixture(libraryV2Keeps: true);

        var result = Scan("v1");

        result.Checked.Should().BeGreaterThan(0, "the consumer's calls must actually have been examined");
        result.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public void Scan_WhenALibraryMethodWasRemoved_ReportsThatCallAndNotTheOnesThatSurvive()
    {
        BuildFixture(libraryV2Keeps: false);

        var result = Scan("v2");

        var plain = result.Unresolved.Should().ContainSingle(f => f.Member.StartsWith(LibraryName + ".Api::Value")).Subject;
        plain.Kind.Should().Be(MemberReferenceResolver.Kind.MissingMember);
        plain.Assembly.Should().Be(ConsumerName);
        result.Unresolved.Should().NotContain(f => f.Member.Contains("::Present"), "that member survived the upgrade");
    }

    [Fact]
    public void Scan_WhenAMethodOnAGenericTypeWasRemoved_ReportsIt()
    {
        // A call on Box<int> is a reference on a generic instantiation, which cannot be bound by the
        // runtime's resolver without constructing the type; it takes a different path through the scanner.
        BuildFixture(libraryV2Keeps: false);

        var result = Scan("v2");

        result.Unresolved.Should().ContainSingle(f => f.Member.StartsWith(LibraryName + ".Box`1<>::Get"));
    }

    [Fact]
    public void Scan_WhenABaseTypeGainedAnAbstractMember_ReportsTheSubclassThatCanNoLongerLoad()
    {
        // The other common symptom of skew: nothing calls into the base, so no member reference names the
        // break, but the runtime refuses to load the subclass.
        BuildFixture(libraryV2Keeps: false);

        var result = Scan("v2");

        var failure = result.Unresolved.Should().ContainSingle(f => f.Member.StartsWith(ConsumerName + ".Impl::<type load>")).Subject;
        failure.Kind.Should().Be(MemberReferenceResolver.Kind.MissingType);
    }

    [Fact]
    public void Scan_WhenTheLibraryIsAbsentEntirely_ReportsAMissingAssembly()
    {
        BuildFixture(libraryV2Keeps: true);

        var result = Scan("v3");

        result.Unresolved.Should().OnlyContain(f => f.Kind == MemberReferenceResolver.Kind.MissingAssembly);
        // Both shapes of reference: the plain one and the one on the generic instantiation.
        result.Unresolved.Should().Contain(f => f.Member.StartsWith(LibraryName + ".Api::"));
        result.Unresolved.Should().Contain(f => f.Member.StartsWith(LibraryName + ".Box`1<>::"));
    }

    private MemberReferenceResolver.Result Scan(string version)
    {
        var directory = Path.Combine(_root, version);
        var context = new DirectoryLoadContext(directory);
        var consumer = context.LoadFromAssemblyPath(Path.Combine(directory, ConsumerName + ".dll"));
        return MemberReferenceResolver.Scan([consumer]);
    }

    // v1 holds the library the consumer is compiled against and the consumer; v2 holds the consumer beside
    // a library that has lost Api.Value and Box<T>.Get (renamed), or kept them when libraryV2Keeps is set.
    private void BuildFixture(bool libraryV2Keeps)
    {
        var v1 = Path.Combine(_root, "v1");
        var v2 = Path.Combine(_root, "v2");
        SaveLibrary(Path.Combine(v1, LibraryName + ".dll"), removeMembers: false);
        SaveLibrary(Path.Combine(v2, LibraryName + ".dll"), removeMembers: !libraryV2Keeps);

        // Compiled against v1: load it so the consumer's IL can name its real members.
        var loadContext = new DirectoryLoadContext(v1);
        var library = loadContext.LoadFromAssemblyPath(Path.Combine(v1, LibraryName + ".dll"));
        SaveConsumer(Path.Combine(v1, ConsumerName + ".dll"), library);
        File.Copy(Path.Combine(v1, ConsumerName + ".dll"), Path.Combine(v2, ConsumerName + ".dll"));
        // v3: the consumer with no library beside it at all.
        File.Copy(Path.Combine(v1, ConsumerName + ".dll"), Path.Combine(_root, "v3", ConsumerName + ".dll"));
    }

    private static void SaveLibrary(string path, bool removeMembers)
    {
        var assembly = NewAssembly(LibraryName);
        var module = assembly.DefineDynamicModule(LibraryName);

        var api = module.DefineType(LibraryName + ".Api", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        // Value(int) is what the consumer calls; v2 replaces it with a method of a different name.
        var value = api.DefineMethod(
            removeMembers ? "ValueRenamed" : "Value",
            MethodAttributes.Public | MethodAttributes.Static, typeof(int), [typeof(int)]);
        var il = value.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
        var present = api.DefineMethod("Present", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        il = present.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        api.CreateType();

        var box = module.DefineType(LibraryName + ".Box`1", TypeAttributes.Public | TypeAttributes.Class);
        var t = box.DefineGenericParameters("T")[0];
        var get = box.DefineMethod(removeMembers ? "GetRenamed" : "Get", MethodAttributes.Public | MethodAttributes.Static, t, Type.EmptyTypes);
        il = get.GetILGenerator();
        il.DeclareLocal(t);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ret);
        box.CreateType();

        // The consumer subclasses this. v2 adds an abstract member, so the subclass no longer loads, and
        // nothing in the consumer calls into the base: no member reference names the break.
        var baseType = module.DefineType(
            LibraryName + ".Base", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Class);
        baseType.DefineDefaultConstructor(MethodAttributes.Family);
        const MethodAttributes abstractMember =
            MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual
            | MethodAttributes.HideBySig | MethodAttributes.NewSlot;
        baseType.DefineMethod("M", abstractMember, typeof(void), Type.EmptyTypes);
        if (removeMembers)
            baseType.DefineMethod("N", abstractMember, typeof(void), Type.EmptyTypes);
        baseType.CreateType();

        assembly.Save(path);
    }

    private static void SaveConsumer(string path, Assembly library)
    {
        var assembly = NewAssembly(ConsumerName);
        var module = assembly.DefineDynamicModule(ConsumerName);
        var caller = module.DefineType(ConsumerName + ".Caller", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var run = caller.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        var il = run.GetILGenerator();

        var api = library.GetType(LibraryName + ".Api")!;
        il.Emit(OpCodes.Ldc_I4_5);
        il.Emit(OpCodes.Call, api.GetMethod("Value")!);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Call, api.GetMethod("Present")!);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Call, library.GetType(LibraryName + ".Box`1")!.MakeGenericType(typeof(int)).GetMethod("Get")!);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        caller.CreateType();

        var impl = module.DefineType(
            ConsumerName + ".Impl", TypeAttributes.Public | TypeAttributes.Class, library.GetType(LibraryName + ".Base"));
        impl.DefineDefaultConstructor(MethodAttributes.Public);
        var overrideM = impl.DefineMethod(
            "M", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, typeof(void), Type.EmptyTypes);
        overrideM.GetILGenerator().Emit(OpCodes.Ret);
        impl.CreateType();

        assembly.Save(path);
    }

    private static PersistedAssemblyBuilder NewAssembly(string name) =>
        new(new AssemblyName(name) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);

    private sealed class DirectoryLoadContext(string directory) : AssemblyLoadContext(isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
