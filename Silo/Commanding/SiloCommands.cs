using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Serilog;
using Silo.Contracts.Providers.Data;
using Silo.Extensions;
using Silo.Model;
using Silo.Ui;

namespace Silo.Commanding;

public abstract record SiloCommand(
    string               Header,
    Enabler.EnablerFunc? ItemEnabler = null,
    Checker.CheckerFunc? Checker     = null,
    string?              ToolTip     = null,
    object?              Icon        = null,
    bool                 IsCheckable = false)
    : AppCommand(Header, null, null, ToolTip, Icon, IsCheckable, Checker)
{
}

/// <summary>
///     File -> Close Silo: closes the Silo, first offering to save unsaved changes (cancelling the offer keeps it open).
/// </summary>
public record CloseSiloCommand(OpenSilo Silo)
    : SiloCommand("_Close Silo", Enabler.IsSiloLoaded)
{
    protected override async Task OnExecute(object? parameter)
    {
        var    s    = Silo.Required();
        string name = s.Name ?? "Silo";

        App.Status(await s.CloseAsync() ? $"Closed {name}" : "Close cancelled");
    }
}

public record SaveSiloCommand(OpenSilo Silo)
    : SiloCommand("_Save", Enabler.IsSiloLoaded)
{
    protected override async Task OnExecute(object? parameter)
    {
        var s = Silo.Required();

        if (s.FileInfo?.Exists is not true)
        {
            // The file was deleted or moved since it was opened: ask where to put it.
            await new SaveSiloAsCommand(Silo).ExecuteAsync(parameter);
            return;
        }

        try
        {
            await s.SaveAsync();
            App.Status(s.IsUnsynced ? $"Saved {s.Name} on this computer only - not uploaded" : $"Saved {s.Name}");
        }
        catch (OperationCanceledException)
        {
            App.Status("Save cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving {path} failed", s.FilePath);
            MessageBox.Show($"The Silo could not be saved:\n{ex.Message}", AppIdentity.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public record ExportLogsCommand(OpenSilo Silo)
    : SiloCommand("Export _Logs...", Enabler.IsSiloLoaded)
{
    protected override Task OnExecute(object? parameter)
    {
        throw new NotImplementedException();
    }
}

public record ImportDataCommand(OpenSilo Silo)
    : SiloCommand("_Import Data...", Enabler.IsSiloLoaded)
{
    protected async override Task OnExecute(object? parameter)
    {
        var s = Silo.Required()!;
        var inFiles = Dialogs.OpenFiles("Select file(s) to import...",
                                        "All files (*.*)|*.*");
        if (inFiles is null or [])
        {
            s.Debug("User cancelled out of Import File(s) dialog");
            return;
        }

        int num = inFiles.Count;
        int ok  = 0;
        int ng  = 0;
        foreach (var fn in inFiles)
        {
            if (await s.Data.ImportFileAsync(fn))
                ok++;
            else
                ng++;
        }

        s.Debug("Finished processing {num} input files.  Success={ok} Failed={ng}",
                num, ok, ng);
    }
}

public record ExportAllCommand(OpenSilo Silo)
    : SiloCommand("Export _All...", Enabler.IsSiloLoaded)
{
    protected override Task OnExecute(object? parameter)
    {
        throw new NotImplementedException();
    }
}

public record ExportStateCommand(OpenSilo Silo)
    : SiloCommand("Export S_tate...", Enabler.IsSiloLoaded)
{
    protected override Task OnExecute(object? parameter)
    {
        throw new NotImplementedException();
    }
}

public record ConvertToArchiveCommand(OpenSilo Silo)
    : SiloCommand("Convert to _Archive Format", Enabler.IsSiloLoaded, ToolTip: "Splits a single-file Silo into the multi-file archive format")
{
    protected override async Task OnExecute(object? parameter)
    {
        var s = Silo.Required()!;
        if (s.Data is not ISiloFile { Format: SiloStorageFormat.Simple })
        {
            App.Status($"{s.Name} is already in the archive format");
            return;
        }

        try
        {
            await s.ConvertToArchiveAsync();
        }
        catch (OperationCanceledException)
        {
            App.Status("Conversion cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Converting {path} failed", s.FilePath);
            MessageBox.Show($"The Silo could not be converted:\n{ex.Message}", AppIdentity.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public record SaveSiloAsCommand(OpenSilo Silo)
    : SiloCommand("Save _As...", Enabler.IsSiloLoaded)
{
    public Task ExecuteAsync(object? parameter) => OnExecute(parameter);

    protected override async Task OnExecute(object? parameter)
    {
        var s = Silo.Required()!;
        var sfd = new SaveFileDialog()
                  {
                      DefaultExt = ".silo",
                      Filter =
                          "Silo files (*.silo)|*.silo",
                      FilterIndex = 1,
                      Title       = "Save Silo file as...",
                      FileName    = s.FilePath
                  };
        if (sfd.ShowDialog() != true || string.IsNullOrEmpty(sfd.FileName))
        {
            s.Debug("User cancelled out of Save As dialog.");
            return;
        }

        var path = sfd.FileName;
        try
        {
            await s.SaveAsAsync(path);
            App.Status($"Saved as {s.Name}");
        }
        catch (OperationCanceledException)
        {
            App.Status("Save cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving as {path} failed", path);
            MessageBox.Show($"The Silo could not be saved:\n{ex.Message}", AppIdentity.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
