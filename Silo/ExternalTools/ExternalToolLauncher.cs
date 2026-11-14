using System.Collections.Concurrent;
using Serilog;
using Silo.Commanding;

namespace Silo.ExternalTools;

/// <summary>
///     Does what an <see cref="ExternalToolRun" /> says, through the shell (<see cref="IShellExecute" />). Terminal commands
///     open in Windows Terminal when it is installed, else in a console window; either stays open when the command is done.
/// </summary>
public static class ExternalToolLauncher
{
    private static readonly TimeSpan PathCacheLife = TimeSpan.FromMinutes(1);

    // program -> whether it was on the PATH, and when that was looked at (menus ask often; installing a tool is rare)
    private static readonly ConcurrentDictionary<string, (bool Found, DateTime At)> PathCache = new(StringComparer.OrdinalIgnoreCase);

    private static IShellExecute Shell => App.Require<IShellExecute>();

    /// <summary>Runs <paramref name="tool" /> for <paramref name="target" />; a failure is shown to the person.</summary>
    public static async Task LaunchAsync(ExternalTool tool, object target)
    {
        ExternalToolRun run;
        try
        {
            run = tool.Run(target);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "External tool {tool} failed for {target}", tool.Id, target);
            App.Alert(ex, $"'{tool.Header.Replace("_", "")}' could not be run for {target}");
            return;
        }

        await LaunchAsync(run);
    }

    /// <summary>Does what <paramref name="run" /> says.</summary>
    public static async Task LaunchAsync(ExternalToolRun run)
    {
        switch (run)
        {
            case ExternalToolRun.Terminal t:
                await InTerminal(t.Title, "cmd.exe", "/K " + t.CommandLine);
                break;

            case ExternalToolRun.PowerShell { Elevated: true } p:
                await Shell.ShellExecute("powershell.exe", "runas", PowerShellArguments(p.Script));
                break;

            case ExternalToolRun.PowerShell p:
                await InTerminal(p.Title, "powershell.exe", PowerShellArguments(p.Script));
                break;

            case ExternalToolRun.Browser b:
                await Shell.ShellExecute(b.Url.AbsoluteUri);
                break;

            case ExternalToolRun.Program p:
                await Shell.Start(p.Path, p.Arguments);
                break;

            case ExternalToolRun.InApp a:
                try
                {
                    await a.Work();
                }
                catch (OperationCanceledException)
                {
                    Log.Debug("Cancelled: {work}", a.Description);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "External tool work failed: {work}", a.Description);
                    App.Alert(ex, $"Could not {a.Description}");
                }

                break;
        }
    }

    /// <summary>
    ///     The arguments that run <paramref name="script" /> in a PowerShell that stays open. The script is passed encoded, so
    ///     no quoting of the command line (or of Windows Terminal's, which splits at ';') can change it.
    /// </summary>
    public static string PowerShellArguments(string script)
    {
        return "-NoExit -NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    /// <summary>Whether <paramref name="program" /> is found in a folder on the PATH (looked at again after a minute).</summary>
    public static bool OnPath(string program)
    {
        var now = DateTime.UtcNow;
        if (PathCache.TryGetValue(program, out var seen) && now - seen.At < PathCacheLife) return seen.Found;

        bool found = OpenTerminalFileCommand.OnPath(program);
        PathCache[program] = (found, now);
        return found;
    }

    private static Task InTerminal(string title, string program, string arguments)
    {
        return OnPath("wt.exe")
                   ? Shell.Start("wt.exe", $"new-tab --title \"{title.Replace("\"", "'")}\" {program} {arguments}")
                   : Shell.Start(program, arguments);
    }
}
