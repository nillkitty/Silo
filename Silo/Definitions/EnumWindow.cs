using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Serilog;
using Silo.Providers.Definitions;
using EnumFlags = Silo.DbModel.Meta.EnumFlags;
using EnumType = Silo.DbModel.Meta.EnumType;

namespace Typing;

/// <summary>
///     One line of the values grid in the enum dialog: a label, its value and a note. A public class with a default
///     constructor, so the grid can add rows.
/// </summary>
public sealed class EnumValueRow
{
    public string Description { get; set; } = string.Empty;
    public int    Id          { get; set; }

    public bool   IsBlank => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Value) && string.IsNullOrWhiteSpace(Description);
    public string Name    { get; set; } = string.Empty;
    public string Value   { get; set; } = string.Empty;
}

/// <summary>
///     The New Enum / Edit Enum dialog (modal): the enum's name, what its values are (integers, strings, guids ...), a few
///     flags, and a grid of its
///     known values (label, value, note). Save checks the whole definition (<see cref="EnumRules" />) and says what is
///     wrong in the dialog; it
///     writes through the Silo's <see cref="IEnumStore" />.
/// </summary>
public sealed class EnumWindow : Window
{
    private readonly CheckBox _bitmask = new() { Content = "_Bitmask: values are bits that can be combined" };

    private readonly TextBox _description = new()
                                            {
                                                AcceptsReturn               = true, TextWrapping = TextWrapping.Wrap, Height = 48,
                                                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                                            };

    private readonly DataGrid _grid  = new();
    private readonly CheckBox _known = new() { Content = "_Only known values are kept; others are stripped on import" };

    private readonly TextBlock _message = new()
                                          {
                                              TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
                                              Visibility   = Visibility.Collapsed
                                          };

    private readonly TextBox                            _name    = new();
    private readonly CheckBox                           _numbers = new() { Content = "Always show the _number instead of the label" };
    private readonly EnumRecord                         _original;
    private readonly ObservableCollection<EnumValueRow> _rows = [];
    private readonly IEnumStore                         _store;
    private readonly ComboBox                           _type = new();

    /// <summary>The enum as it was saved, once the dialog was closed with Save.</summary>
    public EnumRecord? Saved { get; private set; }

    private EnumType SelectedType => _type.SelectedValue is EnumType t ? t : EnumType.Integral;

    /// <summary>
    ///     Shows the dialog: a new enum (no <paramref name="editId" />) or that enum. Returns the enum as saved, or null
    ///     when cancelled.
    /// </summary>
    public static EnumRecord? Prompt(IEnumStore store, int? editId)
    {
        EnumRecord original = editId is { } id
                                  ? store.GetEnum(id) ?? throw new KeyNotFoundException("That enum is no longer in the Silo.")
                                  : EnumRecord.Create(string.Empty);

        var window = new EnumWindow(store, original);
        return window.ShowDialog() == true ? window.Saved : null;
    }

    private static DataGridTextColumn Column(string header, string property, double width)
    {
        return new DataGridTextColumn
               { Header = header, Binding = new Binding(property) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = width };
    }

    /// <summary>Only integral and byte enums can be bitmasks: the box is disabled (and cleared) for the others.</summary>
    private void ApplyType()
    {
        bool supported = EnumRules.SupportsBitmask(SelectedType);
        _bitmask.IsEnabled = supported;
        if (!supported) _bitmask.IsChecked = false;
    }

