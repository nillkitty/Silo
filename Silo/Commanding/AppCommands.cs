using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Serilog;
using Silo.Hosting;
using Silo.Model;
using Silo.Ui;
using Silo.Ui.Windows;
using System.Windows.Threading;

namespace Silo.Commanding;

/// <summary>
///     File -> Open Silo
/// </summary>
public record OpenSiloCommand() : SiloCommand("_Open Silo...", Enabler.Always)
{
    protected override async Task OnExecute(object? parameter)
    {
        string? path = parameter as string;
        if (path is null)
        {
            var dlg = new OpenFileDialog
                      {
                          Title       = "Open Silo",
                          Filter      = "Silo files (*.silo)|*.silo|All files (*.*)|*.*",
                          Multiselect = false
                      };
            if (dlg.ShowDialog() != true)
            {
                App.Status("User cancelled out of Open Silo dialog");
                return;
            }

            path = dlg.FileName;
        }

        await OpenPathAsync(path);
    }

    /// <summary>
    ///     Opens a .silo file or URL and makes it the active Silo -- in this process, or in a new one when it is a Solo
    ///     Silo or a URL (see <see cref="SiloRouting" />). Reports problems to the person instead of throwing. Returns null
    ///     when nothing was opened here. The status bar says "Opening ..." for as long as it takes (see <see cref="AppStatus" />).
    /// </summary>
    /// <param name="path">The file or URL.</param>
    /// <param name="operation">
    ///     The status-bar operation that already tells the person this is going to happen (startup begins one before the window is
    ///     up); this call ends it. A new one is begun when omitted.
    /// </param>
    public static async Task<OpenSilo?> OpenPathAsync(string path, StatusOperation? operation = null)
    {
        var status = operation ?? AppStatus.Instance.Begin("Opening " + Describe(path));
        var clock  = Stopwatch.StartNew();
        try
        {
            // The opening itself holds the UI thread for a while: let the status line show what is happening before it starts.
            if (Application.Current?.Dispatcher is { } dispatcher && dispatcher.CheckAccess())
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);

            var silo = await SiloRouting.OpenAsync(path);
            if (silo is null) status.Dispose(); // handed to another process, or cancelled: those said so themselves
            else status.Done($"Opened {silo.Name ?? Describe(path)} in {clock.ElapsedMilliseconds} ms");
            return silo;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Opening '{path}' failed", path);
            status.Fail($"The Silo could not be opened: {ex.Message}");
            MessageBox.Show($"The Silo could not be opened:\n{path}\n\n{ex.Message}", AppIdentity.AppName, MessageBoxButton.OK,
                            MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>What to call a file or URL in the status bar: the file's name, or the whole address.</summary>
    private static string Describe(string path)
    {
        string trimmed = path.Trim().Trim('"');
        if (trimmed.Contains("://")) return trimmed;

        string name = Path.GetFileName(trimmed.TrimEnd('/', '\\'));
        return name.Length > 0 ? name : trimmed;
    }
}

/// <summary>
///     File -> Open from URL
/// </summary>
public record OpenSiloFromUrlCommand() : SiloCommand("Open from _URL...", Enabler.Always)
{
    protected override async Task OnExecute(object? parameter)
    {
        var url = parameter as string ?? PromptDialog.Ask("Open Silo from URL", "Address of the Silo (an https:// link to a .silo file):", "Open");
        if (url is null)
        {
            App.Status("User cancelled out of Open from URL");
            return;
        }

        await OpenSiloCommand.OpenPathAsync(url);
    }
}

/// <summary>
///     File -> New Silo
/// </summary>
public record NewSiloCommand() : SiloCommand("_New Silo...", Enabler.Always)
{
    protected override async Task OnExecute(object? parameter)
    {
        var ui = App.Require<INewSiloDialog>();

        var r = ui.ShowDialog();
        if (r.Cancel)
        {
            App.Status("User canceled out of New Silo dialog");
            return;
        }

        // Creating a Silo replaces whatever file is at the path, so a Silo that is open there must not be silently clobbered.
        string full = Path.GetFullPath(r.FilePath);
        if (OpenSilo.OpenSilos.Any(o => string.Equals(o.FilePath, full, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("A Silo with that file name is already open. Close it first or choose another name.", AppIdentity.AppName,
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (File.Exists(full) && MessageBox.Show($"{full} already exists. Replace it with a new, empty Silo?", AppIdentity.AppName,
                                                 MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var silo = OpenSilo.Create(full, r.Template);
            App.Status($"Created {silo.Name}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Creating '{path}' failed", full);
            MessageBox.Show($"The Silo could not be created:\n{ex.Message}", AppIdentity.AppName, MessageBoxButton.OK,
                            MessageBoxImage.Error);
        }
    }
}

/// <summary>
///     File -> Exit
/// </summary>
public record AppExitCommand() : SiloCommand("E_xit", Enabler.Always)
{
    protected override async Task OnExecute(object? parameter)
    {
        if (App.Instance is not { } app)
        {
            Environment.Exit(0);
            return;
        }

        // Offers to save every Silo with unsaved changes; cancelling any of them keeps the app open.
        if (!await app.CloseAllSilosAsync())
        {
            App.Status("Exit cancelled");
            return;
        }

        app.Shutdown(0);
    }
}