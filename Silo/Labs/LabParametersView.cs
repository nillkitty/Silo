using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;

namespace Silo.Labs;

/// <summary>The Lab's parameters as a pane: every Lab window has one (Open Settings shows it).</summary>
public sealed class LabParametersInspector : LabInspector
{
    private readonly LabParameterSet _parameters;

    public LabParametersInspector(LabParameterSet parameters)
    {
        _parameters = parameters;
    }

    public const string InspectorId = "parameters";

    public override string Id => InspectorId;

    public override string Title => "Parameters";

    public override string IconKey => "Settings";

    public override LabDock Dock => LabDock.Right;

    public override bool VisibleByDefault => false;

    protected override UIElement BuildView() => new LabParametersView(_parameters);
}

/// <summary>
///     Edits a <see cref="LabParameterSet" />: one editor per parameter by its kind, under its group's heading, and a bar to reset
///     everything and to keep and load presets. A change is kept in the Silo as soon as the editor is left (Enter on a line of text).
/// </summary>
internal sealed class LabParametersView : Grid
{
    private readonly LabParameterSet _parameters;
    private readonly StackPanel      _list = new() { Margin = new Thickness(10, 6, 10, 10) };
    private readonly ComboBox        _presets = new() { MinWidth = 120, IsEditable = false };
    private readonly TextBox         _presetName = new() { MinWidth = 100, ToolTip = "A name for the preset to keep" };
    private          bool            _rebuilding;
    private          bool            _committing;

    public LabParametersView(LabParameterSet parameters)
    {
        _parameters = parameters;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = BuildBar();
        SetRow(bar, 0);
        Children.Add(bar);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _list };
        SetRow(scroll, 1);
        Children.Add(scroll);

        Rebuild();
        RefreshPresets();

        // changes made somewhere else (a preset loaded from the terminal, another window) show here; this view's own edits do not rebuild it
        _parameters.Changed += OnParametersChanged;
        Unloaded            += (_, _) => _parameters.Changed -= OnParametersChanged;
        Loaded              += (_, _) =>
                               {
                                   _parameters.Changed -= OnParametersChanged;
                                   _parameters.Changed += OnParametersChanged;
                               };
    }

    private FrameworkElement BuildBar()
    {
        var bar = new WrapPanel { Margin = new Thickness(8, 6, 8, 2) };

        bar.Children.Add(MakeButton("Reset all", () => _parameters.ResetToDefaults(), "Put every parameter back to its default"));

        bar.Children.Add(new TextBlock { Text = "Preset:", Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(_presets);
        bar.Children.Add(MakeButton("Load", LoadPreset, "Put the parameters to the chosen preset"));
        bar.Children.Add(MakeButton("Delete", DeletePreset, "Forget the chosen preset"));

        _presetName.Margin = new Thickness(10, 0, 4, 0);
        bar.Children.Add(_presetName);
        bar.Children.Add(MakeButton("Save as", SavePreset, "Keep the parameters as they are now under the name"));
        return bar;
    }

    private static Button MakeButton(string text, Action click, string tip)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), ToolTip = tip };
        button.Click += (_, _) => click();
        return button;
    }

    private void OnParametersChanged(LabParameter? changed)
    {
        // a single parameter changed by this view's own editor is already showing
        if (changed is not null && _committing) return;

        Dispatcher.InvokeAsync(() =>
                               {
                                   Rebuild();
                                   RefreshPresets();
                               });
    }

    private void RefreshPresets()
    {
        string? selected = _presets.SelectedItem as string;
        _presets.ItemsSource = _parameters.PresetNames();
        if (selected is not null && _presets.Items.Contains(selected)) _presets.SelectedItem = selected;
    }

    private void LoadPreset()
    {
        if (_presets.SelectedItem is string name && !_parameters.LoadPreset(name)) Log.Warning("There is no preset {Name}", name);
    }

    private void DeletePreset()
    {
        if (_presets.SelectedItem is not string name) return;

        _parameters.DeletePreset(name);
        RefreshPresets();
    }

    private void SavePreset()
    {
        string name = _presetName.Text.Trim();
        if (name.Length == 0) return;

        try
        {
            _parameters.SavePreset(name);
            _presetName.Clear();
            RefreshPresets();
            _presets.SelectedItem = name;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Saving the preset failed");
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Preset", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Rebuild()
    {
        if (_rebuilding) return;

        _rebuilding = true;
        try
        {
            _list.Children.Clear();

            string? group = null;
            foreach (var p in _parameters)
            {
                if (p.Group != group)
                {
                    group = p.Group;
                    if (group.Length > 0) _list.Children.Add(Heading(group));
                }

                _list.Children.Add(Editor(p));
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text.ToUpperInvariant(), FontWeight = FontWeights.SemiBold, FontSize = 11, Margin = new Thickness(0, 12, 0, 2) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        return heading;
    }

    private FrameworkElement Editor(LabParameter p)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        if (p.Kind != LabParameterKind.Bool)
            panel.Children.Add(new TextBlock { Text = p.Title, FontWeight = FontWeights.SemiBold });

        switch (p.Kind)
        {
            case LabParameterKind.Bool:
                var check = new CheckBox { Content = p.Title, IsChecked = p.Bool, FontWeight = FontWeights.SemiBold, ToolTip = p.Hint };
                check.Click += (_, _) => Commit(p, check.IsChecked == true);
                panel.Children.Add(check);
                break;

            case LabParameterKind.Choice:
                var combo = new ComboBox { ItemsSource = p.Choices, SelectedItem = p.Text, ToolTip = p.Hint, Margin = new Thickness(0, 2, 0, 0) };
                combo.SelectionChanged += (_, _) =>
                                          {
                                              if (combo.SelectedItem is string choice) Commit(p, choice);
                                          };
                panel.Children.Add(combo);
                break;

            case LabParameterKind.Flags:
                var flags = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
                var boxes = new List<CheckBox>();
                foreach (string choice in p.Choices)
                {
                    var box = new CheckBox { Content = choice, IsChecked = p.Lines.Contains(choice, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 0, 12, 2) };
                    box.Click += (_, _) => Commit(p, boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToArray());
                    boxes.Add(box);
                    flags.Children.Add(box);
                }

                panel.Children.Add(flags);
                break;

            case LabParameterKind.Lines:
                var lines = new TextBox
                            {
                                Text                          = string.Join(Environment.NewLine, p.Lines),
                                AcceptsReturn                 = true,
                                TextWrapping                  = TextWrapping.NoWrap,
                                MinHeight                     = 52,
                                MaxHeight                     = 180,
                                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                                FontFamily                    = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                                ToolTip                       = p.Hint,
                                Margin                        = new Thickness(0, 2, 0, 0)
                            };
                lines.LostFocus += (_, _) => Commit(p, lines.Text);
                panel.Children.Add(lines);
                break;

            default:
                var line = new TextBox { Text = p.Text, ToolTip = p.Hint, Margin = new Thickness(0, 2, 0, 0) };
                line.LostFocus += (_, _) => Commit(p, line.Text);
                line.KeyDown   += (_, e) =>
                                  {
                                      if (e.Key == Key.Enter) Commit(p, line.Text);
                                  };
                panel.Children.Add(line);
                break;
        }

        if (p.Description.Length > 0)
        {
            var description = new TextBlock { Text = p.Description, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
            description.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
            panel.Children.Add(description);
        }

        return panel;
    }

    private void Commit(LabParameter p, object? value)
    {
        if (_rebuilding) return;

        _committing = true;
        try
        {
            p.Value = value;
        }
        finally
        {
            _committing = false;
        }
    }
}
