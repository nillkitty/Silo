using System.Text;
using System.Windows.Controls;
using AuthoredTable = Silo.DbModel.Global.AuthoredTable;
using Serilog;
using Silo.Model;
using Silo.Ui.Windows;

namespace Silo.Tabular;

/// <summary>Raised by the table editor after it pasted cells from the clipboard.</summary>
public sealed class GridPasteEventArgs(ParsedPaste paste) : EventArgs
{
    public ParsedPaste Paste { get; } = paste;
}

/// <summary>
///     Getting data onto a table from the clipboard: reading and parsing it, "paste as a new table", and keeping the raw
///     paste in the Silo (<see cref="IPastedStore" />) so the table can be traced back to what was pasted.
/// </summary>
public static class TablePaste
{
    /// <summary>Reads the formats the table paste understands from the clipboard (null, with a message, if it cannot be read).</summary>
    public static ClipboardPayload? ReadClipboard()
    {
        try
        {
            if (Clipboard.GetDataObject() is not { } data) return null;

            return new ClipboardPayload(Read(data, "Silo.Table"), Read(data, DataFormats.Html), Read(data, DataFormats.CommaSeparatedValue), Read(data, DataFormats.UnicodeText));
        }
        catch (Exception ex) // the clipboard can be locked by another program for a moment
        {
            App.Status("Could not read the clipboard: " + ex.Message);
            return null;
        }
    }

    private static string? Read(IDataObject data, string format)
    {
        try
        {
            return data.GetDataPresent(format) ? data.GetData(format) as string : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Reading the clipboard format {format} failed", format);
            return null;
        }
    }

    /// <summary>Whether the clipboard holds something the table paste could use.</summary>
    public static bool CanPaste()
    {
        try
        {
            return Clipboard.ContainsText() || Clipboard.ContainsData(DataFormats.Html) || Clipboard.ContainsData("Silo.Table");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Keeps the raw paste in the Silo; returns its id, or null when it could not be kept (that never stops the paste itself).</summary>
    public static int? Record(OpenSilo silo, ParsedPaste paste, string target)
    {
        try
        {
            return silo.Data is IPastedStore store ? store.AddPasted("clipboard -> " + target, paste.Format, Encoding.UTF8.GetBytes(paste.Raw)) : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Recording the paste failed");
            return null;
        }
    }

    /// <summary>Edit -> Paste as New Table: makes a table of the clipboard's data (asking for its name and whether the first row is a header) and opens it.</summary>
    public static void AsNewTable(OpenSilo? silo)
    {
        if (TableEditors.StoreOf(silo) is not { } store) return;
        if (ReadClipboard() is not { } payload) return;

        if (PasteParser.Parse(payload) is not { } paste || paste.Rows.Count == 0)
        {
            App.Status("The clipboard holds no table or text to paste");
            return;
        }

        bool header = paste.Sheet is null && (paste.HeaderHint || PasteParser.LooksLikeHeader(paste.Rows));
        var dialog = new PasteTableDialog(paste, TableEditors.UniqueName(store, "Pasted table"), header) { Owner = Application.Current?.MainWindow };
        if (dialog.ShowDialog() != true) return;

        var    sheet = PasteParser.BuildSheet(paste, dialog.FirstRowIsHeader);
        string name  = TableEditors.UniqueName(store, dialog.TableName);

        int?   pastedId = Record(silo!, paste, $"new table '{name}'");
        string note     = $"Pasted from the clipboard on {DateTime.Now:g} ({paste.Format}" + (pastedId is { } id ? $", Pasted #{id}" : string.Empty) + ")";

        AuthoredTable? table = null;
        if (!TableEditors.Guard(() =>
                                {
                                    var created = new AuthoredTable { Name = name, Description = note };
                                    created.WriteSheet(sheet);
                                    table = store.SaveTable(created);
                                }, "create the table"))
            return;

        TableEditors.RaiseChanged();
        TableEditors.OpenExisting(silo, table!.Id);
        App.Status($"Created \"{name}\" from the clipboard ({sheet.Rows.Count} rows, {sheet.Columns.Count} columns)");
    }
}

/// <summary>
///     Asks what to do with pasted data before it becomes a new table: its name, whether the first row holds column names,
///     with a preview of the result.
/// </summary>
public sealed class PasteTableDialog : Window
{
    private readonly ParsedPaste _paste;
    private readonly TextBox     _name;
    private readonly CheckBox    _header;
    private readonly DataGrid    _preview;

    public PasteTableDialog(ParsedPaste paste, string name, bool firstRowIsHeader)
    {
        _paste = paste;

        Title                 = "Paste as new table";
        Width                 = 640;
        Height                = 460;
        MinWidth              = 420;
        MinHeight             = 320;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _name = new TextBox { Text = name, Margin = new Thickness(0, 2, 0, 8) };
        _name.SelectAll();

        var about = new TextBlock { Text = paste.Describe(), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        about.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");

        _header = new CheckBox
                  {
                      Content   = "First row holds the column names",
                      IsChecked = firstRowIsHeader,
                      IsEnabled = paste.Sheet is null, // a Silo table brings its own columns
                      Margin    = new Thickness(0, 0, 0, 8)
                  };
        _header.Click += (_, _) => RefreshPreview();

        _preview = new DataGrid
                   {
                       AutoGenerateColumns = false,
                       IsReadOnly          = true,
                       CanUserAddRows      = false,
                       HeadersVisibility   = DataGridHeadersVisibility.Column
                   };

        var ok     = new Button { Content = "Create table", IsDefault = true, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 3, 14, 3) };
        ok.Click += (_, _) => DialogResult = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Table name" });
        top.Children.Add(_name);
        top.Children.Add(about);
        top.Children.Add(_header);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);

        var root = new DockPanel { Margin = new Thickness(14) };
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(_preview);
        Content = root;

        WindowPlacements.Track(this, App.Instance?.ActiveSilo, WindowPlacements.Dialog, "silo:dialog/paste-table");

        Loaded += (_, _) =>
                  {
                      _name.Focus();
                      RefreshPreview();
                  };
    }

    public string TableName => string.IsNullOrWhiteSpace(_name.Text) ? "Pasted table" : _name.Text.Trim();

    public bool FirstRowIsHeader => _header.IsChecked == true;

    /// <summary>Shows the first rows as they will be: column names and types as guessed, cells as pasted.</summary>
    private void RefreshPreview()
    {
        var sheet = PasteParser.BuildSheet(_paste, FirstRowIsHeader);

        var table = new System.Data.DataTable();
        for (int c = 0; c < sheet.Columns.Count; c++) table.Columns.Add($"c{c}", typeof(string));
        foreach (var row in sheet.Rows.Take(12)) table.Rows.Add(row.Cast<object>().ToArray());

        _preview.Columns.Clear();
        for (int c = 0; c < sheet.Columns.Count; c++)
        {
            var column = sheet.Columns[c];
            _preview.Columns.Add(new DataGridTextColumn
                                 {
                                     Header  = $"{column.Name}  ({column.Type.ToString().ToLowerInvariant()})",
                                     Binding = new System.Windows.Data.Binding($"[c{c}]")
                                 });
        }

        _preview.ItemsSource = table.DefaultView;
    }
}
