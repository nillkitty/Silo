namespace Silo.Contracts.Core;

public interface IToggle
{
    string  Header      { get; }
    object? Icon        { get; }
    bool    IsCheckable { get; }
    bool    IsChecked   { get; set; }
    bool    IsEnabled   { get; }
    string? ToolTip     { get; }


    Task ToggleAsync(object? parameter);
}
