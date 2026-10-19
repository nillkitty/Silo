using System.Windows.Controls;
using System.Windows.Media;
using SyntaxKind = Silo.DbModel.Meta.SyntaxKind;
using Silo.Providers.Definitions;

namespace Silo.Definitions;

/// <summary>
///     The New Syntax / Edit Syntax dialog (modal): a name (and optional alias), whether the syntax is built on one of Silo's parsers or on a
///     pattern, an example, and a place to try values out -- it says at once whether a value is valid and what it becomes. Save checks the definition
///     (<see cref="SyntaxRules" />) and writes through the Silo's <see cref="ISyntaxStore" />.
/// </summary>
public sealed class SyntaxWindow : Window
{
    private readonly TextBox     _name        = new();
    private readonly TextBox     _alias       = new();
    private readonly TextBox     _description = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 48, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly RadioButton _parserKind  = new() { Content = "Built-in _parser", GroupName = "kind" };
    private readonly RadioButton _patternKind = new() { Content = "Pa_ttern (regular expression)", GroupName = "kind", Margin = new Thickness(16, 0, 0, 0) };
    private readonly ComboBox    _parser      = new();
    private readonly TextBlock   _parserHint  = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
    private readonly TextBox     _pattern     = new() { FontFamily = new FontFamily("Consolas"), AcceptsReturn = false };
    private readonly TextBox     _example     = new();
    private readonly TextBox     _try         = new();
    private readonly TextBlock   _result      = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock   _message     = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ISyntaxStore _store;
    private readonly SyntaxRecord _original;
    private string?               _autoExample;

    /// <summary>The syntax as it was saved, once the dialog was closed with Save.</summary>
    public SyntaxRecord? Saved { get; private set; }

    /// <summary>Shows the dialog: a new syntax (no <paramref name="editId" />) or that syntax. Returns it as saved, or null when cancelled.</summary>
    public static SyntaxRecord? Prompt(ISyntaxStore store, int? editId)
    {
        SyntaxRecord original = editId is { } id
                                    ? store.GetSyntax(id) ?? throw new KeyNotFoundException("That syntax is no longer in the Silo.")
                                    : SyntaxRecord.ForParser(string.Empty, SyntaxParsers.All[0].Key);

        var window = new SyntaxWindow(store, original);
        return window.ShowDialog() == true ? window.Saved : null;
    }

