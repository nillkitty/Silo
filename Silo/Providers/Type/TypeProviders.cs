using Silo.Contracts.Providers.Data.Shape;
using Silo.DbModel.Worlds;

namespace Silo.Providers;

public record TypeLookup(Uri CurrentNamespace, string Prefix, string Name);

public class SyntaxTypeProvider : ITypeProvider
{
    public string? DisplayName => "Syntaxes";
}

public class EnumTypeProvider : ITypeProvider
{
    public string? DisplayName => "Enums";
}

public class MetaverseTypeProvider : ITypeProvider
{
    public string? DisplayName => "Metaverse Types";
}

public class UnitTypeProvider : ITypeProvider
{
    public string? DisplayName => "Unit Types";
}

public class WorldTypeProvider(World w) : ITypeProvider
{
    public World   World       { get; } = w.Required();
    public string? DisplayName => $"World - {World.DisplayName}";
}

public class PersonaTypeProvider : ITypeProvider
{
    public string? DisplayName => "Personal Types";
}

public class DynamicTypeProvider : ITypeProvider
{
    public string? DisplayName => "Dynamic Types";
}