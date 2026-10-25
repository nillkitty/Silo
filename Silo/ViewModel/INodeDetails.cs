namespace Silo.ViewModel;

/// <summary>
///     Implemented by the object behind a <see cref="SiloNode" /> (its <see cref="SiloNode.DataContext" />) that can say
///     what it is and describe itself, for the Explorer's Properties pane.
/// </summary>
public interface INodeDetails
{
    /// <summary>The node's icon: an icon key ("Plug") or a picture's address.</summary>
    IconRef? Icon { get; }
    string Name { get; }

    /// <summary>What the node is, in words ("Folder", "Drive", "Junction"...).</summary>
    string TypeName { get; }

    Uri Uri { get; }

    /// <summary>
    ///     Label / value rows describing the node. May read the file system (or whatever backs it), so it is only called
    ///     when the person selects the node.
    /// </summary>
    IEnumerable<(string Name, string Value)> GetDetails();
}