    private SyntaxWindow(ISyntaxStore store, SyntaxRecord original)
    {
        _store    = store;
        _original = original;
        bool isNew = original.Id == 0;

        Title                 = isNew ? "New syntax" : "Edit syntax";
        Width                 = 540;
        SizeToContent         = SizeToContent.Height;
        ResizeMode            = ResizeMode.NoResize;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner                 = Application.Current?.MainWindow is { IsVisible: true } w ? w : null;
        if (Owner is null) WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _parser.ItemsSource       = SyntaxParsers.All.Select(p => new { p.Key, Text = $"{p.Title}  ({p.Key})" }).ToList();
        _parser.SelectedValuePath = "Key";
        _parser.DisplayMemberPath = "Text";

        _name.Text         = original.Name;
        _alias.Text        = original.Alias       ?? string.Empty;
        _description.Text  = original.Description ?? string.Empty;
        _pattern.Text      = original.Pattern     ?? string.Empty;
        _example.Text      = original.Example     ?? string.Empty;
        _parserKind.IsChecked  = original.Kind == SyntaxKind.Parser;
        _patternKind.IsChecked = original.Kind == SyntaxKind.Pattern;
        _parser.SelectedValue  = SyntaxParsers.Find(original.Parser)?.Key ?? SyntaxParsers.All[0].Key;
        _autoExample           = original.Kind == SyntaxKind.Parser && SyntaxParsers.Find(original.Parser) is { } own && own.Example == original.Example ? own.Example : null;

        var grid = new Grid { Margin = new Thickness(16, 16, 16, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var kinds = new StackPanel { Orientation = Orientation.Horizontal };
        kinds.Children.Add(_parserKind);
        kinds.Children.Add(_patternKind);

        var parserBox = new StackPanel();
        parserBox.Children.Add(_parser);
        parserBox.Children.Add(_parserHint);

        AddRow(grid, 0, "_Name",        _name);
        AddRow(grid, 1, "A_lias",       _alias);
        AddRow(grid, 2, "_Description", _description);
        AddRow(grid, 3, "_Built on",    kinds);
        AddRow(grid, 4, "Pars_er",      parserBox);
        AddRow(grid, 5, "Pa_ttern",     _pattern);
        AddRow(grid, 6, "_Example",     _example);

        var tryTitle = new TextBlock { Text = "Try it", FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 4, 16, 4) };
        var tryBox   = new StackPanel { Margin = new Thickness(16, 0, 16, 0) };
        _try.AcceptsReturn = true;
        _try.Height        = 48;
        _try.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        tryBox.Children.Add(_try);
        _result.Margin = new Thickness(0, 6, 0, 0);
        tryBox.Children.Add(_result);

        var hint = new TextBlock
                   {
                       Text         = "Other parts of the Silo refer to this syntax as syntax:Name (or by its alias). A pattern has to match the whole value.",
                       TextWrapping = TextWrapping.Wrap,
                       Opacity      = 0.65,
                       Margin       = new Thickness(16, 10, 16, 0)
                   };

        var save   = new Button { Content = "Save", IsDefault = true, MinWidth = 84, Padding = new Thickness(14, 4, 14, 4) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => Save();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16, 12, 16, 16) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var root = new StackPanel();
        root.Children.Add(grid);
        root.Children.Add(tryTitle);
        root.Children.Add(tryBox);
        root.Children.Add(new StackPanel { Margin = new Thickness(16, 0, 16, 0), Children = { _message } });
        root.Children.Add(hint);
        root.Children.Add(buttons);
        Content = root;

        _parserKind.Checked  += (_, _) => Refresh();
        _patternKind.Checked += (_, _) => Refresh();
        _parser.SelectionChanged += (_, _) => OnParserChanged();
        _pattern.TextChanged     += (_, _) => Evaluate();
        _try.TextChanged         += (_, _) => Evaluate();
        Refresh();

        Loaded += (_, _) => (isNew ? (Control)_name : _try).Focus();
    }

    private bool UsesParser => _parserKind.IsChecked == true;

    private string? SelectedParserKey => _parser.SelectedValue as string;

    /// <summary>Enables the box for the kind chosen and leaves the other alone (its text is kept in case the person changes their mind).</summary>
    private void Refresh()
    {
        _parser.IsEnabled  = UsesParser;
        _pattern.IsEnabled = !UsesParser;
        ShowParserHint();
        Evaluate();
    }

    private void OnParserChanged()
    {
        // an example that was only filled in from the parser before is replaced by the new parser's; one the person wrote stays
        if (SyntaxParsers.Find(SelectedParserKey) is { } parser && (string.IsNullOrEmpty(_example.Text) || _example.Text == _autoExample))
        {
            _example.Text = parser.Example;
            _autoExample  = parser.Example;
        }

        ShowParserHint();
        Evaluate();
    }

    private void ShowParserHint()
    {
        _parserHint.Text = SyntaxParsers.Find(SelectedParserKey) is { } p ? $"{p.Description} Becomes a {p.ValueType.Name}." : string.Empty;
    }

    /// <summary>The syntax as the dialog shows it now (not yet checked).</summary>
    private SyntaxRecord Draft()
    {
        return UsesParser
                   ? new SyntaxRecord(_original.Id, _name.Text, _alias.Text, _description.Text, SyntaxKind.Parser, SelectedParserKey, null, _example.Text, _original.Flags)
                   : new SyntaxRecord(_original.Id, _name.Text, _alias.Text, _description.Text, SyntaxKind.Pattern, null, _pattern.Text, _example.Text, _original.Flags);
    }

    /// <summary>Tries the "Try it" text against the syntax as it is now, and says what happens.</summary>
    private void Evaluate()
    {
        if (_result is null || _try is null) return; // events fire while the dialog is still being built

        if (!StoredSyntax.TryCreate(SyntaxRules.Normalize(Draft()), out var syntax, out string? buildError))
        {
            Say(buildError ?? "The syntax cannot be built yet.", Brushes.IndianRed);
            return;
        }

        if (_try.Text.Length == 0)
        {
            Say("Type a value above to see whether it is valid and what it becomes.", null);
            return;
        }

        if (syntax!.TryParse(_try.Text, out object? value, out string? error))
        {
            string shown = syntax.GetString(value!);
            Say($"Valid. Becomes a {value!.GetType().Name}; stored as: {(shown.Length > 200 ? shown[..200] + "..." : shown)}", Brushes.SeaGreen);
        }
        else
        {
            Say("Not valid: " + error, Brushes.IndianRed);
        }
    }

    private void Say(string text, Brush? brush)
    {
        _result.Text = text;
        if (brush is null) _result.ClearValue(TextBlock.ForegroundProperty);
        else _result.Foreground = brush;
    }

    private void Save()
    {
        var draft    = Draft();
        var others   = _store.GetSyntaxes().Where(s => s.Id != _original.Id);
        var problems = SyntaxRules.Validate(draft, others);
        if (problems.Count > 0)
        {
            ShowMessage(string.Join(Environment.NewLine, problems));
            return;
        }

        try
        {
            Saved        = _store.SaveSyntax(draft);
            DialogResult = true;
        }
        catch (DefinitionException ex)
        {
            ShowMessage(ex.Message);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "The syntax could not be saved");
            ShowMessage("The syntax could not be saved: " + ex.Message);
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

        var text = new Label { Content = label, Target = field is Panel ? null : field, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(0, 3, 12, 0) };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);

        if (field is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);

        grid.Children.Add(text);
        grid.Children.Add(field);
    }
}
