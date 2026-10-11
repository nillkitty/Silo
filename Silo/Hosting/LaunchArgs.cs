using Silo.Remote;

namespace Silo.Hosting;

/// <summary>
///     The command line of a GUI start: at most one document (a path or a URL) and the <c>/P</c> switch.
///     <c>silo.exe [/P] [path-or-url]</c>
/// </summary>
/// <param name="Target">The path or URL to open, if any.</param>
/// <param name="IsUrl">True when <paramref name="Target" /> is a remote location.</param>
/// <param name="Solo"><c>/P</c> (also <c>-p</c>, <c>--solo</c>): open the document in its own process, even if it is a normal Silo.</param>
public sealed record LaunchArgs(string? Target, bool IsUrl, bool Solo)
{
    public static LaunchArgs Parse(string[] args)
    {
        string? target = null;
        var     solo   = false;

        foreach (var arg in args)
        {
            if (arg.Equals("/P", StringComparison.OrdinalIgnoreCase) || arg.Equals("-p", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--solo", StringComparison.OrdinalIgnoreCase))
            {
                solo = true;
                continue;
            }

            // Any other switch (-x, --gui, /x) is not a document.
            if (arg.StartsWith('-') || (arg.Length == 2 && arg[0] == '/')) continue;

            target ??= arg.Trim().Trim('"');
        }

        return new LaunchArgs(target, target is not null && SiloLocation.IsRemote(target), solo);
    }
}
