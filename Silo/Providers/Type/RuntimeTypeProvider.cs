using System.Collections;
using System.Collections.Concurrent;
using System.Data;
using System.Dynamic;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Silo.Providers.Data.Shape;
using Silo.Providers.Reflection;
using Telefrag.DI;

namespace Silo.Providers;

/// <summary>
///     Provides access to runtime types with the prefix 'clr:'
/// </summary>
[Singleton(typeof(ITypeProvider))]
public class RuntimeTypeProvider : ITypeProvider
{
    public string? DisplayName => "Runtime Types";

    public static string? GetClrNamespace(Uri uriNamespace, bool throwErrors)
    {
        uriNamespace.Required();
        if (!uriNamespace.Scheme.Is("clr-namespace"))
            return !throwErrors ? null : throw new NotSupportedException("This provider only supports the URI scheme:  clr-namespace");

        // The path of "clr-namespace:System.Collections.Generic" is the namespace. It comes back percent-encoded
        // (GetUriForClrNamespace escapes it), so decode it.
        return Uri.UnescapeDataString(uriNamespace.AbsolutePath);
    }

    public static Uri GetUriForClrNamespace(string nsName)
    {
        nsName.Required();
        return new Uri($"clr-namespace:{Uri.EscapeDataString(nsName.Trim())}", UriKind.Absolute);
    }

    /// <summary>
    ///     Finds the runtime type a <c>clr:</c> reference names, in the loaded assemblies. The name may be fully qualified
    ///     (<c>System.Collections.Generic.List</c>), partly (<c>Collections.Generic.List</c> from inside
    ///     <c>clr-namespace:System</c>)
    ///     or just a name (<c>String</c> from inside <c>clr-namespace:System</c>); see
    ///     <see cref="IReflectionProvider.FindType" />.
    /// </summary>
    public IType? Lookup(TypeLookup lookup)
    {
        lookup.Required();
        if (!lookup.Prefix.Is("clr") || lookup.Name.Nill()) return null;

        // a namespace that is not a clr-namespace says nothing about where to look
        string? currentNs = lookup.CurrentNamespace is null ? null : GetClrNamespace(lookup.CurrentNamespace, false);

        var type = App.Require<IReflectionProvider>().FindType(lookup.Name.Trim(), currentNs);
        return type is null ? null : new RuntimeType(type);
    }

    public IType? Lookup(Type type)
    {
        return new RuntimeType(type.Required());
    }
}

public class RuntimeType(Type clrType) : IType
{
    private const string UriScheme = "clr-namespace";

    private static readonly ConcurrentDictionary<string, Type?> TypeCache = new(StringComparer.Ordinal);

