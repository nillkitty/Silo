using Silo.Contracts.Providers.Data.Shape;
using Silo.DbModel.Worlds;
using Telefrag.DI;

namespace Silo.Providers;

[Singleton(typeof(ITypeProvider))]
public class SyntaxTypeProvider : ITypeProvider
{
    public string? DisplayName => "Syntaxes";

    public IType? Lookup(TypeLookup lookup)
    {
        return null; // not written yet: knows no types
    }

    public IType? Lookup(Type type)
    {
        return null; // not written yet: knows no types
    }
}

[Singleton(typeof(ITypeProvider))]
public class EnumTypeProvider : ITypeProvider
{
    public string? DisplayName => "Enums";

    public IType? Lookup(TypeLookup lookup)
    {
        return null; // not written yet: knows no types
    }

    public IType? Lookup(Type type)
    {
        return null; // not written yet: knows no types
    }
}

[Singleton(typeof(ITypeProvider))]
public class MetaverseTypeProvider : ITypeProvider
{
    public string? DisplayName => "Metaverse Types";

    public IType? Lookup(TypeLookup lookup)
    {
        return null; // not written yet: knows no types
    }

    public IType? Lookup(Type type)
    {
        return null; // not written yet: knows no types
    }
}

[Singleton(typeof(ITypeProvider))]
public class UnitTypeProvider : ITypeProvider
{
    public string? DisplayName => "Unit Types";

    public IType? Lookup(TypeLookup lookup)
    {
        return null; // not written yet: knows no types
    }

    public IType? Lookup(Type type)
    {
        return null; // not written yet: knows no types
    }
}

public class WorldTypeProvider(World w) : ITypeProvider
{
    public World   World       { get; } = w.Required();
    public string? DisplayName => $"World - {World.DisplayName}";

    public IType? Lookup(TypeLookup lookup)
    {
        return null; // not written yet: knows no types
    }

    public IType? Lookup(Type type)
    {
        return null; // not written yet: knows no types
    }
}

[Singleton(typeof(ITypeProvider))]
public class PersonaTypeProvider : ITypeProvider
{
    public string? DisplayName => "Personal Types";

    public IType? Lookup(TypeLookup lookup)
    {
        return null; // not written yet: knows no types
    }

    public IType? Lookup(Type type)
    {
        return null; // not written yet: knows no types
    }
}