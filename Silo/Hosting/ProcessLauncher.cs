using System.Diagnostics;
using System.IO;
using Serilog;

namespace Silo.Hosting;

/// <summary>Starts another Silo process for a document (the "Launch new Silo process, pass as arg" box of the startup flow).</summary>
public static class ProcessLauncher
{
    /// <summary>
    ///     Starts a new Silo process on <paramref name="target" /> (a path or URL). It goes through the normal startup
    ///     flow: a Solo Silo or URL becomes its own process; a normal Silo reaches the stock instance.
    /// </summary>
    /// <param name="forceSolo">Adds <c>/P</c>: the new process keeps the document to itself even if it is a normal Silo.</param>
    public static bool Launch(string target, bool forceSolo = false)
    {
        try
        {
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("The path of this program is unknown.");
            if (self.EndsWith(".com", StringComparison.OrdinalIgnoreCase)) self = Path.ChangeExtension(self, ".exe");

            var info = new ProcessStartInfo(self) { UseShellExecute = false };
            if (forceSolo) info.ArgumentList.Add("/P");
            info.ArgumentList.Add(target);

            Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not start a new Silo process for '{Target}'", target);
            return false;
        }
    }
}
