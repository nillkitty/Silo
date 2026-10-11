namespace Silo.DbModel.Global;

/// <summary>
///     One row per save of the Silo, written by the SQLite provider inside the saved file itself (so the history
///     travels with the document and reads the same for both storage formats). The file's last-write time equals
///     <see cref="SavedUtc" />. A file cannot contain its own hash, so each row stores the hash of the file as it was
///     <i>before</i> this save (<see cref="PreviousHash" />) -- the rows form a chain that can be checked against copies
///     of earlier versions; the save's own hash is carried by the <c>Saved</c> event and becomes the next row's value.
/// </summary>
public class SaveLogEntry
{
    public int      Id           { get; set; }
    public DateTime SavedUtc     { get; set; }
    public int      Sequence     { get; set; }
    public string   Format       { get; set; } = string.Empty;
    public string?  PreviousHash { get; set; }
    public string   User         { get; set; } = string.Empty;
    public string   Machine      { get; set; } = string.Empty;
    public string?  Source       { get; set; }
    public string?  Note         { get; set; }
}
