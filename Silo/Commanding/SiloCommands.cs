using System.Windows;
using System.Windows.Controls;
using Microsoft.AspNetCore.Mvc.Diagnostics;
using Serilog;
using Silo.Extensions;
using Silo.Model;
using Silo.Ui;
using Telerik.Windows.Controls;

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

public record SaveSiloCommand(OpenSilo Silo)
    : SiloCommand("_Save", Enabler.IsSiloLoaded)
{
    protected override async Task OnExecute(object? parameter)
    {
        var s = Silo.Required();

        if (s.FileInfo?.Exists is true)
        {
            await s.Data.SaveAsync();
            s.Debug("Silo saved successfully to {path}", Silo.FilePath);
            return;
        }

        (new SaveSiloAsCommand(Silo)).Execute(parameter);
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

public record SaveSiloAsCommand(OpenSilo Silo)
    : SiloCommand("Save _As...", Enabler.IsSiloLoaded)
{
    protected override async Task OnExecute(object? parameter)
    {
        var s = Silo.Required()!;
        var sfd = new RadSaveFileDialog()
                  {
                      DefaultExt = ".silo",
                      Filter =
                          "Silo files (*.silo)|*.silo",
                      FilterIndex = 0,
                      Header      = "Save Silo file as...",
                      FileName    = s.FilePath
                  };
        if (sfd.ShowDialog() is false || sfd.FileName is null)
        {
            s.Debug("User cancelled out of Save As dialog.");
            return;
        }

        var path = sfd.FileName;
        await s.Data.MigrateToCopyAsync(path);
        s.Debug("Migrated silo to {path}", path);
    }
}