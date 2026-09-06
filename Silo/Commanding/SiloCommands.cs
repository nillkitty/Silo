using System.Windows;
using System.Windows.Controls;
using Microsoft.AspNetCore.Mvc.Diagnostics;
using Serilog;
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

public record SaveSiloCommand(OpenSilo Silo)
    : SiloCommand("_Save", Enabler.IsSiloLoaded)
{
    protected override Task OnExecute(object? parameter)
    {
        throw new NotImplementedException();
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
    protected override Task OnExecute(object? parameter)
    {
        throw new NotImplementedException();
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
    protected override Task OnExecute(object? parameter)
    {
        throw new NotImplementedException();
    }
}