    /// <summary>Framework structs that hold one atomic value, so are scalars rather than columnar.</summary>
    private static readonly HashSet<Type> ScalarStructs =
    [
        typeof(decimal), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan),
        typeof(Guid), typeof(DateOnly), typeof(TimeOnly),
        typeof(Half), typeof(Int128), typeof(UInt128), typeof(BigInteger),
        typeof(Complex)
    ];

    public Uri Namespace => RuntimeTypeProvider.GetUriForClrNamespace(Value.Namespace ?? "");
    public Uri Uri       => UriFromType(Value);

    public Type Value { get; } = clrType.Required();

    public DataKind Kind => GetKind(Value);


    public string    Name   => Value.Name;
    public string    Prefix => "clr";
    public DataShape Shape  => GetShape(Value);

    /// <summary>
    ///     Builds the URI of a runtime type:
    ///     <c>clr-namespace:&lt;namespace&gt;.&lt;type&gt;/&lt;type arguments...&gt;</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>&lt;namespace&gt;.&lt;type&gt;</c> is the type's CLR full name <i>without</i> its type arguments, so it
    ///         keeps the arity
    ///         marker of a generic (<c>List`1</c>) and uses <c>+</c> for nested types. Type arguments follow, one path segment
    ///         each;
    ///         an argument that is itself generic puts its own arguments in parentheses. An array adds its rank to the name
    ///         (<c>[]</c>, <c>[,]</c>, <c>[*]</c>, inner array first). Every name and rank is percent-encoded, so only
    ///         <c>/ ( )</c>
    ///         are structure. Examples (shown decoded):
    ///     </para>
    ///     <code>
    ///         System.String
    ///         System.Collections.Generic.List`1/System.String
    ///         System.Collections.Generic.Dictionary`2/System.String/System.Collections.Generic.List`1(System.Int32)
    ///         System.Int32[]            System.Collections.Generic.List`1[]/System.Int32
    ///         System.Collections.Generic.List`1        (the open generic type)
    ///     </code>
    ///     Pointers, by-ref types and unbound generic parameters have no URI (<see cref="NotSupportedException" />).
    /// </remarks>
    public static Uri UriFromType(Type value)
    {
        value.Required();
        if (value.IsPointer || value.IsByRef || value.IsGenericParameter)
            throw new NotSupportedException($"{value} cannot be represented as a {UriScheme} URI.");

        var (head, args) = Describe(value);
        string path = Escape(head);
        foreach (var a in args)
            path += "/" + EncodeArgument(a);

        return new Uri($"{UriScheme}:{path}", UriKind.Absolute);
    }

    /// <summary>
    ///     Resolves a URI of the form produced by <see cref="UriFromType" /> back to the runtime type, searching every loaded
    ///     assembly. Throws if the URI is malformed or the type cannot be found.
    /// </summary>
    public static Type TypeFromUri(Uri uri)
    {
        return TryTypeFromUri(uri, out var type, out var error) ? type! : throw error!;
    }

    /// <summary>Like <see cref="TypeFromUri" />, but returns null instead of throwing.</summary>
    public static Type? TryTypeFromUri(Uri uri)
    {
        return TryTypeFromUri(uri, out var type, out _) ? type : null;
    }

    private static bool TryTypeFromUri(Uri uri, out Type? type, out Exception? error)
    {
        type  = null;
        error = null;
        try
        {
            uri.Required();
            if (!uri.IsAbsoluteUri || !uri.Scheme.Is(UriScheme))
                throw new NotSupportedException($"Only {UriScheme}: URIs name runtime types: {uri}");

            var parser = new TypeUriParser(uri.AbsolutePath);
            type = parser.ParseTopLevel();
            return true;
        }
        catch (Exception ex) when (ex is FormatException or TypeLoadException or NotSupportedException or ArgumentException
                                      or InvalidOperationException)
        {
            error = ex;
            return false;
        }
    }

    /// <summary>The name (with rank suffix) of a type and, if it is a constructed generic, its type arguments.</summary>
    private static (string Head, Type[] Arguments) Describe(Type t)
    {
        string suffix = "";
        while (t.IsArray)
        {
            int rank = t.GetArrayRank();
            suffix = (t.IsSZArray ? "[]" : rank == 1 ? "[*]" : "[" + new string(',', rank - 1) + "]") + suffix;
            t      = t.GetElementType()!;
        }

        if (t.IsPointer || t.IsByRef || t.IsGenericParameter)
            throw new NotSupportedException($"{t} cannot be represented as a {UriScheme} URI.");

        if (t.IsConstructedGenericType)
            return (t.GetGenericTypeDefinition().FullName + suffix, t.GetGenericArguments());

        return (t.FullName + suffix, []);
    }

    private static string EncodeArgument(Type t)
    {
        var (head, args) = Describe(t);
        string s = Escape(head);
        return args.Length == 0 ? s : s + "(" + string.Join("/", args.Select(EncodeArgument)) + ")";
    }

    private static string Escape(string name)
    {
        return Uri.EscapeDataString(name);
    }

    private static Type? FindType(string fullName)
    {
        return TypeCache.GetOrAdd(fullName, static name =>
                                            {
                                                if (Type.GetType(name, false) is { } t) return t;

                                                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                                                    try
                                                    {
                                                        if (asm.GetType(name, false) is { } found) return found;
                                                    }
                                                    catch (Exception ex) when (ex is FileNotFoundException or FileLoadException
                                                                                                           or NotSupportedException)
                                                    {
                                                        // an assembly that cannot be inspected cannot hold the type
                                                    }

                                                return null;
                                            });
    }

    public static DataShape GetShape(Type value)
    {
        var ca = value.Required().GetCustomAttributes(true);
        foreach (var a in ca)
        {
            if (a is not Attribute aa) continue;
            if (aa is DataShapeAttribute dsa) return dsa.Shape;
        }

        if (value.IsAssignableTo(typeof(IDynamicMetaObjectProvider)))
            return DataShape.Dynamic;

        var tr = GetTraits(value, ca);

        if (tr.IsMultiTabular) return DataShape.Multitabular;
        if (tr.IsTabular) return DataShape.Tabular;
        if (tr.IsColumnar) return DataShape.Columnar;
        if (tr.IsLinear) return DataShape.Linear;
        if (tr.IsScalar) return DataShape.Scalar;
        if (tr.IsEnumerable) return DataShape.Container;
        return DataShape.Object;
    }

    public static bool IsScalar(Type t)
    {
        return GetTraits(t.Required()).IsScalar;
    }

    public static bool IsColumnar(Type t)
    {
        return GetTraits(t.Required()).IsColumnar;
    }

    public static bool IsLinear(Type t)
    {
        return GetTraits(t.Required()).IsLinear;
    }

    public static bool IsTabular(Type t)
    {
        return GetTraits(t.Required()).IsTabular;
    }

    public static bool IsMultiTabular(Type t)
    {
        return GetTraits(t.Required()).IsMultiTabular;
    }

    public static bool IsEnumerable(Type t)
    {
        return GetTraits(t.Required()).IsEnumerable;
    }

    public static TypeTraits GetTraits(Type value, object[]? ca = null)
    {
        ca ??= value.Required().GetCustomAttributes(true);

        // int? is shaped like int
        var core = Nullable.GetUnderlyingType(value) ?? value;

        bool Implements(Type genericDefinition)
        {
            return (core.IsGenericType && core.GetGenericTypeDefinition() == genericDefinition) || core.GetInterfaces()
                                                                                                       .Any(x => x.IsGenericType &&
                                                                                                                 x.GetGenericTypeDefinition() ==
                                                                                                                 genericDefinition);
        }

        // Scalar: one atomic value -- primitives, enums, strings and the framework's atomic structs (not structs in general).
        bool s = core.IsPrimitive || core.IsEnum || core == typeof(string) || ScalarStructs.Contains(core);

        // Columnar: a fixed set of named or positional values -- tuples and structs (including KeyValuePair).
        bool c = !s && (typeof(ITuple).IsAssignableFrom(core) || core.IsStruct());

        // Tabular: a table, or an array with more than one dimension. MultiTabular: a set of tables.
        // (Only the type itself or a subtype counts -- not a base type such as object or IListSource.)
        bool t = core.IsAssignableTo(typeof(DataTable)) || (core.IsArray && core.GetArrayRank() > 1);
        bool m = core.IsAssignableTo(typeof(DataSet));

        // Linear: a vector or list. string is excluded even though it is IEnumerable<char>.
        bool l = !s && !t && !m && ((core.IsArray && core.GetArrayRank() == 1) || typeof(IList).IsAssignableFrom(core) ||
                                    Implements(typeof(IList<>))                || Implements(typeof(IReadOnlyList<>)));

        // Enumerable: anything that can be iterated (this covers IEnumerable<T> too, as it extends IEnumerable).
        bool e = core != typeof(string) && typeof(IEnumerable).IsAssignableFrom(core);

        return new TypeTraits
               {
                   IsStruct                 = value.IsStruct(),
                   IsRecordClass            = value.IsRecordClass(),
                   IsRecordStruct           = value.IsRecordStruct(),
                   IsVisible                = value.IsVisible,
                   IsValueType              = value.IsValueType,
                   IsAbstract               = value.IsAbstract,
                   IsArray                  = value.IsArray,
                   IsByRef                  = value.IsByRef,
                   IsVariableBoundArray     = value.IsVariableBoundArray,
                   IsClass                  = value.IsClass,
                   IsConstructedGenericType = value.IsConstructedGenericType,
                   IsContextful             = value.IsContextful,
                   IsExplicitLayout         = value.IsExplicitLayout,
                   IsImport                 = value.IsImport,
                   IsCOMObject              = value.IsCOMObject,
                   IsPrimitive              = value.IsPrimitive,
                   IsInterface              = value.IsInterface,
                   IsNested                 = value.IsNested,
                   IsEnum                   = value.IsEnum,
                   IsNotPublic              = value.IsNotPublic,
                   IsPointer                = value.IsPointer,
                   IsSpecialName            = value.IsSpecialName,
                   IsGenericTypeDefinition  = value.IsGenericTypeDefinition,
                   IsSealed                 = value.IsSealed,
                   IsColumnar               = c,
                   IsLinear                 = l,
                   IsTabular                = t,
                   IsEnumerable             = e,
                   IsMultiTabular           = m,
                   IsScalar                 = s
               };
    }

    public static DataKind GetKind(Type value)
    {
        value.Required();

        // A type with unbound type arguments (List<>, or List<T> seen from inside generic code) is not usable until they are
        // supplied, whatever else it is -- so this comes first.
        if (value.ContainsGenericParameters)
            return DataKind.Open;

        // DynamicObject, ExpandoObject and anything else that binds its members at run time.
        if (value.IsAssignableTo(typeof(IDynamicMetaObjectProvider)))
            return DataKind.Dynamic;

        if (value.IsAssignableTo(typeof(ISyntax)))
            return DataKind.Syntax;

        // CLR types are typed by name. DataKind.Structural is never produced here: IStructuralEquatable (which arrays and
        // tuples implement) is about comparing values structurally, not about duck-typed definitions.
        return DataKind.Nominal;
    }

    /// <summary>Recursive-descent reader for the path grammar described at <see cref="UriFromType" />.</summary>
    private sealed class TypeUriParser(string path)
    {
        private static readonly Regex RankPattern = new(@"\[[,*]*\]", RegexOptions.Compiled);
        private                 int   _pos;

        public Type ParseTopLevel()
        {
            // top := head ( '/' argument )*      argument := head [ '(' argument ( '/' argument )* ')' ]
            string head = ReadHead();
            var    args = new List<Type>();
            while (_pos < path.Length)
            {
                Expect('/');
                args.Add(ParseArgument());
            }

            return Build(head, args);
        }

        private Type ParseArgument()
        {
            string head = ReadHead();
            var    args = new List<Type>();
            if (Peek() == '(')
            {
                _pos++;
                args.Add(ParseArgument());
                while (Peek() == '/')
                {
                    _pos++;
                    args.Add(ParseArgument());
                }

                Expect(')');
            }

            return Build(head, args);
        }

        private string ReadHead()
        {
            int start = _pos;
            while (_pos < path.Length && path[_pos] is not ('/' or '(' or ')')) _pos++;
            if (_pos == start) throw new FormatException($"Expected a type name at position {start} of '{path}'.");

            return Uri.UnescapeDataString(path[start.._pos]);
        }

        private char? Peek()
        {
            return _pos < path.Length ? path[_pos] : null;
        }

        private void Expect(char c)
        {
            if (Peek() != c) throw new FormatException($"Expected '{c}' at position {_pos} of '{path}'.");

            _pos++;
        }

        private static Type Build(string head, List<Type> args)
        {
            // split "Name[,][]" into the name and its rank suffixes (inner first)
            int end = head.Length;
            while (end > 0 && head[end - 1] == ']')
            {
                int open = head.LastIndexOf('[', end - 1);
                if (open < 0) throw new FormatException($"Unbalanced '[' in '{head}'.");
                end = open;
            }

            string name = head[..end];
            var    type = FindType(name) ?? throw new TypeLoadException($"The type '{name}' was not found in any loaded assembly.");

            if (args.Count > 0)
            {
                if (!type.IsGenericTypeDefinition || type.GetGenericArguments().Length != args.Count)
                    throw new FormatException($"'{name}' does not take {args.Count} type argument(s).");

                type = type.MakeGenericType(args.ToArray());
            }

            foreach (Match m in RankPattern.Matches(head[end..]))
            {
                string token = m.Value;
                type = token == "[]" ? type.MakeArrayType() : token == "[*]" ? type.MakeArrayType(1) : type.MakeArrayType(token.Length - 1);
            }

            return type;
        }
    }
}

