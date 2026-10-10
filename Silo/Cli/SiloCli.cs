using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Silo.Cli;

/// <summary>
///     Command mode: what <c>silo &lt;command&gt; ...</c> does at a command prompt. Runs with no WPF and no
///     <see cref="App" />; standard input and output are the process's own, so it pipes like any console tool.
///     Exit codes: 0 success, 1 failure, 2 usage error.
/// </summary>
/// <remarks>
///     This is the extension point for bulk processing and automation: add a verb to <see cref="Commands" />.
/// </remarks>
internal static class SiloCli
{
    /// <summary>The commands, by name. A command gets the arguments after its own name and returns an exit code.</summary>
    private static readonly Dictionary<string, (string Summary, Func<string[], int> Run)> Commands =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["help"]    = ("Show this help.", _ => Help()),
            ["version"] = ("Show the Silo version.", _ => Version())
        };

    public static int Run(string[] args)
    {
        if (args.Length == 0) return Help();

        string verb = args[0];
        if (verb is "-h" or "--help" or "/?" or "-?") return Help();
        if (verb is "-v" or "--version") return Version();

        if (Commands.TryGetValue(verb, out var command)) return command.Run(args[1..]);

        Console.Error.WriteLine($"silo: unknown command '{verb}'. Run 'silo help' for the list.");
        return 2;
    }

    private static int Help()
    {
        Console.WriteLine("Usage: silo [command] [arguments]");
        Console.WriteLine();
        Console.WriteLine("Run with no command to start the Silo app.");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        foreach (var (name, (summary, _)) in Commands.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {name,-12}{summary}");

        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --gui       Start the app even when run as a command (silo --gui [file]).");
        Console.WriteLine("  --cli       With silo.exe, run a command instead of starting the app.");
        return 0;
    }

    private static int Version()
    {
        var asm  = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "unknown";

        Console.WriteLine($"Silo {info}");
        return 0;
    }
}
