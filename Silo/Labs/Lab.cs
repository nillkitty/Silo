using System.ComponentModel;
using Silo.Commanding;

namespace Silo.Labs;

/// <summary>Which side of the main view an inspector is docked on by default.</summary>
public enum LabDock
{
    /// <summary>To the left of the main view.</summary>
    Left,

    /// <summary>To the right of the main view.</summary>
    Right,

    /// <summary>Below the main view (above the terminal).</summary>
    Bottom
}

/// <summary>
///     One menu a Lab adds to its window's menu bar (the window has the standard ones: File, Edit, Lab, Value, View). A menu whose header
///     is "_File" or "_Edit" is the Lab's own content for that menu (the window adds New Window and Close to File); the others are
///     added between Edit and Lab.
/// </summary>
/// <param name="Header">The menu's text ("_Rules"); a leading "_" makes the next letter the access key.</param>
/// <param name="Items">The entries in order; a null entry is a separator.</param>
public sealed record LabMenu(string Header, IReadOnlyList<ProvidedCommand?> Items);

/// <summary>
///     A "Lab": a UI harness for studying one kind of value (a web address, some bits, text, JSON, an XML schema...) in a window of its
///     own, apart from but complementary to the main Silo window. This is the Lab itself -- what it is called, what value it studies, its
///     parameters -- and the factory of its windows; what one window does is an <see cref="ILabSession" />.
/// </summary>
/// <remarks>
///     <para>
///         Labs are found through the container (<c>[Singleton(typeof(ILab))]</c>), so a Lab someone adds shows up in the Analysis menu
///         and in every node's "Open in ..." command without any other change. Most Labs derive from <see cref="LabBase{TValue}" />.
///     </para>
///     <para>
///         Silo opens a value in a Lab (<see cref="LabHost.Open" />, the standard "Open in Lab" command) and can ask a window what it is
///         studying now (<see cref="ILabSession.Value" />). Each opened value gets a window of its own.
///     </para>
/// </remarks>
[RequireRegistration]
public interface ILab
{
    /// <summary>A short stable key ("web"): what the Lab's saved parameters and window layout are filed under.</summary>
    string Id { get; }

    /// <summary>What the Lab is called ("Web Lab").</summary>
    string Name { get; }

    /// <summary>One sentence for tooltips: what the Lab is for.</summary>
    string Description { get; }

    /// <summary>The key of the Lab's icon (see <c>Resources/NavIcons.xaml</c>); null for none.</summary>
    string? IconKey { get; }

    /// <summary>Where the Lab is listed relative to the others (lower first).</summary>
    int Order { get; }

    /// <summary>The kind of value the Lab studies.</summary>
    Type ValueType { get; }

    /// <summary>
    ///     The Lab's parameters: the settings that shape how it works, kept in the Silo (the same for every window of the Lab).
    ///     Empty for a Lab that has none.
    /// </summary>
    LabParameterSet Parameters { get; }

    /// <summary>
    ///     Whether the Lab can study <paramref name="candidate" />, and if so the value as the Lab wants it. A Lab accepts the value type
    ///     itself and anything that obviously stands for one (text that reads as a web address, say). Must be quick: it is asked of every
    ///     node when a menu is built.
    /// </summary>
    bool TryAccept(object? candidate, out object? value);

    /// <summary>A new session: one window's worth of the Lab. Nothing is shown until the window is.</summary>
    ILabSession CreateSession();
}

/// <summary>What a Lab window offers its session: a way to bring up its own panes.</summary>
public interface ILabHost
{
    /// <summary>The window.</summary>
    Window Window { get; }

    /// <summary>Opens (or brings forward) the inspector with this <see cref="LabInspector.Id" />; "parameters" is the Lab's parameters.</summary>
    void ShowInspector(string id);

    /// <summary>Closes the window.</summary>
    void Close();
}

