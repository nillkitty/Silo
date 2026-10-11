using System.IO;
using Silo.DbModel.Sqlite;
using Silo.Model;
using Silo.Remote;

namespace Silo.Hosting;

/// <summary>
///     Decides, for a document the person opens from inside a running Silo (File &gt; Open, drag and drop, "Open from URL"),
///     whether it opens in this process or in a new one -- the lower half of the startup flowchart:
///     <list type="bullet">
///         <item>Stock instance: a normal Silo opens here; a Solo Silo or a URL launches a new Silo process.</item>
///         <item>Solo / Remote process (single-document): opens here only while it has no document; otherwise launches a new process.</item>
///     </list>
/// </summary>
public static class SiloRouting
{
    /// <summary>
    ///     Opens a path or URL. Returns the opened Silo, or null when it was handed to another process (or the person
    ///     cancelled). Throws when the Silo could not be opened.
    /// </summary>
    public static async Task<OpenSilo?> OpenAsync(string target)
    {
        target = target.Required().Trim().Trim('"');
        var app = App.Instance!;

        if (SiloLocation.TryParseRemote(target, out var uri))
        {
            if (SiloLocation.Classify(target) == SiloLocationKind.Git)
                throw new NotSupportedException(GitSiloRemote.NotImplementedMessage);

            // A URL always gets a process of its own, unless this is a single-document process with nothing open yet.
            if (app.Launch.Mode == LaunchMode.Stock || OpenSilo.OpenSilos.Count > 0)
                return Handoff(target, false);

            return await OpenSilo.OpenRemoteAsync(uri);
        }

        var full = Path.GetFullPath(target);
        if (OpenSilo.OpenSilos.Any(o => string.Equals(o.FilePath, full, StringComparison.OrdinalIgnoreCase)))
            return OpenSilo.OpenFile(new SiloFile(full)); // already open here: just activate it

        var solo = SiloProbe.Probe(full).IsSolo;
        var here = app.Launch.Mode == LaunchMode.Stock ? !solo : OpenSilo.OpenSilos.Count == 0;
        if (!here) return Handoff(full, solo);

        return OpenSilo.OpenFile(new SiloFile(full));
    }

    private static OpenSilo? Handoff(string target, bool solo)
    {
        if (!ProcessLauncher.Launch(target, solo))
            throw new InvalidOperationException("A new Silo process could not be started.");

        App.Status(solo ? "Opened in its own window (Solo Silo)" : "Opened in a new window");
        return null;
    }
}
