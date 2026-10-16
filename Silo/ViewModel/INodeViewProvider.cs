namespace Silo.ViewModel;

/// <summary>
///     Implemented by the object behind a <see cref="SiloNode" /> (its <see cref="SiloNode.DataContext" />) that can build a
///     view of itself, shown in a document tab when the node is selected in the Explorer. A node whose object does not
///     implement it gets a generic view (what it is, its details and what is under it).
/// </summary>
public interface INodeViewProvider
{
    /// <summary>Builds the view (on the UI thread; anything slow should load in the background and fill the view in later), or null for the generic one.</summary>
    UIElement? BuildView();
}
