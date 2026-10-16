using Silo.Contracts.ConnectorServices;

namespace Silo.Contracts.Providers.Data.Shape;

/// <summary>A reference to a type as it is written: <c>clr:System.String</c>, or just <c>String</c> from inside <c>clr-namespace:System</c>.</summary>
/// <param name="CurrentNamespace">The namespace the reference is written in, which a name that is not fully qualified is looked for in.</param>
/// <param name="Prefix">The Silo prefix of the provider the type is asked of (<c>clr</c>, <c>dyn</c>).</param>
/// <param name="Name">The name, as written (qualified or not).</param>
public record TypeLookup(Uri CurrentNamespace, string Prefix, string Name);

/// <summary>Finds the types of one kind (the ones with its prefix).</summary>
public interface ITypeProvider : IDisplayName
{
    /// <summary>The type a reference names, or null when this provider does not know one by that prefix and name.</summary>
    IType? Lookup(TypeLookup lookup);

    /// <summary>The type that stands for a runtime type, or null when this provider has no such type.</summary>
    IType? Lookup(Type type);
}
