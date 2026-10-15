using System.Collections.Specialized;
using AuthoredTable = Silo.DbModel.Global.AuthoredTable;
using Silo.Model;
using Silo.Ui.Windows;

namespace Silo.Tabular;

/// <summary>
///     A window holding one table editor (<see cref="TableDocumentView" />): the pop-out version of a table's document tab,
///     for putting a table beside the main window. All the editing and saving lives in the view; this only provides the
///     window around it and closes it when its Silo is closed.
/// </summary>
public sealed class TableEditorWindow : Window
{
    private readonly OpenSilo          _silo;
    private readonly TableDocumentView _doc;
    private bool                       _closed;

    public TableEditorWindow(OpenSilo silo, ITableStore store, AuthoredTable table, bool showOutput)
    {
        _silo = silo;

        Width  = 920;
        Height = 640;
        Owner  = Application.Current?.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _doc = new TableDocumentView(silo, store, table, showOutput) { ShowPopOut = false };
        _doc.TitleChanged += (_, _) => UpdateTitle();
        Content            = _doc;
        UpdateTitle();

        // each table's window comes back where and as big as it was left (kept in the Silo)
        if (_doc.TableId > 0) WindowPlacements.Track(this, silo, WindowPlacements.Table, $"silo:table/{_doc.TableId}");

        OpenSilo.OpenSilos.CollectionChanged += OnSilosChanged;
        Closing += (_, _) => _doc.Release();
        Closed  += (_, _) =>
                   {
                       _closed = true;
                       OpenSilo.OpenSilos.CollectionChanged -= OnSilosChanged;
                   };
    }

    /// <summary>Forgets pending edits (the table is being deleted).</summary>
    public void Discard() => _doc.Discard();

    /// <summary>The id of the table in the window.</summary>
    public int TableId => _doc.TableId;

    /// <summary>The Silo the table belongs to.</summary>
    public OpenSilo Silo => _silo;

    /// <summary>Brings the window to the front (restoring it if it was minimised).</summary>
    public void Reveal()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Closes the window and shows the table as a tab of the main window again.</summary>
    public void ReturnToTab()
    {
        int id = _doc.TableId;

        // Close() saves what is pending and runs the Closed handlers (which forget the window) before returning
        Close();
        TableEditors.OpenExisting(_silo, id);
    }

    private void UpdateTitle() => Title = $"{_doc.DocumentTitle} - {_silo.Name}";

    private void OnSilosChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // the Silo was closed while this window was open: nowhere left to save to
        if (!_closed && !OpenSilo.OpenSilos.Contains(_silo))
            Dispatcher.BeginInvoke(new Action(() =>
                                              {
                                                  _doc.Discard();
                                                  Close();
                                              }));
    }
}
