using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Rivalry.Tests;

/// <summary>
/// The dependent contract (.github/RELEASING.md): the public members dependents call, listed in DependentContract.txt under the minor version that
/// introduced them. Removing or reshaping one, or adding a member dependents must implement, needs a minor bump in RivalryCore/meitou.json;
/// adding a member they may call needs only the list updated. Rewrite the list with RIVALRY_WRITE_CONTRACT=1.
/// </summary>
public sealed class DependentContractTests
{
    /// <summary>
    /// The types dependents start from; every public Rivalry Core type their signatures reach joins the contract.
    /// </summary>
    static readonly Type[] Roots =
    [
        typeof(IRivalryApi), typeof(IRivalryState), typeof(IRivalryRegistry), typeof(RivalryOptions), typeof(HostileCandidate),
        typeof(PartyClaim), typeof(PartyView), typeof(WaitingView), typeof(Rule), typeof(IBehaviour), typeof(IDirector), typeof(Assessment),
        typeof(Decisions), typeof(BuiltIn), typeof(RivalryMod),
    ];

    /// <summary>
    /// The interfaces dependents implement: a new member there without a default body breaks every implementation.
    /// </summary>
    static readonly Type[] Implemented = [typeof(IBehaviour), typeof(IDirector)];

    /// <summary>
    /// Members the compiler writes for every record or object, which carry no contract of their own.
    /// </summary>
    static readonly HashSet<string> Generated = ["<Clone>$", "EqualityContract", "PrintMembers", "Equals", "GetHashCode", "ToString", "Deconstruct",
        "op_Equality", "op_Inequality", "Finalize", "MemberwiseClone", "GetType"];

    /// <summary>
    /// The environment variable that rewrites the list instead of checking it.
    /// </summary>
    const string WriteVariable = "RIVALRY_WRITE_CONTRACT";

    /// <summary>
    /// The surface matches the list at the manifest's minor; only additions dependents may ignore pass without a bump.
    /// </summary>
    [Fact]
    public void The_dependent_contract_changes_only_with_a_minor_bump()
    {
        string minor = ManifestMinor();
        var current = Surface();

        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            Write(minor, current);
            return;
        }

        var (listedMinor, listed) = Read();
        var removed = listed.Except(current).ToList();
        var mustImplement = current.Except(listed).Where(ImplementedMember).ToList();
        var added = current.Except(listed).Except(mustImplement).ToList();

