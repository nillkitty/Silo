using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using Telefrag.DI;

namespace Silo.Commanding;

/// <summary>
///     The shell, through <c>ShellExecute</c> (what <see cref="Process.Start(ProcessStartInfo)" /> calls with
///     <c>UseShellExecute</c>). The work is done on the calling thread -- it is quick, and the shell's verbs (Open with,
///     Properties) expect to be called from the UI thread -- and every failure is shown with <see cref="App.Alert(string, string?)" />.
/// </summary>
/// <remarks>
///     The class is not called <c>ShellExecute</c> like its methods: C# does not let a class have a member with its own name.
/// </remarks>
[Singleton(typeof(IShellExecute))]
public class ShellExecutor : IShellExecute
{
    /// <summary>The Win32 error for "the operation was canceled by the user" (the UAC prompt, say).</summary>
    private const int ErrorCancelled = 1223;

    public Task ShellExecute(string path)
    {
        return Run(path, new ProcessStartInfo(path) { UseShellExecute = true }, $"open {path}");
    }

    public Task ShellExecute(string path, string verb, string? arguments = null)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = true, Verb = verb };
        if (!string.IsNullOrEmpty(arguments)) start.Arguments = arguments;
        return Run(path, start, $"{verb} {path}");
    }

    public Task ShellExecute(Guid shellObject)
    {
        // a known folder has a path; opening that is what the person expects. Anything else is a shell namespace object.
        if (KnownFolderPath(shellObject) is { Length: > 0 } folder)
            return ShellExecute(folder);

        string target = ShellNamespaceTarget(shellObject);
        return Run(target, new ProcessStartInfo("explorer.exe", target) { UseShellExecute = false }, $"open {target}");
    }

    public Task Reveal(string path)
    {
        var start = RevealStart(path);
        if (start is null)
        {
            App.Alert($"Cannot show '{path}' in Windows Explorer: neither it nor its folder exists.");
            return Task.CompletedTask;
        }

        return Run(path, start, $"show {path} in Explorer");
    }

    public Task Start(string program, string? arguments = null, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        if (!string.IsNullOrEmpty(arguments)) start.Arguments = arguments;
        if (!string.IsNullOrEmpty(workingDirectory)) start.WorkingDirectory = workingDirectory;
        return Run(program, start, $"start {program}");
    }

    /// <summary>The <c>shell:::{id}</c> text Explorer opens a shell namespace object with.</summary>
    public static string ShellNamespaceTarget(Guid shellObject)
    {
        return "shell:::" + shellObject.ToString("B").ToUpperInvariant();
    }

    /// <summary>
    ///     How to show <paramref name="path" /> in Explorer: an existing folder opens; an existing file is selected in its folder
    ///     (<c>explorer.exe /select,"path"</c>); something that is not there falls back to the nearest folder above it that
    ///     is. Null when there is no such folder.
    /// </summary>
    public static ProcessStartInfo? RevealStart(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (Directory.Exists(path))
            return new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = false };

        if (global::System.IO.File.Exists(path))
            return new ProcessStartInfo("explorer.exe", "/select," + Quote(path)) { UseShellExecute = false };

        for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent))
                return new ProcessStartInfo("explorer.exe", Quote(parent)) { UseShellExecute = false };

        return null;
    }

    /// <summary>Quotes a path for a command line; a trailing backslash (a drive root) is doubled so it does not escape the closing quote.</summary>
    private static string Quote(string path) => path.EndsWith('\\') ? $"\"{path}\\\"" : $"\"{path}\"";

    private static Task Run(string what, ProcessStartInfo start, string description)
    {
        try
        {
            using var process = Process.Start(start);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Debug("The person cancelled: {action}", description);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Shell: {action} failed", description);
            App.Alert($"Could not {description}.\n\n{ex.Message}", "Shell");
        }

        return Task.CompletedTask;
    }

    // ---- known folders ---------------------------------------------------------------------------

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token,
                                                   [MarshalAs(UnmanagedType.LPWStr)] out string path);

    /// <summary>The folder a known-folder id (<c>FOLDERID_Downloads</c>...) stands for, or null if it is not one (or this is not Windows).</summary>
    private static string? KnownFolderPath(Guid id)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            return SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var path) == 0 ? path : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
