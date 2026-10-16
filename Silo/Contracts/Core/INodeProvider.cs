using Silo.ViewModel;

namespace Silo.Contracts.Core;

public interface ILocatable
{
    Uri? GetUri(UriContext context);
}

public enum UriContext
{
    /// <summary>
    ///     Gets any URI associated with this object
    /// </summary>
    Any,

    /// <summary>
    ///     Gets only public, well-known URIs
    /// </summary>
    Public,

    /// <summary>
    ///     Prefer private internal URIs
    /// </summary>
    Private,

    /// <summary>
    ///     Get the URI associated with a specific node in the navigation tree/explorer
    /// </summary>
    Node,

    /// <summary>
    ///     Gets a URI that can be used on the system to navigate to this object via deep linking
    /// </summary>
    Navigation
}

/// <summary>
///     A connection, tool, or document which provides a node to the Silo navigation tree.
/// </summary>
public interface INodeProvider
{
    /// <summary>
    ///     The raw thing this provider stands for (a <c>FileSystemInfo</c> for a file system item), for code that wants the
    ///     data and not the model. It is not the node's <see cref="SiloNode.DataContext" />: that is the provider itself, so
    ///     the Explorer can find the provider's details, view and menu. <see cref="SiloNode.Data" /> returns this.
    /// </summary>
    object? Data { get; }

    /// <summary>
    ///     Called upon a Connector returning a Connection /
    ///     a Tool being registered /
    ///     a document being added -- that implements this interface
    /// </summary>
    /// <returns>
    ///     Returns a SiloNode to add to the Nav Tree, or null if it does not have a node to
    ///     contribute at this time.
    /// </returns>
    SiloNode? GetNode();
}

public interface INodeProvider<out TModel> : INodeProvider
{
    new TModel? Data { get; }

    object? INodeProvider.Data => Data;
}