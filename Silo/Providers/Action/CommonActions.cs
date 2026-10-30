namespace Silo.Providers.Action;

public enum CommonActions
{
    /// <summary>
    ///     No action
    /// </summary>
    None,

    /// <summary>
    ///     Show the item (App, Silo, Connection, Container, Node)
    /// </summary>
    Show,

    /// <summary>
    ///     Hide the item (App, Silo, Connection, Container, Node)
    /// </summary>
    Hide,

    /// <summary>
    ///     Activate/select the item (App, Silo, Connection, Container, Node)
    /// </summary>
    Activate,

    /// <summary>
    ///     [Tab] Focus the item
    /// </summary>
    Focus,

    /// <summary>
    ///     Opens the item in its default editor / viewer (Connection, Container, Node, Value)
    /// </summary>
    Open,

    /// <summary>
    ///     Expands the item in the explorer (Connection, Container)
    /// </summary>
    Expand,

    /// <summary>
    ///     Collpses the item in the explorer (Connection, Container)
    /// </summary>
    Collapse,

    /// <summary>
    ///     Closes the item's document pane if it has one (Connection, Container, Node, Value)
    /// </summary>
    Close,

    /// <summary>
    ///     Opens the item's properties pages (Connection, Container, Node, Value)
    /// </summary>
    Properties,

    /// <summary>
    ///     Exports to file / prompts to export to file (Connection, Container, Node, Value)
    /// </summary>
    Export,

    /// <summary>
    ///     Removes an item from the explorer, possibly the Silo
    /// </summary>
    Remove,

    /// <summary>
    ///     Deletes a leaf object (Node) in its native source (Node)
    /// </summary>
    Delete,

    /// <summary>
    ///     Deletes a container and all of its children in its native source (Container)
    /// </summary>
    DeleteTree,

    /// <summary>
    ///     Moves an item to a new parent container (Container, Node)
    /// </summary>
    Move,

    /// <summary>
    ///     Renames an item (Connection, Container, Node, Value)
    /// </summary>
    Rename,

    /// <summary>
    ///     Opens the context menu for this item
    /// </summary>
    OpenContextMenu,

    /// <summary>
    ///     Cuts the item (to the clipbard) (Silo, Connection, Container, Node, Value)
    /// </summary>
    Cut,

    /// <summary>
    ///     Copies the item (to the clipboard) (Silo, Connection, Container, Node, Value)
    /// </summary>
    Copy,

    /// <summary>
    ///     Pastes an item (from the clipboard) (Connection, Container, List)
    /// </summary>
    Paste,

    /// <summary>
    ///     Duplicates the item (Container, Node, Value)
    /// </summary>
    Duplicate,

    /// <summary>
    ///     Reroots the items tree (Connection, Container, Node)
    /// </summary>
    Reroot,

    /// <summary>
    ///     Adds this node as a new root (Connection, Container, Node, Value)
    /// </summary>
    RootFromHere,

    /// <summary>
    ///     Imports an item (from a file) (Connection, Container, List)
    /// </summary>
    Import,

    /// <summary>
    ///     Creates an item (Connection, Container, List)
    /// </summary>
    Create,

    /// <summary>
    ///     Copy's the items URI
    /// </summary>
    CopyUri,

    /// <summary>
    ///     Creates a deep link shortcut to this item and places it on the desktop (or prompts user)
    /// </summary>
    AddShortcut,

    /// <summary>
    ///     Adds this item to the user bookmarks
    /// </summary>
    AddToBookmarks,

    /// <summary>
    ///     Remove this item from the user bookmarks
    /// </summary>
    RemoveFromBookmarks
}