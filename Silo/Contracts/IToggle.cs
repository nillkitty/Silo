using System;
using System.Collections.Generic;
using System.Text;

namespace Silo.Contracts;

public interface IToggle
{
    string  Header      { get; }
    string? ToolTip     { get; }
    object? Icon        { get; }
    bool    IsCheckable { get; }
    bool    IsChecked   { get; set; }
    bool    IsEnabled   { get; }


    Task ToggleAsync(object? parameter);
}