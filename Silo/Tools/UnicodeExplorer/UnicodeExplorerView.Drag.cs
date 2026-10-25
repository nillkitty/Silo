namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     Dragging characters out of the results: onto the collection, a palette, or into another program (as text). A drag from a selected cell
///     takes the whole selection; the collection and the palettes take the drop (see <see cref="GlyphDrag" />).
/// </summary>
public sealed partial class UnicodeExplorerView
{
    /// <summary>A plain press on one of several selected cells, waiting for the release to select just that one (no release: it was a drag).</summary>
    private int _deferredClick = -1;

    private void OnItemReleased(int index)
    {
        if (_deferredClick == index) OnItemClicked(index, System.Windows.Input.ModifierKeys.None, 1);
        _deferredClick = -1;
    }

    private void OnItemDragStarted(int index, UIElement from)
    {
        _deferredClick = -1;
        if (index < 0 || index >= _results.Length) return;

        int[] picked     = _sel.Contains(index) ? SelectedCodePoints() : [_results[index]];
        var   codePoints = picked.Where(IsCollectable).ToList();
        if (codePoints.Count == 0)
        {
            Say("Unassigned code points cannot be dragged.");
            return;
        }

        DragDrop.DoDragDrop(from, GlyphDrag.Create(codePoints), DragDropEffects.Copy);
    }
}
