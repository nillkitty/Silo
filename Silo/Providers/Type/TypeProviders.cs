using Serilog;
using Silo.DbModel.Worlds;
using Silo.Providers.Data;
using Silo.Providers.Data.Shape;
using Silo.Providers.Definitions;
using Telefrag.DI;

namespace Silo.Providers;

/// <summary>
///     Finds the syntaxes defined in the active Silo (<c>syntax:Name</c>, by name or alias; see
///     <see cref="ISyntaxStore" />). New Silos start with the
///     predefined ones: Timestamp, Duration, Guid, Regex, Wildcard, IPAddress, IPSubnet, Version, Oid, SecurityIdentifier,
///     JObject (jobj), JArray (jarr),
///     JToken (jtok), Base64, Html, Csv, Tsv, CliArgs and CliCommand.
/// </summary>
[Singleton(typeof(ITypeProvider))]
public class SyntaxTypeProvider : ITypeProvider
{
    public string? DisplayName => "Syntaxes";

    public IType? Lookup(TypeLookup lookup)
    {
        try
        {
            return DefinedTypes.LookupSyntax(App.Instance?.ActiveSilo?.Data as ISyntaxStore, lookup);
        }
        catch (Exception ex) when (ex is not ArgumentNullException)
        {
            Log.Warning(ex, "Looking up a syntax failed");
            return null;
        }
    }

    public IType? Lookup(Type type)
    {
        return null; // runtime types are the CLR provider's
    }
}

/// <summary>Finds the enums defined in the active Silo (<c>enum:Name</c>; see <see cref="IEnumStore" />).</summary>
[Singleton(typeof(ITypeProvider))]
public class EnumTypeProvider : ITypeProvider
{
    public string? DisplayName => "Enums";

    public IType? Lookup(TypeLookup lookup)
    {
        try
        {
            return DefinedTypes.LookupEnum(App.Instance?.ActiveSilo?.Data as IEnumStore, lookup);
        }
        catch (Exception ex) when (ex is not ArgumentNullException)
        {
            Log.Warning(ex, "Looking up an enum failed");
            return null;
        }
    }

    public IType? Lookup(Type type)
    {
        return null; // runtime types are the CLR provider's
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