    private void AddValueRow()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);

        var draft = Draft();
        var row   = new EnumValueRow { Value = EnumRules.SuggestNextValue(draft) };
        _rows.Add(row);
        _grid.SelectedItem = row;
        _grid.ScrollIntoView(row);
        _grid.CurrentCell = new DataGridCellInfo(row, _grid.Columns[0]);
        _grid.Focus();
        _grid.BeginEdit();
    }

    private void RemoveSelected()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var row in _grid.SelectedItems.OfType<EnumValueRow>().ToList()) _rows.Remove(row);
    }

    /// <summary>The enum as the dialog shows it now (not yet checked).</summary>
    private EnumRecord Draft()
    {
        var flags                             = EnumFlags.None;
        if (_bitmask.IsChecked == true) flags |= EnumFlags.Bitmask;
        if (_known.IsChecked   == true) flags |= EnumFlags.KnownValuesOnly;
        if (_numbers.IsChecked == true) flags |= EnumFlags.DontUseName;

        var values = _rows.Where(r => !r.IsBlank)
                          .Select(r => new EnumValueRecord(r.Id, r.Name ?? string.Empty, r.Value,
                                                           string.IsNullOrWhiteSpace(r.Description) ? null : r.Description))
                          .ToList();
        return new EnumRecord(_original.Id, _name.Text, string.IsNullOrWhiteSpace(_description.Text) ? null : _description.Text,
                              SelectedType, flags, values);
    }

    private void Save()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row,  true);

        var draft    = Draft();
        var others   = _store.GetEnums().Where(e => e.Id != _original.Id).Select(e => e.Name);
        var problems = EnumRules.Validate(draft, others);
        if (problems.Count > 0)
        {
            ShowMessage(string.Join(Environment.NewLine, problems));
            return;
        }

        try
        {
            Saved        = _store.SaveEnum(draft);
            DialogResult = true;
        }
        catch (DefinitionException ex)
        {
            ShowMessage(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The enum could not be saved");
            ShowMessage("The enum could not be saved: " + ex.Message);
        }
    }

    private void ShowMessage(string text)
    {
        _message.Text       = text;
        _message.Foreground = Brushes.IndianRed;
        _message.Visibility = Visibility.Visible;
    }

    private static void AddRow(Grid grid, int row, string label, UIElement field)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var text = new Label
                   {
                       Content = label, Target = field, VerticalAlignment = VerticalAlignment.Top,
                       Padding = new Thickness(0, 3, 12, 0)
                   };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);

        if (field is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);

        grid.Children.Add(text);
        grid.Children.Add(field);
    }

    private EnumWindow(IEnumStore store, EnumRecord original)
    {
        _store    = store;
        _original = original;
        bool isNew = original.Id == 0;

        Title                 = isNew ? "New enum" : "Edit enum";
        Width                 = 560;
        Height                = 620;
        MinWidth              = 460;
        MinHeight             = 480;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner                 = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
        if (Owner is null) WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _type.ItemsSource       = Enum.GetValues<EnumType>().Select(t => new { Type = t, Text = DefinitionText.Describe(t) }).ToList();
        _type.SelectedValuePath = "Type";
        _type.DisplayMemberPath = "Text";
        _type.SelectedValue     = original.Type;

        _name.Text         = original.Name;
        _description.Text  = original.Description ?? string.Empty;
        _bitmask.IsChecked = (original.Flags & EnumFlags.Bitmask)         != 0;
        _known.IsChecked   = (original.Flags & EnumFlags.KnownValuesOnly) != 0;
        _numbers.IsChecked = (original.Flags & EnumFlags.DontUseName)     != 0;
        foreach (var v in original.Values)
            _rows.Add(new EnumValueRow
                      { Id = v.Id, Name = v.Name, Value = v.Value ?? string.Empty, Description = v.Description ?? string.Empty });

        var form = new Grid { Margin                            = new Thickness(16, 16, 16, 0) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(form, 0, "_Name",        _name);
        AddRow(form, 1, "_Values are",  _type);
        AddRow(form, 2, "_Description", _description);

        var flags = new StackPanel { Margin = new Thickness(16, 0, 16, 8) };
        flags.Children.Add(_bitmask);
        flags.Children.Add(_known);
        flags.Children.Add(_numbers);
        foreach (UIElement child in flags.Children) ((FrameworkElement)child).Margin = new Thickness(0, 0, 0, 3);

        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows      = true;
        _grid.CanUserDeleteRows   = true;
        _grid.CanUserResizeRows   = false;
        _grid.HeadersVisibility   = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.SelectionMode       = DataGridSelectionMode.Extended;
        _grid.ItemsSource         = _rows;
        _grid.Margin              = new Thickness(0, 4, 0, 0);
        _grid.Columns.Add(Column("Label", nameof(EnumValueRow.Name),  150));
        _grid.Columns.Add(Column("Value", nameof(EnumValueRow.Value), 110));
        _grid.Columns.Add(new DataGridTextColumn
                          {
                              Header = "Note",
                              Binding = new Binding(nameof(EnumValueRow.Description))
                                        { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                              Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                          });

        var add = new Button { Content = "_Add value", Padding = new Thickness(10, 3, 10, 3) };
        add.Click += (_, _) => AddValueRow();
        var remove = new Button { Content = "_Remove", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        remove.Click += (_, _) => RemoveSelected();
        var gridButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        gridButtons.Children.Add(add);
        gridButtons.Children.Add(remove);

        var valuesBox   = new DockPanel { Margin = new Thickness(16, 0, 16, 0) };
        var valuesTitle = new TextBlock { Text   = "Known values", FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(valuesTitle, Dock.Top);
        DockPanel.SetDock(gridButtons, Dock.Bottom);
        valuesBox.Children.Add(valuesTitle);
        valuesBox.Children.Add(gridButtons);
        valuesBox.Children.Add(_grid);

        var hint = new TextBlock
                   {
                       Text =
                           "Other parts of the Silo refer to this enum as enum:Name, so renaming it breaks those references. Values are stored in a standard form (255, not 0xFF).",
                       TextWrapping = TextWrapping.Wrap,
                       Opacity      = 0.65,
                       Margin       = new Thickness(0, 8, 0, 0)
                   };

        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 84, Padding = new Thickness(14, 4, 14, 4) };
        var cancel = new Button
                     {
                         Content = "Cancel", IsCancel = true, MinWidth = 84, Padding = new Thickness(14, 4, 14, 4),
                         Margin  = new Thickness(8,                                                      0, 0,  0)
                     };
        save.Click += (_, _) => Save();
        var buttons = new StackPanel
                      {
                          Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                          Margin      = new Thickness(0, 12, 0, 0)
                      };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var footer = new StackPanel { Margin = new Thickness(16, 0, 16, 16) };
        footer.Children.Add(_message);
        footer.Children.Add(hint);
        footer.Children.Add(buttons);

        var root = new DockPanel();
        var top  = new StackPanel();
        top.Children.Add(form);
        top.Children.Add(flags);
        DockPanel.SetDock(top,    Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(valuesBox);
        Content = root;

        _type.SelectionChanged += (_, _) => ApplyType();
        ApplyType();

        Loaded += (_, _) => (isNew ? (Control)_name : _grid).Focus();
    }
}