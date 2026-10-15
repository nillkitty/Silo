namespace Silo.Drawio;

/// <summary>
///     Raised whenever a diagram of a Silo is created, saved, renamed or deleted, by whoever did it (the canvas tool, the
///     Explorer), so every list of diagrams -- the Explorer tree, the canvas's picker -- stays current.
/// </summary>
public static class DiagramEvents
{
    public static event EventHandler? Changed;

    public static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);
}
