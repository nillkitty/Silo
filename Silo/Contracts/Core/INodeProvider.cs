using Silo.ViewModel;

namespace Silo.Contracts.Core;

/// <summary>
///     A connection, tool, or document which provides a node to the Silo navigation tree.
/// </summary>
public interface INodeProvider
{
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