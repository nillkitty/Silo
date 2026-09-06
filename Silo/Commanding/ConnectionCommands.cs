using System;
using System.Collections.Generic;
using System.Text;
using Silo.Model;

namespace Silo.Commanding;

public record NewConnectionCommand(OpenSilo Silo)
    : SiloCommand("New _Connection...", Enabler.IsSiloAuthorable)
{
    protected override Task OnExecute(object? parameter)
    {
        throw new NotImplementedException();
    }
}

public record NewCredentialCommand(OpenSilo Silo)
    : SiloCommand("New Cre_dential...", Enabler.IsSiloAuthorable)
{
    protected override Task OnExecute(object? parameter)
    {
        throw new NotImplementedException();
    }
}