using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Media;
using DataBinding = Silo.DbModel.Global.DataBinding;

namespace Silo.Bindings;

/// <summary>
///     The editor for one <see cref="DataBinding" />: name, source, interval, a Test button that takes a reading, and the
///     token to paste into a diagram cell's <c>silo-label</c>.
/// </summary>
public sealed class DataBindingWindow : Window
{
    private static readonly Regex ValidName = new(@"^[A-Za-z0-9_.\-]+$", RegexOptions.Compiled);

    private readonly CheckBox                _enabled;
    private readonly TextBox                 _description;
    private readonly TextBox                 _interval;
    private readonly TextBox                 _name;
    private readonly Func<string, bool>      _nameTaken;
    private readonly ComboBox                _source;
    private readonly TextBlock               _status;
    private readonly TextBlock               _token;
    private readonly DataBinding             _original;

    private DataBindingWindow(DataBinding binding, Func<string, bool> nameTaken)
    {
        _original  = binding;
        _nameTaken = nameTaken;

        Title                 = binding.Id > 0 ? "Edit data binding" : "New data binding";
        SizeToContent         = SizeToContent.Height;
        Width                 = 560;
        ResizeMode            = ResizeMode.NoResize;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _name = new TextBox { Text = binding.Name };
        _name.TextChanged += (_, _) => UpdateToken();

        _source = new ComboBox { IsEditable = true, Text = binding.Source };
        foreach (var example in BindingSources.All.SelectMany(s => s.Examples)) _source.Items.Add(example);

        _interval    = new TextBox { Text = FormatInterval(binding.Interval) };
        _enabled     = new CheckBox { Content = "Sample while the Silo is open", IsChecked = binding.Enabled };
        _description = new TextBox { Text = binding.Description ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44 };

        _token = new TextBlock { FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
        var copy = new Button { Content = "Copy", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += (_, _) => Clipboard.SetText(_token.Text);
        var tokenRow = new StackPanel { Orientation = Orientation.Horizontal };
        tokenRow.Children.Add(_token);
        tokenRow.Children.Add(copy);

        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) };
        var test = new Button { Content = "Test", Padding = new Thickness(14, 3, 14, 3), HorizontalAlignment = HorizontalAlignment.Left };
        test.Click += async (_, _) => await TestAsync();
        var testRow = new StackPanel();
        testRow.Children.Add(test);
        testRow.Children.Add(_status);

        int row = 0;
        AddRow(grid, ref row, "Name", _name);
        AddRow(grid, ref row, "Source", _source);
        AddRow(grid, ref row, "Every (seconds)", _interval);
        AddRow(grid, ref row, "", _enabled);
        AddRow(grid, ref row, "Description", _description);
        AddRow(grid, ref row, "Diagram token", tokenRow);
        AddRow(grid, ref row, "", testRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok      = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(14, 4, 14, 4) };
        var cancel  = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) =>
                    {
                        if (Validate() is { } problem)
                        {
                            _status.Foreground = Brushes.Firebrick;
                            _status.Text       = problem;
                            return;
                        }

                        DialogResult = true;
                    };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, row);
        Grid.SetColumnSpan(buttons, 2);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(buttons);

        Content = grid;
        Loaded += (_, _) =>
                  {
                      _name.Focus();
                      _name.SelectAll();
                  };
        UpdateToken();
    }

    /// <summary>Shows the editor; returns the edited copy, or null if cancelled.</summary>
    /// <param name="binding">The binding to edit (not changed).</param>
    /// <param name="nameTaken">Whether another binding in the Silo already has a name.</param>
    public static DataBinding? Edit(DataBinding binding, Func<string, bool> nameTaken)
    {
        var w = new DataBindingWindow(binding, nameTaken) { Owner = OwnerWindow() };
        if (w.Owner is null) w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog() == true ? w.Result() : null;
    }

    private static Window? OwnerWindow()
    {
        foreach (Window w in Application.Current.Windows)
            if (w.IsActive && w.IsVisible)
                return w;
        return Application.Current.MainWindow is { IsVisible: true } main ? main : null;
    }

    private static void AddRow(Grid grid, ref int row, string label, UIElement editor)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var caption = new TextBlock { Text = label, Margin = new Thickness(0, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(caption, row);
        grid.Children.Add(caption);

        if (editor is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        row++;
    }

    private static string FormatInterval(TimeSpan t)
    {
        return t.TotalSeconds.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private static bool TryParseInterval(string text, out TimeSpan interval)
    {
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double seconds) && seconds > 0)
        {
            interval = TimeSpan.FromSeconds(seconds);
            return true;
        }

        return TimeSpan.TryParse(text, CultureInfo.CurrentCulture, out interval) && interval > TimeSpan.Zero;
    }

    private void UpdateToken()
    {
        _token.Text = "{bind:" + (_name.Text.Trim() is { Length: > 0 } n ? n : "Name") + "}";
    }

    private string? Validate()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0) return "Give the binding a name.";
        if (!ValidName.IsMatch(name)) return "Names may use letters, digits, _ - and . only (they appear inside {bind:...} tokens).";
        if (!string.Equals(name, _original.Name, StringComparison.OrdinalIgnoreCase) && _nameTaken(name))
            return $"This Silo already has a data binding named {name}.";
        if (BindingSources.For(_source.Text.Trim()) is null)
            return "The source needs a known scheme: " + string.Join(", ", BindingSources.All.Select(s => s.Scheme + ":")) + ".";
        if (!TryParseInterval(_interval.Text, out _)) return "The interval should be a number of seconds, e.g. 1 or 0.5.";
        return null;
    }

    private async Task TestAsync()
    {
        string source = _source.Text.Trim();
        _status.Foreground = Brushes.Gray;
        _status.Text       = "Reading...";
        try
        {
            var value = await Task.Run(() =>
                                       {
                                           using var reader = BindingSources.Open(source);
                                           reader.Read(); // rate counters need two readings
                                           Thread.Sleep(500);
                                           return reader.Read();
                                       });
            _status.Text = "Read: " + (value switch
                                       {
                                           null           => "(nothing)",
                                           IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
                                           _              => value.ToString()
                                       });
        }
        catch (Exception ex)
        {
            _status.Foreground = Brushes.Firebrick;
            _status.Text       = ex.Message;
        }
    }

    private DataBinding Result()
    {
        var b = _original.Clone();
        b.Name        = _name.Text.Trim();
        b.Source      = _source.Text.Trim();
        b.Interval    = TryParseInterval(_interval.Text, out var t) ? t : b.Interval;
        b.Enabled     = _enabled.IsChecked == true;
        b.Description = string.IsNullOrWhiteSpace(_description.Text) ? null : _description.Text.Trim();
        b.Modified    = DateTime.UtcNow;
        return b;
    }
}
