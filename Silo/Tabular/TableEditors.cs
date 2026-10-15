using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using AuthoredTable = Silo.DbModel.Global.AuthoredTable;
using Serilog;
using Silo.Model;
using Silo.Ui.Documents;

namespace Silo.Tabular;

/// <summary>
///     Opens, creates, duplicates and deletes authored tables. A table opens as a document tab in the main window (the
///     Explorer previews it on a click, this pins it), or on request in a window of its own; the list views are told when
///     anything changed.
/// </summary>
public static class TableEditors
{
    private static readonly Dictionary<string, TableEditorWindow> Windows = new();
    private static readonly ConditionalWeakTable<OpenSilo, StrongBox<int>> SiloKeys = new();
    private static int _nextSiloKey;

    /// <summary>Raised after a table is created, saved, renamed, duplicated or deleted.</summary>
    public static event EventHandler? Changed;

    public static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);

    /// <summary>The table windows that are open (tables popped out of their tab); the View menu lists them.</summary>
    public static IReadOnlyList<TableEditorWindow> PopOutWindows => Windows.Values.ToList();

    private static DocumentTabs? Tabs => (Application.Current?.MainWindow as MainWindow)?.Documents;

    // ---- document identity -------------------------------------------------------------------

    /// <summary>The start of the document key of every table of a Silo (to close them all with it).</summary>
    public static string DocPrefix(OpenSilo silo)
    {
        var box = SiloKeys.GetValue(silo, _ => new StrongBox<int>(++_nextSiloKey));
        return $"table:{box.Value}:";
    }

    /// <summary>The key of the document tab showing a table; the same for the Explorer, the Tables list and the editor itself.</summary>
    public static string DocKey(OpenSilo silo, int id) => DocPrefix(silo) + id;

    /// <summary>The table editor as a piece of UI for a document tab. A table that has gone shows a message instead.</summary>
    public static UIElement BuildDocument(OpenSilo silo, int id, bool showOutput)
    {
        if (silo.Data is ITableStore store && store.GetTable(id) is { } table)
            return new TableDocumentView(silo, store, table, showOutput);

        return new TextBlock { Text = "This table no longer exists.", Margin = new Thickness(24) };
    }

    /// <summary>The document tab closed: saves what is pending in it.</summary>
    public static void Release(UIElement element)
    {
        (element as TableDocumentView)?.Release();
    }

    /// <summary>The table store of a Silo, or null (with the reason shown) when it cannot hold tables.</summary>
    public static ITableStore? StoreOf(OpenSilo? silo, bool needWritable = true)
    {
        if (silo is null)
        {
            App.Status("Open a Silo first");
            return null;
        }

        if (silo.Data is not ITableStore store)
        {
            App.Status("This Silo cannot hold tables");
            return null;
        }

        if (needWritable && !silo.IsWritable)
        {
            App.Status("This Silo is read-only");
            return null;
        }

        return store;
    }

    // ---- opening -----------------------------------------------------------------------------

    /// <summary>Add Data -> Tabular: creates a table in the Silo and opens it for typing into.</summary>
    public static void CreateAndOpen(OpenSilo? silo, TableTemplate template)
    {
        if (StoreOf(silo) is not { } store) return;

        AuthoredTable? table = null;
        if (!Guard(() =>
                   {
                       var created = new AuthoredTable { Name = UniqueName(store, template.DefaultName) };
                       created.WriteSheet(template.Build());
                       table = store.SaveTable(created);
                   }, "create the table"))
            return;

        RaiseChanged();
        Show(silo!, store, table!, template.ShowOutput);
    }

    /// <summary>Runs a store operation, reporting a failure (full archive, read-only file, ...) to the person instead of crashing.</summary>
    public static bool Guard(Action action, string what)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not {what}", what);
            MessageBox.Show($"Could not {what}:\n{ex.Message}", AppIdentity.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    /// <summary>Opens a table in its own (pinned) document tab; brings it forward if it is already open.</summary>
    public static void OpenExisting(OpenSilo? silo, int id)
    {
        if (StoreOf(silo, false) is not { } store) return;
        if (store.GetTable(id) is not { } table)
        {
            App.Status("That table no longer exists");
            return;
        }

        Show(silo!, store, table, false);
    }

    /// <summary>Moves a table into a window of its own (closing its tab, if it has one).</summary>
    public static void OpenInWindow(OpenSilo? silo, int id)
    {
        if (StoreOf(silo, false) is not { } store || silo is null) return;

        string key = DocKey(silo, id);
        if (Windows.TryGetValue(key, out var existing))
        {
            existing.Activate();
            return;
        }

        // the tab saves what is pending as it closes, so the window reads the current version
        Tabs?.Close(key);

        if (store.GetTable(id) is not { } table)
        {
            App.Status("That table no longer exists");
            return;
        }

        var window = new TableEditorWindow(silo, store, table, false);
        Windows[key] = window;
        window.Closed += (_, _) => Windows.Remove(key);
        window.Show();
    }

    private static void Show(OpenSilo silo, ITableStore store, AuthoredTable table, bool showOutput)
    {
        string key = DocKey(silo, table.Id);
        if (Windows.TryGetValue(key, out var window))
        {
            window.Activate();
            return;
        }

        if (Tabs is { } tabs)
        {
            tabs.Open(key, table.Name, () => BuildDocument(silo, table.Id, showOutput), Release, pin: true);
            return;
        }

        // no main window to hold a tab: use a window
        OpenInWindow(silo, table.Id);
    }

    // ---- duplicate / delete ------------------------------------------------------------------

    public static void Duplicate(OpenSilo? silo, int id)
    {
        if (StoreOf(silo) is not { } store) return;
        if (store.GetTable(id) is not { } source)
        {
            App.Status("That table no longer exists");
            return;
        }

        // pending edits of the open copy belong in the duplicate
        (Tabs?.ContentOf(DocKey(silo!, id)) as TableDocumentView)?.CommitAndFlush();
        source = store.GetTable(id) ?? source;

        var copy = source.Clone();
        copy.Id        = 0;
        copy.Name      = UniqueName(store, source.Name + " copy");
        copy.Created   = copy.Modified = DateTime.UtcNow;
        copy.WorldType = null!; // the copy gets a type of its own
        if (Guard(() => store.SaveTable(copy), "duplicate the table"))
            RaiseChanged();
    }

    public static void Delete(OpenSilo? silo, int id)
    {
        if (StoreOf(silo) is not { } store) return;

        string name = store.GetTable(id)?.Name ?? "this table";
        var answer = MessageBox.Show($"Delete the table \"{name}\" from this Silo?\n\nThis cannot be undone (other than by not saving the Silo).",
                                     AppIdentity.AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        // an open editor must not write the table back as it closes
        string key = DocKey(silo!, id);
        (Tabs?.ContentOf(key) as TableDocumentView)?.Discard();
        Tabs?.Close(key);
        if (Windows.TryGetValue(key, out var window))
        {
            window.Discard();
            window.Close();
        }

        if (Guard(() => store.DeleteTable(id), "delete the table"))
            RaiseChanged();
    }

    public static string UniqueName(ITableStore store, string baseName)
    {
        var names = store.ListTables().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
            if (!names.Contains($"{baseName} {i}"))
                return $"{baseName} {i}";
    }
}

/// <summary>A starting point for a new table (Add Data -> Tabular -> ...).</summary>
public sealed record TableTemplate(string DefaultName, Func<SheetData> Build, bool ShowOutput = true)
{
    /// <summary>All columns are rich text (inline markdown formatting).</summary>
    public static TableTemplate RichText => new("Rich text table", () => SheetData.Create(CellType.RichText, 3, 5));

    /// <summary>All columns are literal text.</summary>
    public static TableTemplate PlainText => new("Table", () => SheetData.Create(CellType.Text, 3, 5));

    /// <summary>A starter with a text, a number and a date column; change the types per column afterwards.</summary>
    public static TableTemplate Typed => new("Typed table", () =>
                                                           {
                                                               var s = new SheetData();
                                                               s.Columns.Add(new SheetColumn { Name = "Name", Type = CellType.Text });
                                                               s.Columns.Add(new SheetColumn { Name = "Count", Type = CellType.Number });
                                                               s.Columns.Add(new SheetColumn { Name = "Date", Type = CellType.Date });
                                                               for (int i = 0; i < 5; i++) s.Rows.Add(["", "", ""]);
                                                               return s;
                                                           });

    public static TableTemplate FromKey(string? key) => (key ?? "rich").ToLowerInvariant() switch
                                                        {
                                                            "plain" => PlainText,
                                                            "typed" => Typed,
                                                            _       => RichText
                                                        };
}
