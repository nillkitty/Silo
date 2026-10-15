namespace Silo.Tabular;

/// <summary>
///     Keeps the raw data of every paste, so a table made from pasted data can always be traced back to what was actually
///     pasted. Implemented by the SQLite provider (the <c>Pasted</c> table of the Silo's scratch database, <c>x</c>).
/// </summary>
public interface IPastedStore
{
    /// <summary>Records a paste and returns the id of its row.</summary>
    /// <param name="source">Where it came from and went, in words ("clipboard -> new table 'Sales'").</param>
    /// <param name="type">The clipboard format the data was read from.</param>
    /// <param name="data">The data exactly as it was on the clipboard.</param>
    int AddPasted(string source, string type, byte[] data);
}
