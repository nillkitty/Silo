using Silo.DbModel.Global;

namespace Silo.Contracts.Providers.Data;

/// <summary>One remembered window: where it was, how big, and for the main window how its panes were arranged.</summary>
/// <param name="Type">What kind of window ("Main", "Table", "Tool", "Dialog").</param>
/// <param name="Uri">Which one of that kind (<c>silo:main</c>, <c>silo:table/5</c>, <c>silo:tool/drawiocanvas</c>).</param>
/// <param name="Layout">The main window's dock layout (AvalonDock XML), else null.</param>
public sealed record WindowRecord(string Type, string Uri, int X, int Y, int Width, int Height, WindowPlacementState State, string? Layout = null);

/// <summary>
///     Remembers window positions, sizes and the dock layout in the Silo (the <c>Window</c> table), for the person who is
///     using it: what they arranged comes back the next time they open the Silo.
/// </summary>
/// <remarks>
///     A row belongs to the current user once that user is one of the Silo's Users; until then (a Silo nobody has been
///     added to) the rows are shared by everyone who opens it. Writing here does <b>not</b> mark the Silo as having unsaved
///     changes -- the arrangement is saved along with the next save for any other reason.
/// </remarks>
public interface IWindowStore
{
    /// <summary>The window as the current user left it, else as the Silo's shared default has it, else null.</summary>
    WindowRecord? GetWindow(string type, string uri);

    /// <summary>Remembers the windows (replacing what was remembered for the same type and uri).</summary>
    void SaveWindows(IReadOnlyCollection<WindowRecord> windows);

    /// <summary>Forgets everything remembered for the current user (the "reset window layout" command); returns how many windows.</summary>
    int ClearWindows();

    /// <summary>Forgets a window for everyone (its table was deleted).</summary>
    void ForgetWindow(string type, string uri);
}
