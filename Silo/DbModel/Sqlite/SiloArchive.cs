using System.IO;
using System.IO.Compression;

namespace Silo.DbModel.Sqlite;

/// <summary>
/// Expands a <c>.silo</c> file (a zip archive of SQLite database files, per the layout
/// documented in README.md) into a working temp directory, and re-compresses that directory
/// back into a <c>.silo</c> file.
/// </summary>
/// <remarks>
/// Encrypted Silos -- a nested 'encrypted' inner zip wrapping a stub <c>z.db</c> in an outer
/// unencrypted zip -- are out of scope for this pass; only the flat, unencrypted layout is
/// handled here.
/// </remarks>
internal static class SiloArchive
{
    /// <summary>
    /// Creates a fresh, empty working directory for a Silo to be extracted into or built in.
    /// </summary>
    public static string CreateWorkingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SiloWorking", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Expands <paramref name="siloPath"/> into <paramref name="workingDirectory"/>.
    /// </summary>
    public static void Extract(string siloPath, string workingDirectory)
    {
        ZipFile.ExtractToDirectory(siloPath, workingDirectory, overwriteFiles: true);
    }

    /// <summary>
    /// Compresses every file currently in <paramref name="workingDirectory"/> into a zip
    /// archive written to <paramref name="siloPath"/>, replacing whatever was there. The write
    /// goes through a temp file and an atomic move so a failure partway through never leaves a
    /// corrupt/truncated .silo file behind.
    /// </summary>
    public static async Task RepackAsync(string workingDirectory, string siloPath)
    {
        await Task.Run(() =>
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(siloPath));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = siloPath + ".tmp";
            if (System.IO.File.Exists(tmpPath))
                System.IO.File.Delete(tmpPath);

            using (var archive = ZipFile.Open(tmpPath, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(workingDirectory, "*", SearchOption.AllDirectories))
                {
                    var entryName = Path.GetRelativePath(workingDirectory, file).Replace('\\', '/');
                    archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                }
            }

            if (System.IO.File.Exists(siloPath))
                System.IO.File.Delete(siloPath);
            System.IO.File.Move(tmpPath, siloPath);
        });
    }
}
