using Silo.Contracts.Providers.Data.Shape;
using Silo.DbModel.Worlds;
using Telefrag.DI;

namespace Silo.Providers;

public record TypeLookup(Uri CurrentNamespace, string Prefix, string Name);

[Singleton(typeof(ITypeProvider))]
public class SyntaxTypeProvider : ITypeProvider
{
    public string? DisplayName => "Syntaxes";
}

[Singleton(typeof(ITypeProvider))]
public class EnumTypeProvider : ITypeProvider
{
    public string? DisplayName => "Enums";
}

[Singleton(typeof(ITypeProvider))]
public class MetaverseTypeProvider : ITypeProvider
{
    public string? DisplayName => "Metaverse Types";
}

[Singleton(typeof(ITypeProvider))]
public class UnitTypeProvider : ITypeProvider
{
    public string? DisplayName => "Unit Types";
}

public class WorldTypeProvider(World w) : ITypeProvider
{
    public World   World       { get; } = w.Required();
    public string? DisplayName => $"World - {World.DisplayName}";
}

[Singleton(typeof(ITypeProvider))]
public class PersonaTypeProvider : ITypeProvider
{
    public string? DisplayName => "Personal Types";
}

[Singleton(typeof(ITypeProvider))]
public class DynamicTypeProvider : ITypeProvider
{
    public string? DisplayName => "Dynamic Types";
}