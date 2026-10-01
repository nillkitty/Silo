using Silo.ViewModel;

namespace Silo.Connectors;

/// <summary>
///     Context given to initializing Connectors
/// </summary>
public class ConnectContext
{
    public INavManager NavigationManager { get; }

    /// <summary>
    ///     The reason the connection is being initialized.
    /// </summary>
    public ConnectReason Reason { get; }

    public SiloNode CreateNode(SiloNode? parent, object model)
    {
        var n = new SiloNode(model.Required());
        NavigationManager.AddNode(n, parent);
        return n;
    }

    public ConnectContext(ConnectReason reason, INavManager navManager)
    {
        Reason            = reason;
        NavigationManager = navManager.Required();
    }
}

/// <summary>
///     The reason a connection is being initialized
/// </summary>
public enum ConnectReason
{
    /// <summary>
    ///     The user is creating the connection interactively.
    /// </summary>
    UserCreate,

    /// <summary>
    ///     The connection is being restored interactively
    /// </summary>
    UserRestore,

    /// <summary>
    ///     The connection is being restored non-interactively.
    /// </summary>
    BackgroundRestore
}