public class DataShapeAttribute(DataShape shape) : Attribute
{
    public DataShape Shape { get; set; } = shape;
}

public struct TypeTraits
{
    public bool IsStruct                 { get; init; }
    public bool IsVisible                { get; init; }
    public bool IsRecordClass            { get; init; }
    public bool IsRecordStruct           { get; init; }
    public bool IsScalar                 { get; init; }
    public bool IsColumnar               { get; init; }
    public bool IsLinear                 { get; init; }
    public bool IsTabular                { get; init; }
    public bool IsMultiTabular           { get; init; }
    public bool IsEnumerable             { get; init; }
    public bool IsValueType              { get; init; }
    public bool IsAbstract               { get; init; }
    public bool IsArray                  { get; init; }
    public bool IsByRef                  { get; init; }
    public bool IsVariableBoundArray     { get; init; }
    public bool IsClass                  { get; init; }
    public bool IsConstructedGenericType { get; init; }
    public bool IsContextful             { get; init; }
    public bool IsExplicitLayout         { get; init; }
    public bool IsImport                 { get; init; }
    public bool IsCOMObject              { get; init; }
    public bool IsPrimitive              { get; init; }
    public bool IsInterface              { get; init; }
    public bool IsNested                 { get; init; }
    public bool IsEnum                   { get; init; }
    public bool IsNotPublic              { get; init; }
    public bool IsPointer                { get; init; }
    public bool IsSpecialName            { get; init; }
    public bool IsGenericTypeDefinition  { get; init; }
    public bool IsSealed                 { get; init; }
}

public static class TypeExtensions
{
    extension(Type type)
    {
        public bool IsRecord()
        {
            ArgumentNullException.ThrowIfNull(type);

            // Record classes/structs emit a compiler-generated "<Clone>$" method (or PrintMembers)
            // Record classes emit an EqualityContract property
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            return type.GetMethod("<Clone>$", flags) != null || type.GetProperty("EqualityContract", flags) != null;
        }

        public bool IsStruct()
        {
            ArgumentNullException.ThrowIfNull(type);
            return type.IsValueType && !type.IsPrimitive && !type.IsEnum;
        }

        public bool IsRecordClass()
        {
            return type.IsRecord() && !type.IsValueType;
        }

        public bool IsRecordStruct()
        {
            return type.IsRecord() && type.IsValueType;
        }
    }
}