/// <summary>
///     One window of a Lab, studying one value. The window (see <c>LabWindow</c>) is the same for every Lab: it lays out the main view,
///     the inspectors, the menus, the toolbar and the terminal from what the session says; the session says what is specific to the kind of
///     value.
/// </summary>
public interface ILabSession : INotifyPropertyChanged, IDisposable
{
    /// <summary>The Lab this is a window of.</summary>
    ILab Lab { get; }

    /// <summary>The window title: the Lab and what it is studying.</summary>
    string Title { get; }

    /// <summary>A line of progress or outcome for the status bar ("Loaded", "Blocked a download").</summary>
    string Status { get; }

    /// <summary>The value being studied now (for the Web Lab, where the page is now); null when there is none yet.</summary>
    object? Value { get; }

    /// <summary>The value as text a person can edit and enter in the window's value box.</summary>
    string ValueText { get; }

    /// <summary>Raised when <see cref="Value" /> changed (the page navigated, the text was edited...).</summary>
    event EventHandler? ValueChanged;

    /// <summary>Raised when the context actions, or whether any action can run, may have changed.</summary>
    event EventHandler? ActionsChanged;

    /// <summary>Tells the session its window; called once, before <see cref="BuildMainView" />.</summary>
    void Attach(ILabHost host);

    /// <summary>Studies <paramref name="value" /> (anything <see cref="ILab.TryAccept" /> accepts); null for the Lab's default (a blank page, an empty text...).</summary>
    Task OpenAsync(object? value);

    /// <summary>Reads text typed in the window's value box. False when it is not a value of this Lab.</summary>
    bool TryParseValue(string text, out object? value);

    /// <summary>The Lab's main panel: a view of the value. Called once.</summary>
    UIElement BuildMainView();

    /// <summary>Actions that go to places or open things, whatever the value: Go Home, Open Settings. In the Lab menu.</summary>
    IEnumerable<ProvidedCommand> GlobalActions { get; }

    /// <summary>Actions that depend on what is happening now: Back, Cancel, Accept. Asked again when <see cref="ActionsChanged" /> is raised. On the toolbar.</summary>
    IEnumerable<ProvidedCommand> ContextActions { get; }

    /// <summary>Actions on the value being studied: Refresh, View Source, Add to Silo. In the Value menu and on the toolbar.</summary>
    IEnumerable<ProvidedCommand> ValueActions { get; }

    /// <summary>
    ///     The Lab's own menus (see <see cref="LabMenu" />). Empty for a Lab that has none: the window then sorts the global and value
    ///     actions into File and Edit by what they are (new, open, save... / undo, copy, paste...) and the rest stay in Lab and Value.
    /// </summary>
    IEnumerable<LabMenu> Menus => [];

    /// <summary>
    ///     A bar shown between the menu bar and the toolbar (the Menu Lab puts the menu it is making there); null for none. Called once,
    ///     after <see cref="BuildMainView" />.
    /// </summary>
    UIElement? BuildTopBar() => null;

    /// <summary>
    ///     Changes when the default arrangement of the panes does, so windows saved with the old one do not keep it (the saved layout is
    ///     filed under the Lab and this number).
    /// </summary>
    int LayoutVersion => 1;

    /// <summary>Views of the current data (the DOM, the console, ...): each is a pane docked left or right of the main view.</summary>
    IReadOnlyList<LabInspector> Inspectors { get; }

    /// <summary>Commands the Lab adds to the terminal in the window's status bar (the window has its own: help, params, set, ...; and every action is a verb).</summary>
    IReadOnlyList<LabTerminalCommand> TerminalCommands { get; }
}

/// <summary>
///     A view of the current data of a Lab that sits in a pane next to the main view (the DOM of a page, its console, its requests...).
///     The person can move it, tab it with others, float it and close it; the Lab window remembers where it was left.
/// </summary>
public abstract class LabInspector
{
    private UIElement?   _view;
    private LabOwner?    _scope;
    private LabPaneType? _paneType;

    /// <summary>A short stable key ("dom"); how the layout remembers the inspector.</summary>
    public abstract string Id { get; }

    /// <summary>The pane's title.</summary>
    public abstract string Title { get; }

    /// <summary>The key of the pane's icon; null for none.</summary>
    public virtual string? IconKey => null;

