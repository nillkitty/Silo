using System.IO;
using System.Runtime.InteropServices;
using Silo.Connectors.File;

namespace Silo.Commanding;

/// <summary>
///     A command on one file system item (<see cref="Model" />): the Node menu, right-click menu and toolbar entries that
///     <see cref="Silo.Providers.Action.FileActions" /> offers. Each carries its item, so it can be run without a parameter;
///     a <see cref="FileModel" /> given as the parameter overrides it.
/// </summary>
/// <param name="Model">The item the command is for.</param>
/// <param name="Header">Menu-style text (an underscore marks an access key).</param>
/// <param name="When">Decides, from the item, whether the command can run now; null: always.</param>
/// <param name="ToolTip">What the command does.</param>
/// <param name="Icon">The key of its icon (see Resources/NavIcons.xaml).</param>
public abstract record FileCommand(
    FileModel              Model,
    string                 Header,
    Func<FileModel, bool>? When    = null,
    string?                ToolTip = null,
    object?                Icon    = null) : AppCommand(Header, null, EnableWhen(Model, When), ToolTip, Icon)
{
    /// <summary>The item to act on: the one passed as the parameter, else this command's own.</summary>
    protected FileModel Target(object? parameter) => parameter as FileModel ?? Model;

    /// <summary>The shell, as an app service.</summary>
    protected static IShellExecute Shell => App.Require<IShellExecute>();

    private static Enabler.EnablerFunc? EnableWhen(FileModel model, Func<FileModel, bool>? when)
    {
        return when is null ? null : p => when(p as FileModel ?? model);
    }

    /// <summary>Puts text on the clipboard and says so in the status bar; tells the person if another program has the clipboard locked.</summary>
    protected static void CopyToClipboard(string text, string what)
    {
        try
        {
            Clipboard.SetText(text);
            App.Status($"Copied the {what}");
        }
        catch (COMException ex)
        {
            App.Alert(ex, $"Could not copy the {what}: the clipboard is in use by another program.");
        }
    }
}

/// <summary>Open in Default App: opens the file with the program Windows has for its type.</summary>
public record LaunchFileCommand(FileModel Model)
    : FileCommand(Model, "_Open in Default App", m => m.Kind != FileKind.Missing && !m.IsContainer,
                  "Opens the file in the default app registered for the file's type", "Launch")
{
    protected override Task OnExecute(object? parameter)
    {
        var f = Target(parameter);
        if (global::System.IO.File.Exists(f.Path)) return Shell.ShellExecute(f.Path);

        App.Alert($"File '{f.Path}' does not exist.");
        return Task.CompletedTask;
    }
}

/// <summary>Open with...: the shell's "Open with" chooser for the file.</summary>
public record OpenWithFileCommand(FileModel Model)
    : FileCommand(Model, "Open _with...", m => m.Kind != FileKind.Missing && !m.IsContainer,
                  "Choose which program to open the file with", "TbOpen")
{
    protected override Task OnExecute(object? parameter)
    {
        var f = Target(parameter);
        if (global::System.IO.File.Exists(f.Path)) return Shell.ShellExecute(f.Path, "openas");

        App.Alert($"File '{f.Path}' does not exist.");
        return Task.CompletedTask;
    }
}

/// <summary>Reveal in Explorer: shows the item in a Windows Explorer window (a folder is opened, a file selected in its folder).</summary>
public record RevealFileCommand(FileModel Model)
    : FileCommand(Model, "_Reveal in Explorer", null, "Shows the item in a Windows Explorer window", "Folder17")
{
    protected override Task OnExecute(object? parameter) => Shell.Reveal(Target(parameter).Path);
}

/// <summary>Reveal Destination in Explorer: shows what a link points to.</summary>
public record RevealLinkTargetCommand(FileModel Model)
    : FileCommand(Model, "_Reveal Destination in Explorer", m => m.LinkTarget is { Length: > 0 },
                  "Shows the item the link points to in a Windows Explorer window", "Folder17")
{
    protected override Task OnExecute(object? parameter)
    {
        return Target(parameter).LinkTarget is { Length: > 0 } target ? Shell.Reveal(target) : Task.CompletedTask;
    }
}

/// <summary>Copy as Path.</summary>
public record CopyPathFileCommand(FileModel Model)
    : FileCommand(Model, "Copy as _Path", null, "Puts the full path on the clipboard", "TbCopy")
{
    protected override Task OnExecute(object? parameter)
    {
        CopyToClipboard(Target(parameter).Path, "path");
        return Task.CompletedTask;
    }
}

/// <summary>Copy as URI.</summary>
public record CopyUriFileCommand(FileModel Model)
    : FileCommand(Model, "Copy as _URI", null, "Puts the location on the clipboard as a file:// URI", "TbCopy")
{
    protected override Task OnExecute(object? parameter)
    {
        var f = Target(parameter);
        CopyToClipboard(f.Uri?.AbsoluteUri ?? f.Path, "URI");
        return Task.CompletedTask;
    }
}

/// <summary>Refresh Contents (a folder) / Refresh Metadata (a file): reads the file system again.</summary>
public record RefreshFileCommand(FileModel Model, bool Contents)
    : FileCommand(Model, Contents ? "_Refresh Contents" : "_Refresh Metadata", null,
                  Contents ? "Reads the folder's contents again" : "Reads the item's details again", "Refresh")
{
    protected override Task OnExecute(object? parameter) => Target(parameter).RefreshAsync();
}

/// <summary>Open External Terminal Here: a terminal window (Windows Terminal if it is installed, else a command prompt) in the folder.</summary>
public record OpenTerminalFileCommand(FileModel Model)
    : FileCommand(Model, "Open E_xternal Terminal Here", m => m.Kind != FileKind.Missing,
                  "Opens a terminal window in this folder (the item's folder, for a file)", "Terminal")
{
    protected override Task OnExecute(object? parameter)
    {
        var    f   = Target(parameter);
        string dir = f.IsContainer ? f.Path : Path.GetDirectoryName(f.Path) ?? f.Path;

        return OnPath("wt.exe") ? Shell.Start("wt.exe", $"-d \"{dir.TrimEnd('\\')}\"", dir) : Shell.Start("cmd.exe", "/K", dir);
    }

    /// <summary>Whether a program is found in a folder on the PATH (Windows Terminal is an app-execution alias there).</summary>
    private static bool OnPath(string program)
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return folders.Any(d =>
                           {
                               try
                               {
                                   return global::System.IO.File.Exists(Path.Combine(d.Trim(), program));
                               }
                               catch (ArgumentException)
                               {
                                   return false;
                               }
                           });
    }
}

/// <summary>
///     A menu item for something Silo is going to do for a file system item but cannot yet: always disabled, its tool tip says what it
///     will do. Swap it for a real command in <see cref="Silo.Providers.Action.FileActions" /> when the feature exists.
/// </summary>
/// <param name="Model">The item the command is for.</param>
/// <param name="Header">Menu-style text.</param>
/// <param name="Plan">What the command will do, in a sentence.</param>
/// <param name="Icon">The key of its icon, if it has one.</param>
public record PlannedFileCommand(FileModel Model, string Header, string Plan, object? Icon = null)
    : FileCommand(Model, Header, _ => false, "Not available yet: " + Plan, Icon)
{
    protected override Task OnExecute(object? parameter)
    {
        App.Alert($"'{Header.Replace("_", "").TrimEnd('.')}' is not available yet: {Plan}");
        return Task.CompletedTask;
    }
}
