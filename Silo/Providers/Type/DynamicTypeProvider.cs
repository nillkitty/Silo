using System.Dynamic;
using Silo.Providers.Data.Shape;
using Telefrag.DI;

namespace Silo.Providers;

/// <summary>
///     Type provider which provdies two types -- 'dynamic' (dyn:dynamic, type://DynamicTypeProvider/dynamic) which
///     is fully dynamic, and 'dto' (dyn:dto, type://DynamicTypeProvider/DynamicDto) which is a data-only dynamic
///     type
/// </summary>
[Singleton(typeof(ITypeProvider))]
public class DynamicTypeProvider : ITypeProvider
{
    public string? DisplayName => "Dynamic Types";

    public IType? Lookup(TypeLookup lookup)
    {
        lookup.Required();
        if (!lookup.Prefix.Is("dyn")) return null;

        return DynamicType.Parse(lookup.Name);
    }

    public IType? Lookup(Type type)
    {
        type.Required();
        if (type == typeof(DynamicObject))
            return DynamicType.Dynamic;
        if (type == typeof(ExpandoObject))
            return DynamicType.Dto;
        return null;
    }
}

public abstract record DynamicType(DynamicKind Kind) : IType
{
    public abstract Uri         Uri       { get; }
    public static   DynamicType Dto       { get; } = new DynamicDto();
    public static   DynamicType Dynamic   { get; } = new FullyDynamic();
    public          Uri         Namespace => new("ns://*", UriKind.Absolute);

    public abstract string    Name   { get; }
    public          string    Prefix => "dyn";
    public          DataShape Shape  => DataShape.Dynamic;

    DataKind IType.Kind => DataKind.Dynamic;

    /// <summary>The dynamic type with that name (<c>dynamic</c>, <c>dto</c> or <c>dynamicdto</c>, any case), or null.</summary>
    public static DynamicType? Parse(string? typeName)
    {
        if (typeName is null) return null;

        switch (typeName.ToLower())
        {
            case "dynamic":             return Dynamic;
            case "dto" or "dynamicdto": return Dto;
        }

        return null;
    }

    public record FullyDynamic() : DynamicType(DynamicKind.Full)
    {
        public override string Name { get; } = "Dynamic";
        public override Uri    Uri  => Namespace.Append("dynamic");
    }

    public record DynamicDto() : DynamicType(DynamicKind.Get | DynamicKind.Set | DynamicKind.Index)
    {
        public override string Name { get; } = "DTO";
        public override Uri    Uri  => Namespace.Append("dto");
    }
}

[Flags]
public enum DynamicKind
{
    /// <summary>
    ///     No dynamic elements -- type is fully static
    /// </summary>
    None = 0,

    /// <summary>
    ///     Property getters are resolved at runtime
    /// </summary>
    Get = 1,

    /// <summary>
    ///     Property setters are resolved at runtime
    /// </summary>
    Set = 2,

    /// <summary>
    ///     Method invocation is resolved at runtime
    /// </summary>
    Invoke = 4,

    /// <summary>
    ///     Indexing/subscripting is resolved at runtime
    /// </summary>
    Index = 8,

    /// <summary>
    ///     All member access is resolved at runtime
    /// </summary>
    Full = 0xFFFF
}