    /// <summary>The side the pane is first docked on.</summary>
    public virtual LabDock Dock => LabDock.Right;

    /// <summary>
    ///     Which column (left and right) or which section (bottom) of its side the pane is in, numbered from the window's edge inward. Panes with the same side and lane are tabbed together; panes in different lanes sit next to each
    ///     other when the window is wide enough, else everything on a side is tabbed together.
    /// </summary>
    public virtual int Lane => 0;

    /// <summary>Whether the pane is open in a window that has no remembered layout; the others are opened from the View menu.</summary>
    public virtual bool VisibleByDefault => true;

    /// <summary>
    ///     What the pane is about. The View menu lists the panes of the things the Lab works with and keeps in the Silo (<see cref="LabPaneKind.Entity" />)
    ///     first, and the views of what is going on (<see cref="LabPaneKind.Other" />, the default) after a line.
    /// </summary>
    public virtual LabPaneKind Kind => LabPaneKind.Other;

    /// <summary>
    ///     Whether the pane shows what is always there (<see cref="LabPaneType.Static" />, the default) or what belongs to the subject, part or
    ///     tool at hand (<see cref="LabPaneType.Instance" />: the pane is closed while its <see cref="Scope" /> does not apply).
    /// </summary>
    public virtual LabPaneType PaneType => _paneType ?? LabPaneType.Static;

    /// <summary>
    ///     What owns the pane: an aspect (<see cref="LabOwner.OfAspect" />) or a tool (<see cref="LabOwner.OfTool" />); null for a pane of the
    ///     Lab itself. See <see cref="PaneType" /> for what an owner decides.
    /// </summary>
    public virtual LabOwner? Scope => _scope;

    /// <summary>
    ///     Makes the pane belong to an aspect or tool (see <see cref="Scope" />): by default an instance pane, closed while its owner does
    ///     not apply. Returns the pane, so a Lab writes <c>new MatchesPane(this).OwnedBy(LabOwner.OfAspect("matches"))</c>.
    /// </summary>
    public LabInspector OwnedBy(LabOwner owner, LabPaneType type = LabPaneType.Instance)
    {
        _scope    = owner;
        _paneType = type;
        return this;
    }

    /// <summary>What the pane shows (made once).</summary>
    public UIElement View => _view ??= BuildView();

    /// <summary>Whether the pane is open right now (set by the window).</summary>
    public bool IsShown { get; internal set; }

    /// <summary>Builds what the pane shows.</summary>
    protected abstract UIElement BuildView();

    /// <summary>The pane was opened: refresh whatever was not kept up to date while it was closed.</summary>
    public virtual void OnShown()
    {
    }

    /// <summary>The pane was closed.</summary>
    public virtual void OnHidden()
    {
    }
}

/// <summary>What was typed in the terminal for a command a Lab added.</summary>
/// <param name="Session">The window's session.</param>
/// <param name="Line">The whole line.</param>
/// <param name="Rest">The line after the command's name.</param>
/// <param name="Args">The words after the command's name ("quoted words" kept together).</param>
public sealed record LabTerminalCall(ILabSession Session, string Line, string Rest, IReadOnlyList<string> Args);

/// <summary>A command the Lab adds to its terminal.</summary>
/// <param name="Name">What is typed.</param>
/// <param name="Help">One line for <c>help</c>: the usage and what it does.</param>
/// <param name="Run">Does it; returns the text to show (null for just "ok"). Throw to report a problem.</param>
/// <param name="Aliases">Other names for it.</param>
public sealed record LabTerminalCommand(string Name, string Help, Func<LabTerminalCall, Task<string?>> Run, IReadOnlyList<string>? Aliases = null)
{
    /// <summary>Whether <paramref name="word" /> names this command.</summary>
    public bool Matches(string word)
    {
        return string.Equals(Name, word, StringComparison.OrdinalIgnoreCase)
               || (Aliases?.Any(a => string.Equals(a, word, StringComparison.OrdinalIgnoreCase)) ?? false);
    }
}
