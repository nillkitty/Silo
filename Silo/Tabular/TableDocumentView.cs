using System.Windows.Controls;
using System.Windows.Threading;
using AuthoredTable = Silo.DbModel.Global.AuthoredTable;
using Serilog;
using Silo.Model;
using Silo.Ui.Documents;

namespace Silo.Tabular;

/// <summary>
///     The editor of one authored table, as the content of a document tab (or of a pop-out window,
///     <see cref="TableEditorWindow" />). Edits are saved into the Silo a moment after typing stops, when the tab closes
///     and before the Silo is saved or closed, so there is no Save button: the Silo itself is saved with File -> Save Silo.
/// </summary>
public sealed class TableDocumentView : UserControl, IDocumentContent, IEditContributor
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);

    private readonly OpenSilo        _silo;
    private readonly ITableStore     _store;
    private readonly SheetEditorView _view;
    private readonly TextBox         _name;
    private readonly TextBlock       _state;
    private readonly Button          _popOut;
    private readonly DispatcherTimer _saveTimer;
    private readonly Func<Task>      _saveHook;

    private AuthoredTable _table;
    private bool          _dirty;
    private bool          _released;
    private bool          _loading = true;

    public TableDocumentView(OpenSilo silo, ITableStore store, AuthoredTable table, bool showOutput)
    {
        _silo  = silo;
        _store = store;
        _table = table;

        _view = new SheetEditorView();
        _view.Load(table.ReadSheet());
        _view.Changed += (_, _) => OnEdited();
        _view.Pasted  += (_, e) => TablePaste.Record(_silo, e.Paste, $"table '{DocumentTitle}'");
        if (showOutput) _view.ShowOutputPane();

        _name = new TextBox { Text = table.Name, MinWidth = 260, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 6, 8, 6), ToolTip = "Table name" };
        _name.TextChanged += (_, _) =>
                             {
                                 if (!_loading) OnEdited();
                             };

        _state = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _state.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");

        _popOut = new Button { Content = "Open in window", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Move this table into a window of its own" };
        _popOut.Click += (_, _) => TableEditors.OpenInWindow(_silo, _table.Id);

        var top = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_popOut, Dock.Right);
        DockPanel.SetDock(_state, Dock.Right);
        top.Children.Add(_popOut);
        top.Children.Add(_state);
        top.Children.Add(_name);

        var root = new DockPanel();
        root.Children.Add(top);
        root.Children.Add(_view);
        Content = root;

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) => Flush();

        // File -> Save and closing the Silo run the save hooks first, so edits still waiting for the timer are included
        _saveHook = () =>
                    {
                        CommitAndFlush();
                        return Task.CompletedTask;
                    };
        _silo.SaveHooks.Add(_saveHook);

        _loading = false;
        SetState("Saved in the Silo");
    }

    /// <summary>The id of the table being edited.</summary>
    public int TableId => _table.Id;

    /// <summary>Whether the "Open in window" button shows (it does not in the pop-out window itself).</summary>
    public bool ShowPopOut
    {
        get => _popOut.Visibility == Visibility.Visible;
        set => _popOut.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public IEnumerable<EditEntry> EditEntries() => _view.EditEntries();

    /// <inheritdoc />
    public event EventHandler? PinRequested;

    /// <inheritdoc />
    public event EventHandler? TitleChanged;

    /// <inheritdoc />
    public string? DocumentTitle => string.IsNullOrWhiteSpace(_name.Text) ? _table.Name : _name.Text.Trim();

    private void OnEdited()
    {
        if (_loading || _released) return;

        _dirty = true;
        SetState("Editing...");
        _saveTimer.Stop();
        _saveTimer.Start();

        // working in a preview tab makes it a permanent one
        PinRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Writes pending edits into the Silo now (does nothing when there are none, or the Silo is gone).</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        if (!_dirty) return;

        // the Silo was closed: nowhere left to save to
        if (!OpenSilo.OpenSilos.Contains(_silo))
        {
            _dirty = false;
            return;
        }

        try
        {
            var table = _table.Clone();
            table.Name     = DocumentTitle ?? _table.Name;
            table.Modified = DateTime.UtcNow;
            table.WriteSheet(_view.ToSheet());

            bool renamed = !string.Equals(table.Name, _table.Name, StringComparison.Ordinal);
            _table = _store.SaveTable(table);
            _dirty = false;
            SetState($"Saved in the Silo at {DateTime.Now:t}" + (_silo.IsDirty ? " (File -> Save Silo to write the file)" : string.Empty));
            if (renamed) TitleChanged?.Invoke(this, EventArgs.Empty);
            TableEditors.RaiseChanged();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving table {name} failed", _table.Name);
            SetState("Could not save: " + ex.Message);
        }
    }

    /// <summary>
    ///     Like <see cref="Flush" />, but first ends a cell edit still in progress (so text typed into a cell that has not been
    ///     left yet is included). Used when the Silo is about to be saved or closed, never from the idle timer, which must not
    ///     pull the cell out from under someone who is still typing.
    /// </summary>
    public void CommitAndFlush()
    {
        if (_released) return;

        try
        {
            _view.CommitEdit();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Committing the cell edit before saving failed");
        }

        Flush();
    }

    /// <summary>Forgets pending edits (the table is being deleted).</summary>
    public void Discard()
    {
        _dirty    = false;
        _released = true;
        _saveTimer.Stop();
        _silo.SaveHooks.Remove(_saveHook);
    }

    /// <summary>The tab or window is going away: saves what is pending and stops listening for the Silo's save.</summary>
    public void Release()
    {
        if (_released) return;

        CommitAndFlush();
        _released = true;
        _saveTimer.Stop();
        _silo.SaveHooks.Remove(_saveHook);
    }

    private void SetState(string text) => _state.Text = text;
}
