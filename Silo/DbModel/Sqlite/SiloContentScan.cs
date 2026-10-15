using System.IO;
using System.IO.Compression;
using Serilog;

namespace Silo.DbModel.Sqlite;

/// <summary>
///     Looks inside a <c>.silo</c> file for program files (<c>.dll</c> / <c>.exe</c>) without extracting or opening it.
/// </summary>
/// <remarks>
///     This version of Silo never loads or runs code from a Silo file: Silos are data. A Silo can still carry such files
///     (a Solo Silo's <c>assemblies/</c> folder, or one made by a later version), and they are kept untouched when the Silo
///     is saved, so a newer version could use them. Until then the person is warned when they open one, so nothing is
///     silently ignored. If code in Silo files is ever supported it needs a real trust step first (in the manner of
///     installing an editor extension), not just this notice.
///     Only the zip (Archive) layout can hold such files; a Simple-format Silo is a bare SQLite file. The inner archive of
///     an encrypted Silo is not inspected.
/// </remarks>
public static class SiloContentScan
{
    /// <summary>Extensions treated as program files.</summary>
    private static readonly string[] ProgramExtensions = [".dll", ".exe"];

    /// <summary>The names (as stored in the zip) of every program file in the Silo at <paramref name="siloPath" />; empty for anything else, or on any read error.</summary>
    public static IReadOnlyList<string> FindProgramFiles(string siloPath)
    {
        try
        {
            using var fs = new FileStream(siloPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            // zip files start with "PK"; a Simple-format Silo starts with "SQLite format 3"
            if (fs.ReadByte() != 'P' || fs.ReadByte() != 'K')
                return [];

            fs.Position = 0;
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, true);
            return zip.Entries
                      .Where(e => e.Name.Length > 0 && ProgramExtensions.Any(x => e.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                      .Select(e => e.FullName)
                      .ToList();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not scan '{Path}' for program files", siloPath);
            return [];
        }
    }

    /// <summary>The text of the warning for <paramref name="names" /> (never empty), listing up to a handful of them.</summary>
    public static string WarningText(IReadOnlyList<string> names)
    {
        const int shown = 8;
        var list = string.Join("\n", names.Take(shown).Select(n => "    " + n));
        if (names.Count > shown) list += $"\n    ... and {names.Count - shown} more";

        return $"This Silo contains {names.Count} program file{(names.Count == 1 ? "" : "s")}:\n\n{list}\n\n" +
               "This version of Silo does not load or run code from Silo files, so " + (names.Count == 1 ? "it" : "they") +
               " will be ignored. They are kept in the file, and the rest of the Silo opens normally.";
    }
}
