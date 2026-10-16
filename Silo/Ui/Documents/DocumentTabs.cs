using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using AvalonDock;
using AvalonDock.Layout;
using Serilog;

namespace Silo.Ui.Documents;

/// <summary>
///     Optional hooks for a view living in a document tab: it can ask to be pinned (VS Code pins a preview tab the moment
///     you start editing in it) and can rename its tab.
/// </summary>
public interface IDocumentContent
{
    /// <summary>The person started working in the view: a preview tab should become a permanent one.</summary>
    event EventHandler? PinRequested;

    /// <summary>The tab title changed (the document was renamed).</summary>
    event EventHandler? TitleChanged;

    /// <summary>The title the tab should show.</summary>
    string? DocumentTitle { get; }
}

/// <summary>
///     The document tabs in the middle of the main window (the classic Visual Studio arrangement: documents in tabs,
///     tools and views in panes around them). Opening is by <em>key</em>: opening a key that is already open just brings
///     its tab forward, so a table reached from the Explorer, from the Tables list and from the navigation flyout is one tab.
/// </summary>
/// <remarks>
///     <para>
///         <b>Preview and pinned tabs</b> (single-click previews, double-click pins): there is at most one preview tab. Opening
///         something else as a preview <em>replaces</em> its content instead of piling up tabs; opening it pinned, editing in
///         it (<see cref="IDocumentContent.PinRequested" />) or double-clicking promotes it to a permanent tab. A preview tab
///         is drawn in italics: its title carries an invisible marker character that the header template turns into
///         italics, so the title, not extra state, is what the (AvalonDock-owned) header binds to.
///     </para>
///     <para>
///         A tab's <c>closed</c> callback runs exactly once when its content goes away -- closed by the person, replaced as a
///         preview, or closed from code -- which is where a view saves anything still pending.
///     </para>
/// </remarks>
public sealed class DocumentTabs
{
    private const char PreviewMark = '​';

    private readonly DockingManager         _manager;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private Entry? _preview;

    /// <summary>A document's content was put in a tab (a new tab, or the preview tab taking it over).</summary>
    public event EventHandler<UIElement>? ContentOpened;

    /// <summary>A preview tab was kept as a permanent tab.</summary>
    public event EventHandler? PinnedChanged;

    /// <summary>A tab's content went away (closed, replaced as a preview, or closed from code). Raised once per <see cref="ContentOpened" />.</summary>
    public event EventHandler<UIElement>? ContentReleased;

    public DocumentTabs(DockingManager manager)
    {
        _manager = manager;
        manager.DocumentHeaderTemplate = MakeHeaderTemplate();
    }

    private sealed class Entry
    {
        public Entry(LayoutDocument doc)
        {
            Doc = doc;
        }

        public LayoutDocument Doc { get; }

        public string Key { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public UIElement? Content { get; set; }

        public Action<UIElement>? Closed { get; set; }

        public Action? Unhook { get; set; }

        public bool Pinned { get; set; }

        /// <summary>ContentOpened was raised for the current content and ContentReleased has not been yet.</summary>
        public bool Announced { get; set; }
    }

    /// <summary>The keys of the open tabs.</summary>
    public IReadOnlyList<string> Keys => _entries.Keys.ToList();

    public bool Has(string key) => _entries.ContainsKey(key);

    /// <summary>The keys of the permanent tabs (not the preview tab): the documents the person is keeping open.</summary>
    public IReadOnlyCollection<string> PinnedKeys => _entries.Where(e => e.Value.Pinned).Select(e => e.Key).ToList();

    /// <summary>Whether the preview tab (the single italic tab that clicks reuse) is open.</summary>
    public bool HasPreview => _preview is not null;

    /// <summary>Closes the preview tab, if there is one.</summary>
    public void ClosePreview()
    {
        if (_preview is { } preview) Close(preview.Key);
    }

    /// <summary>The content of the selected tab of the document pane (the one being looked at), or null.</summary>
    public UIElement? SelectedContent => FindPane(_manager.Layout?.RootPanel)?.SelectedContent?.Content as UIElement;

    /// <summary>
    ///     Puts tabs back into the document pane after the dock layout was replaced (the old layout took its tabs with it).
    ///     Each tab is given as its document, the content it showed (released before the old layout went) and whether it was
    ///     the selected one.
    /// </summary>
    public void Adopt(IEnumerable<(LayoutDocument Doc, object? Content, bool Selected)> tabs)
    {
        var pane = EnsurePane();
        foreach (var (doc, content, selected) in tabs)
        {
            pane.Children.Add(doc);
            doc.Content = content;
            if (selected) doc.IsSelected = true;
        }
    }

    /// <summary>The content of an open tab, or null.</summary>
    public UIElement? ContentOf(string key) => _entries.TryGetValue(key, out var e) ? e.Content : null;

    /// <summary>
    ///     Shows the document <paramref name="key" />: brings its tab forward if it is open, otherwise builds it. Unpinned, it
    ///     goes into the (single) preview tab; <paramref name="pin" /> makes it a permanent tab and gives it the focus.
    /// </summary>
    /// <param name="key">Identity of the document; two requests with the same key are one tab.</param>
    /// <param name="title">Shown on the tab.</param>
    /// <param name="build">Creates the content; only called when the tab does not exist yet.</param>
    /// <param name="closed">Called once with the content when the tab goes away.</param>
    public void Open(string key, string title, Func<UIElement> build, Action<UIElement>? closed = null, bool pin = false)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            if (pin) Pin(existing);
            Reveal(existing, pin);
            return;
        }

