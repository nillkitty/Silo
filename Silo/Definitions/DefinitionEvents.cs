namespace Silo.Definitions;

/// <summary>Raised when an enum or a syntax of a Silo was added, changed or deleted from the screens, so the lists and the Explorer can refresh.</summary>
public static class DefinitionEvents
{
    public static event EventHandler? Changed;

    public static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);
}
