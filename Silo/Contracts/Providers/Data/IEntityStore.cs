using Silo.DbModel.Worlds;

namespace Silo.Providers.Data;

/// <summary>
///     Reads the Silo's persisted entities back (the Silo database provider can write them with
///     <see cref="IDatabaseProvider.AddItem{TItem}" />, which upserts by an <c>Id</c> the caller chooses; this is how a caller finds what is
///     there and picks free ids).
/// </summary>
public interface IEntityStore
{
    /// <summary>
    ///     Every stored entity of type <typeparamref name="T" />. References to other entities are filled in. Entities of a World (a type, a
    ///     field, a mapping...) live in that World's database: name it with <paramref name="worldKey" />.
    /// </summary>
    IReadOnlyList<T> ListItems<T>(string? worldKey = null) where T : class;

    /// <summary>The Worlds the Silo has.</summary>
    IReadOnlyList<World> ListWorlds();
}