        Assert.True(listedMinor == minor, $"meitou.json is at minor {minor} but DependentContract.txt lists {listedMinor}: rewrite it ({WriteVariable}=1)");
        Assert.True(removed.Count == 0 && mustImplement.Count == 0, Breaking(removed, mustImplement));
        Assert.True(added.Count == 0, $"New dependent members (a patch is fine): rewrite DependentContract.txt ({WriteVariable}=1)\n+ "
            + string.Join("\n+ ", added));
    }

    #region The surface

    /// <summary>
    /// One line per public member of every type the roots reach.
    /// </summary>
    static SortedSet<string> Surface()
    {
        var lines = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var type in Reached())
            foreach (string line in Lines(type))
                lines.Add(line);

        return lines;
    }

    /// <summary>
    /// The roots and every public Rivalry Core type their public signatures name, transitively.
    /// </summary>
    static HashSet<Type> Reached()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>(Roots);

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type)) continue;

            foreach (var named in Named(type).Where(Ours))
                queue.Enqueue(named);
        }

        return seen;
    }

    /// <summary>
    /// The types a type's base, interfaces and public members name, generic arguments and element types included.
    /// </summary>
    static IEnumerable<Type> Named(Type type)
    {
        var direct = new List<Type>(type.GetInterfaces());
        if (type.BaseType is not null) direct.Add(type.BaseType);

        foreach (var member in Members(type))
            direct.AddRange(Signature(member));

        return direct.SelectMany(Unwrap);
    }

    /// <summary>
    /// A type with its generic arguments and element type, recursively.
    /// </summary>
    static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        var inner = type.HasElementType ? [type.GetElementType()!] : type.IsGenericType ? type.GetGenericArguments() : [];
        foreach (var t in inner.SelectMany(Unwrap))
            yield return t;
    }

    /// <summary>
    /// The types one member's signature names.
    /// </summary>
    static IEnumerable<Type> Signature(MemberInfo member) => member switch
    {
        MethodInfo m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType),
        ConstructorInfo c => c.GetParameters().Select(p => p.ParameterType),
        PropertyInfo p => [p.PropertyType],
        FieldInfo f => [f.FieldType],
        EventInfo e => [e.EventHandlerType!],
        Type t => [t],
        _ => [],
    };

    /// <summary>
    /// A public type from the Rivalry Core assembly.
    /// </summary>
    static bool Ours(Type type) =>
        !type.IsGenericParameter && type.Assembly == typeof(RivalryMod).Assembly && (type.IsPublic || type.IsNestedPublic);

    /// <summary>
    /// The public members a type declares, less the compiler's own.
    /// </summary>
    static IEnumerable<MemberInfo> Members(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        return type.GetMembers(flags).Where(m => !Generated.Contains(m.Name) && !IsAccessor(m) && !m.IsDefined(typeof(CompilerGeneratedAttribute)));
    }

    /// <summary>
    /// A property's or event's get/set/add/remove method, listed with the property or event instead.
    /// </summary>
    static bool IsAccessor(MemberInfo member) => member is MethodInfo { IsSpecialName: true } m && !m.Name.StartsWith("op_", StringComparison.Ordinal);

    #endregion

    #region Lines

    /// <summary>
    /// The type's own line (its kind and base) and one per member.
    /// </summary>
    static IEnumerable<string> Lines(Type type)
    {
        string name = Name(type);
        yield return $"{name} : {Kind(type)}";

        if (type.IsEnum)
        {
            foreach (string value in Enum.GetNames(type))
                yield return $"{name}.{value} = {Convert.ToInt64(Enum.Parse(type, value))}";
            yield break;
        }

        foreach (var member in Members(type))
            yield return $"{name}.{Line(member)}";
    }

    /// <summary>
    /// What kind of type it is and what it derives from.
    /// </summary>
    static string Kind(Type type)
    {
        string kind = type.IsEnum ? "enum " + Name(Enum.GetUnderlyingType(type))
            : type.IsInterface ? "interface"
            : type.IsValueType ? "struct"
            : type.IsAbstract && type.IsSealed ? "static class"
            : type.IsAbstract ? "abstract class"
            : type.IsSealed ? "sealed class"
            : "class";
        if (type.IsEnum) return kind;

        var bases = type.GetInterfaces().Select(Name).Order(StringComparer.Ordinal).ToList();
        if (type.BaseType is { } b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum)) bases.Insert(0, Name(b));

        return bases.Count == 0 ? kind : $"{kind} {string.Join(", ", bases)}";
    }

    /// <summary>
    /// One member as a dependent calls it: name, parameter names and types, return type, settability.
    /// </summary>
    static string Line(MemberInfo member) => member switch
    {
        MethodInfo m => $"{Static(m.IsStatic)}{Abstract(m.IsAbstract)}{m.Name}{Generic(m)}({Parameters(m)}) : {Name(m.ReturnType)}",
        ConstructorInfo c => $"new({Parameters(c)})",
        PropertyInfo p => $"{Static(p.GetMethod?.IsStatic == true)}{Abstract(p.GetMethod?.IsAbstract == true)}{p.Name}{Index(p)} : "
            + $"{Name(p.PropertyType)}{Accessors(p)}",
        FieldInfo f => $"{Static(f.IsStatic)}{Readonly(f)}{f.Name} : {Name(f.FieldType)}{Constant(f)}",
        EventInfo e => $"event {e.Name} : {Name(e.EventHandlerType!)}",
        Type t => $"nested {t.Name}",
        _ => member.Name,
    };

    /// <summary>
    /// "static " for a static member.
    /// </summary>
    static string Static(bool yes) => yes ? "static " : "";

    /// <summary>
    /// "abstract " for a member without a body, which implementers must supply.
    /// </summary>
    static string Abstract(bool yes) => yes ? "abstract " : "";

    /// <summary>
    /// "const " or "readonly " for such a field.
    /// </summary>
    static string Readonly(FieldInfo f) => f.IsLiteral ? "const " : f.IsInitOnly ? "readonly " : "";

    /// <summary>
    /// A constant's value: dependents compile it into their own code.
    /// </summary>
    static string Constant(FieldInfo f) => f.IsLiteral ? $" = {f.GetRawConstantValue()}" : "";

    /// <summary>
    /// A generic method's type parameters.
    /// </summary>
    static string Generic(MethodInfo m) => m.IsGenericMethod ? $"<{string.Join(", ", m.GetGenericArguments().Select(Name))}>" : "";

    /// <summary>
    /// An indexer's parameters.
    /// </summary>
    static string Index(PropertyInfo p) => p.GetIndexParameters() is { Length: > 0 } ps ? $"[{string.Join(", ", ps.Select(Parameter))}]" : "";

    /// <summary>
    /// A method's parameters, in order.
    /// </summary>
    static string Parameters(MethodBase m) => string.Join(", ", m.GetParameters().Select(Parameter));

    /// <summary>
    /// A parameter with its name (dependents may pass it by name) and default (dependents may leave it out).
    /// </summary>
    static string Parameter(ParameterInfo p)
    {
        string modifier = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "";
        string fallback = p.HasDefaultValue ? $" = {p.DefaultValue ?? "null"}" : "";

        return $"{modifier}{Name(p.ParameterType)} {p.Name}{fallback}";
    }

    /// <summary>
    /// The public accessors a property offers.
    /// </summary>
    static string Accessors(PropertyInfo p)
    {
        bool get = p.GetMethod?.IsPublic == true;
        bool set = p.SetMethod?.IsPublic == true;
        bool init = set && p.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit");

        return $" {{ {(get ? "get; " : "")}{(set ? init ? "init; " : "set; " : "")}}}";
    }

    /// <summary>
    /// A type's name as C# spells it, generic arguments included.
    /// </summary>
    static string Name(Type type)
    {
        if (type.IsByRef) return Name(type.GetElementType()!);
        if (type.IsArray) return Name(type.GetElementType()!) + "[]";
        if (Nullable.GetUnderlyingType(type) is { } inner) return Name(inner) + "?";
        if (type.IsGenericParameter) return type.Name;

        string name = type.IsNested ? $"{Name(type.DeclaringType!)}.{type.Name}" : type.Name;
        if (!type.IsGenericType) return name;

        string args = string.Join(", ", type.GetGenericArguments().Select(Name));
        return $"{name[..name.IndexOf('`')]}<{args}>";
    }

    /// <summary>
    /// A new line that dependents must implement: an abstract member of an interface they implement.
    /// </summary>
    static bool ImplementedMember(string line) =>
        Implemented.Any(t => line.StartsWith(Name(t) + ".", StringComparison.Ordinal) && line.Contains(".abstract ", StringComparison.Ordinal));

    #endregion

    #region The list

    /// <summary>
    /// The minor version in RivalryCore/meitou.json, e.g. "0.6".
    /// </summary>
    static string ManifestMinor()
    {
        string manifest = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "meitou.json"));
        var version = Version.Parse(JsonDocument.Parse(manifest).RootElement.GetProperty("version").GetString()!.Split('-')[0]);

        return $"{version.Major}.{version.Minor}";
    }

    /// <summary>
    /// The checked-in list: its minor version and its lines.
    /// </summary>
    static (string Minor, SortedSet<string> Lines) Read()
    {
        var lines = File.ReadAllLines(ListPath()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        string minor = lines[0]["minor ".Length..];

        return (minor, new SortedSet<string>(lines.Skip(1), StringComparer.Ordinal));
    }

    /// <summary>
    /// Rewrites the checked-in list.
    /// </summary>
    static void Write(string minor, SortedSet<string> lines)
    {
        string[] header =
        [
            "# Rivalry Core's dependent contract (.github/RELEASING.md), written by DependentContractTests. Do not edit by hand.",
            $"minor {minor}",
        ];

        File.WriteAllLines(ListPath(), header.Concat(lines));
    }

    /// <summary>
    /// DependentContract.txt beside this source file.
    /// </summary>
    static string ListPath([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "DependentContract.txt");

    /// <summary>
    /// The failure message for a contract change at an unchanged minor.
    /// </summary>
    static string Breaking(List<string> removed, List<string> mustImplement) =>
        "The dependent contract changed: bump the minor in RivalryCore/meitou.json and every dependent's range (docs/versioning.md in GutenStructure), "
        + $"then rewrite DependentContract.txt ({WriteVariable}=1). Ship it as a patch instead by keeping these members.\n"
        + string.Join("\n", removed.Select(l => "- " + l).Concat(mustImplement.Select(l => "+ must implement: " + l)));

    #endregion
}