        UIElement content;
        try
        {
            content = build();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Building the document {key} failed", key);
            content = new TextBlock { Text = "This could not be opened: " + ex.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) };
            closed  = null;
        }

        // the preview tab is reused rather than adding another
        if (!pin && _preview is { } reuse)
        {
            Release(reuse);
            _entries.Remove(reuse.Key);
            Attach(reuse, key, title, content, closed, pinned: false);
            reuse.Doc.Content = content;
            Reveal(reuse, false);
            return;
        }

        var doc   = new LayoutDocument { CanClose = true };
        var entry = new Entry(doc);
        Attach(entry, key, title, content, closed, pin);
        doc.Content = content;
        doc.Closed += (_, _) => OnDocumentClosed(entry);

        EnsurePane().Children.Add(doc);
        if (!pin) _preview = entry;
        Reveal(entry, pin);
    }

    /// <summary>Makes the tab permanent (a no-op for a tab that already is, or one that is not open).</summary>
    public void Pin(string key)
    {
        if (_entries.TryGetValue(key, out var e)) Pin(e);
    }

    /// <summary>Changes the title shown on a tab.</summary>
    public void Rename(string key, string title)
    {
        if (!_entries.TryGetValue(key, out var e)) return;

        e.Title     = title;
        e.Doc.Title = Marked(e);
    }

    /// <summary>Closes a tab from code (running its closed callback).</summary>
    public void Close(string key)
    {
        if (!_entries.TryGetValue(key, out var e)) return;

        Forget(e);
        try
        {
            e.Doc.Close();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Closing the document {key} failed", key);
        }
    }

    /// <summary>Closes every tab whose key matches (e.g. all the tabs of a Silo that is closing).</summary>
    public void CloseWhere(Func<string, bool> match)
    {
        foreach (var key in _entries.Keys.Where(match).ToList())
            Close(key);
    }

    // ---- internals ---------------------------------------------------------------------------

    private void Attach(Entry e, string key, string title, UIElement content, Action<UIElement>? closed, bool pinned)
    {
        e.Key     = key;
        e.Title   = title;
        e.Content = content;
        e.Closed  = closed;
        e.Pinned  = pinned;
        e.Doc.ContentId = key;
        e.Doc.Title     = Marked(e);
        _entries[key]   = e;

        if (pinned && ReferenceEquals(_preview, e)) _preview = null;

        if (content is IDocumentContent dc)
        {
            EventHandler pin   = (_, _) => Pin(e);
            EventHandler title2 = (_, _) =>
                                  {
                                      if (dc.DocumentTitle is { Length: > 0 } t) Rename(e.Key, t);
                                  };
            dc.PinRequested += pin;
            dc.TitleChanged += title2;
            e.Unhook         = () =>
                               {
                                   dc.PinRequested -= pin;
                                   dc.TitleChanged -= title2;
                               };
        }
        else
        {
            e.Unhook = null;
        }

        e.Announced = true;
        RaiseDocumentEvent(ContentOpened, content);
    }

    private void RaiseDocumentEvent(EventHandler<UIElement>? handler, UIElement content)
    {
        try
        {
            handler?.Invoke(this, content);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "A document tab listener failed");
        }
    }

    private void Pin(Entry e)
    {
        if (e.Pinned) return;

        e.Pinned    = true;
        e.Doc.Title = Marked(e);
        if (ReferenceEquals(_preview, e)) _preview = null;
        PinnedChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Marked(Entry e) => e.Pinned ? e.Title : PreviewMark + e.Title;

    private static void Reveal(Entry e, bool activate)
    {
        // selecting the tab does not move the keyboard focus into it (so arrowing through the Explorer keeps working);
        // a pinned open does
        e.Doc.IsSelected = true;
        if (activate) e.Doc.IsActive = true;
    }

    /// <summary>Runs the closed callback and drops the event hooks; the entry itself stays registered.</summary>
    private void Release(Entry e)
    {
        e.Unhook?.Invoke();
        e.Unhook = null;

        if (e.Announced && e.Content is { } announced)
        {
            e.Announced = false;
            RaiseDocumentEvent(ContentReleased, announced);
        }

        if (e.Content is { } content && e.Closed is { } closed)
            try
            {
                closed(content);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Closing the document {key} failed", e.Key);
            }

        e.Closed = null;
    }

    /// <summary>Takes the entry out of the registry and releases it. Safe to call twice.</summary>
    private void Forget(Entry e)
    {
        if (_entries.TryGetValue(e.Key, out var current) && ReferenceEquals(current, e))
            _entries.Remove(e.Key);
        if (ReferenceEquals(_preview, e)) _preview = null;
        Release(e);
    }

    private void OnDocumentClosed(Entry e) => Forget(e);

    private LayoutDocumentPane EnsurePane()
    {
        var root = _manager.Layout?.RootPanel;
        if (FindPane(root) is { } found) return found;

        var pane = new LayoutDocumentPane();
        root?.Children.Add(pane);
        return pane;
    }

    private static LayoutDocumentPane? FindPane(ILayoutElement? element)
    {
        switch (element)
        {
            case LayoutDocumentPane pane:
                return pane;
            case ILayoutContainer container:
                foreach (var child in container.Children)
                    if (FindPane(child) is { } found)
                        return found;
                return null;
            default:
                return null;
        }
    }

    // ---- the tab header: the title, in italics while it is a preview ---------------------------

    private static DataTemplate MakeHeaderTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("Title"));
        text.SetBinding(TextBlock.FontStyleProperty, new Binding("Title") { Converter = PreviewStyleConverter.Instance });
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        return new DataTemplate { VisualTree = text };
    }

    private sealed class PreviewStyleConverter : IValueConverter
    {
        public static readonly PreviewStyleConverter Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is string { Length: > 0 } s && s[0] == PreviewMark ? FontStyles.Italic : FontStyles.Normal;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
