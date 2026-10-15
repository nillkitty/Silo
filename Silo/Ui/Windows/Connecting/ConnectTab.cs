namespace Silo.Ui.Windows.Connecting;

/// <summary>What Connect will do on the current tab.</summary>
/// <param name="Name">What to call the connection.</param>
/// <param name="Uri">Where it goes, when it has a URI (remembered in the recent destinations).</param>
/// <param name="Create">Makes the connection, asynchronously and cancellably. May throw; the dialog then stays open and says why.</param>
public sealed record ConnectionTarget(string Name, Uri? Uri, Func<CancellationToken, Task<IConnection>> Create);

/// <summary>What the dialog hands back when a connection was made.</summary>
public sealed record ConnectionResult(IConnection Connection, string Name, Uri? Uri);

/// <summary>
///     One of the tabs of the New Connection dialog: a way of choosing a connection. A tab tells the dialog what Connect
///     would do (<see cref="Target" />, null while nothing usable is chosen) and the dialog enables Connect accordingly.
/// </summary>
public abstract class ConnectTab
{
    /// <summary>The tab's caption.</summary>
    public abstract string Header { get; }

    /// <summary>The tab's page.</summary>
    public abstract UIElement View { get; }

    /// <summary>What Connect would do right now, or null when it cannot.</summary>
    public ConnectionTarget? Target { get; private set; }

    /// <summary>A line for the bottom of the dialog: what is chosen, or what is missing.</summary>
    public string? Hint { get; private set; }

    /// <summary>True when <see cref="Hint" /> says what is wrong rather than what is chosen.</summary>
    public bool HintIsProblem { get; private set; }

    /// <summary>Raised when <see cref="Target" /> or <see cref="Hint" /> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when the person asks to connect from the tab itself (a double-click on a destination).</summary>
    public event EventHandler? ConnectRequested;

    /// <summary>The dialog was shown (all tabs) so a tab can start its background work.</summary>
    public virtual void Start()
    {
    }

    /// <summary>The tab was chosen (or the dialog opened on it): put the cursor where the person starts typing, if there is such a place.</summary>
    public virtual void FocusInput()
    {
    }

    /// <summary>A connection was made from this tab; remember what was used.</summary>
    public virtual void Connected(ConnectionTarget target)
    {
    }

    /// <summary>The dialog is closing: stop background work.</summary>
    public virtual void Stop()
    {
    }

    /// <summary>Sets what Connect would do and says why not when it cannot.</summary>
    protected void Set(ConnectionTarget? target, string? hint, bool isProblem = false)
    {
        Target        = target;
        Hint          = hint;
        HintIsProblem = isProblem;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected void RequestConnect()
    {
        if (Target is not null) ConnectRequested?.Invoke(this, EventArgs.Empty);
    }
}
