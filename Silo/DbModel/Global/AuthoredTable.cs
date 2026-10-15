using Silo.DbModel.Worlds;
using Silo.Tabular;

namespace Silo.DbModel.Global;

/// <summary>
///     A hand-authored table stored in the Silo's built-in <see cref="LocalWorld">local world</see>: data typed (or pasted)
///     in here rather than synchronised from somewhere else, so the Silo holds the authoritative copy. Each table is also
///     a <see cref="WorldType" /> of that world with a <see cref="WorldField" /> per column, so it can be treated like any
///     other source's type; the rows travel as JSON (<see cref="SheetData" />) until world row storage exists.
/// </summary>
/// <remarks>
///     Registered as a World-family entity: it lives in the local world's database, not in the main one. The provider
///     fills <see cref="World" /> and <see cref="WorldType" /> when saving and loading; callers only deal with the
///     name, description and sheet.
/// </remarks>
public class AuthoredTable
{
    public int Id { get; set; }

    /// <summary>The owning world (always the local world).</summary>
    public World World { get; set; } = null!;

    /// <summary>The world type that describes this table (name and columns).</summary>
    public WorldType WorldType { get; set; } = null!;

    /// <summary>The table's name (kept equal to <see cref="WorldType" />'s name when saved).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Free text about what the table is for.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The columns and rows, as <see cref="SheetData.ToJson" /> writes them.</summary>
    public string DataJson { get; set; } = string.Empty;

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime Modified { get; set; } = DateTime.UtcNow;

    public SheetData ReadSheet() => SheetData.FromJson(DataJson);

    public void WriteSheet(SheetData sheet) => DataJson = sheet.ToJson();

    public AuthoredTable Clone() => (AuthoredTable)MemberwiseClone();
}
