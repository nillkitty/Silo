using System.Diagnostics;
using System.IO;
using Silo.Cli;

namespace Silo;

/// <summary>
///     The one entry point behind both launchers: <c>silo.exe</c> (the GUI-subsystem stub) and
///     <c>silo.com</c> (a console-subsystem copy of the same stub, made at build time -- see
///     <c>tools/PostBuild.ps1</c>). Both start this assembly; they differ only in whether Windows gives
///     the process a console, so cmd and PowerShell wait for it and can pipe its input and output.
/// </summary>
/// <remarks>
///     Windows tries <c>.com</c> before <c>.exe</c> when you type <c>silo</c>, so a command prompt gets the
///     console copy while Explorer, shortcuts and file associations (which name <c>silo.exe</c>) get the
///     GUI. The rules:
///     <list type="bullet">
///         <item><c>silo.exe ...</c> starts the app (any arguments, such as a file to open, go to it). Add <c>--cli</c> first to run a command instead.</item>
///         <item><c>silo ...</c> (the console copy) runs a command and returns its exit code.</item>
///         <item><c>silo</c> with no arguments, or <c>silo --gui ...</c>, starts <c>silo.exe</c> detached and returns, like <c>devenv</c>.</item>
///     </list>
///     Command mode never touches <see cref="App" />, so no WPF is initialised.
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? self        = Environment.ProcessPath;
        bool    consoleStub = self is not null && self.EndsWith(".com", StringComparison.OrdinalIgnoreCase);

        if (args.Length > 0 && args[0].Equals("--cli", StringComparison.OrdinalIgnoreCase))
            return SiloCli.Run(args[1..]);

        if (consoleStub)
        {
            if (args.Length == 0) return LaunchGui(self!, []);
            if (args[0].Equals("--gui", StringComparison.OrdinalIgnoreCase)) return LaunchGui(self!, args[1..]);

            return SiloCli.Run(args);
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>Starts the GUI copy next to the console one, fully detached so a pipeline or script is not held open by it.</summary>
    private static int LaunchGui(string self, string[] args)
    {
        string gui = Path.ChangeExtension(self, ".exe");
        if (!File.Exists(gui))
        {
            Console.Error.WriteLine($"silo: cannot find '{gui}'.");
            return 1;
        }

        var info = new ProcessStartInfo(gui) { UseShellExecute = true };
        foreach (string a in args) info.ArgumentList.Add(a);

        Process.Start(info);
        return 0;
    }
}
