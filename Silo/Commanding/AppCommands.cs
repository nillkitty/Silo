using System.IO;
using Microsoft.Win32;
using Serilog;
using Silo.Model;

namespace Silo.Commanding;

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

    /// <summary>Opens a .silo file and makes it the active Silo, reporting problems to the person instead of throwing.</summary>
    public static Task<OpenSilo?> OpenPathAsync(string path)
    {
        try
        {
            return Task.FromResult<OpenSilo?>(OpenSilo.OpenFile(new SiloFile(path)));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Opening '{path}' failed", path);
            MessageBox.Show($"The Silo could not be opened:\n{path}\n\n{ex.Message}", AppIdentity.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            return Task.FromResult<OpenSilo?>(null);
        }
    }
}

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

        if (File.Exists(full) &&
            MessageBox.Show($"{full} already exists. Replace it with a new, empty Silo?", AppIdentity.AppName,
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
            MessageBox.Show($"The Silo could not be created:\n{ex.Message}", AppIdentity.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

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
