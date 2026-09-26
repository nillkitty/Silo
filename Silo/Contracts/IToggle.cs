namespace Silo.Contracts;

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