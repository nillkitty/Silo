using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;

namespace Silo.Labs;

/// <summary>
///     A pane of parameters: the Lab's own (the general pane), an aspect's or a tool's. Which lists of parameters it shows, and when the pane is
///     there at all, follows from its <see cref="PaneType" /> and <see cref="Scope" /> (see <see cref="LabModelRules.SourcesFor" />):
///     an aspect pane shows the aspect's, the part's, the subject's and the Lab's parameters, a tool pane the tool's, the part's, the subject's
///     and the Lab's, and the general pane everything that applies. Commands tied to the same owners are buttons at the top.
/// </summary>
public sealed class LabParametersInspector : LabInspector
{
    private readonly ILabModel    _model;
    private readonly string       _id;
    private readonly string       _title;
    private readonly string?      _icon;
    private readonly LabOwner?    _scope;
    private readonly LabPaneType  _type;

    public LabParametersInspector(ILabModel model, string id = InspectorId, string title = "Parameters", string? iconKey = "Settings", LabOwner? scope = null, LabPaneType type = LabPaneType.Static)
    {
        _model = model;
        _id    = id;
        _title = title;
        _icon  = iconKey;
        _scope = scope;
        _type  = type;
    }

    public const string InspectorId = "parameters";

    public override string Id => _id;

    public override string Title => _title;

    public override string? IconKey => _icon;

    public override LabDock Dock => LabDock.Right;

    public override bool VisibleByDefault => false;

    public override LabPaneType PaneType => _type;

    public override LabOwner? Scope => _scope;

    protected override UIElement BuildView() => new LabParametersView(_model, _scope, _type);
}

/// <summary>
///     Edits the lists of parameters that apply to a pane: one editor per parameter by its kind under its list's heading, each list with a
///     button to reset it and a menu of its presets. A change is kept in the Silo as soon as the editor is left (Enter on a line of text).
/// </summary>
internal sealed class LabParametersView : Grid
{
    private readonly ILabModel               _model;
    private readonly LabOwner?               _scope;
    private readonly LabPaneType             _type;
    private readonly StackPanel              _commands = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel              _list     = new() { Margin = new Thickness(10, 4, 10, 10) };
    private readonly List<LabParameterSet>   _watched  = [];
    private          bool                    _rebuilding;
    private          bool                    _committing;
    private          bool                    _pending;

    public LabParametersView(ILabModel model, LabOwner? scope, LabPaneType type)
    {
        _model = model;
        _scope = scope;
        _type  = type;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new WrapPanel { Margin = new Thickness(8, 6, 8, 0) };
        bar.Children.Add(_commands);
        SetRow(bar, 0);
        Children.Add(bar);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _list };
        SetRow(scroll, 1);
        Children.Add(scroll);

        Rebuild();

        // what applies changes with the active part and tool, and a preset loaded from the terminal changes the values
        model.ModelChanged += OnModelChanged;
        Unloaded           += (_, _) => Detach();
        Loaded             += (_, _) =>
                              {
                                  model.ModelChanged -= OnModelChanged;
                                  model.ModelChanged += OnModelChanged;
                                  Schedule();
                              };
    }

    private void Detach()
    {
        _model.ModelChanged -= OnModelChanged;
        foreach (var set in _watched) set.Changed -= OnParametersChanged;
        _watched.Clear();
    }

    private void OnModelChanged(object? sender, EventArgs e) => Schedule();

    private void OnParametersChanged(LabParameter? changed)
    {
        // a single parameter changed by this view's own editor is already showing
        if (changed is not null && _committing) return;

        Schedule();
    }

    private void Schedule()
    {
        if (_pending) return;

        _pending = true;
        Dispatcher.InvokeAsync(() =>
                               {
                                   _pending = false;
                                   Rebuild();
                               }, DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        if (_rebuilding) return;

        _rebuilding = true;
        try
        {
            foreach (var set in _watched) set.Changed -= OnParametersChanged;
            _watched.Clear();

            RebuildCommands();
            _list.Children.Clear();

            var sources = LabModelRules.SourcesFor(_model, _scope, _type);
            if (sources.Count == 0)
            {
                var note = new TextBlock { Text = "Nothing to set here right now.", Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
                note.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
                _list.Children.Add(note);
                return;
            }

            foreach (var source in sources)
            {
                if (!_watched.Contains(source.Set))
                {
                    _watched.Add(source.Set);
                    source.Set.Changed += OnParametersChanged;
                }

                _list.Children.Add(SectionHeader(source));

                string? group = null;
                foreach (var p in source.Set)
                {
                    if (p.Group != group)
                    {
                        group = p.Group;
                        if (group.Length > 0) _list.Children.Add(Heading(group));
                    }

                    _list.Children.Add(Editor(p));
                }
            }
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void RebuildCommands()
    {
        _commands.Children.Clear();
        foreach (var command in LabModelRules.CommandsFor(_model, _scope, _type))
        {
            var button = new Button
                         {
                             Command          = command.Command,
                             CommandParameter = command.Parameter,
                             Padding          = new Thickness(7, 3, 8, 3),
                             Margin           = new Thickness(0, 0, 4, 4),
                             ToolTip          = string.IsNullOrEmpty(command.ToolTip) ? LabTerminal.Strip(command.Header) : command.ToolTip
                         };

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (LabUi.Icon(LabMenus.IconFor(command), 14) is { } glyph)
            {
                glyph.Margin = new Thickness(0, 0, 5, 0);
                content.Children.Add(glyph);
            }

            content.Children.Add(new TextBlock { Text = LabTerminal.Strip(command.Header), VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
            _commands.Children.Add(button);
        }
    }

    private UIElement SectionHeader(LabParameterSource source)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 2), LastChildFill = true };

        bool anySetting = source.Set.Any(p => p.Kind != LabParameterKind.Action);
        if (anySetting)
        {
            var presets = new Button { Content = "Presets ▾", Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Keep these settings under a name, or put a kept set back" };
            presets.Click += (_, _) => ShowPresets(presets, source.Set);
            DockPanel.SetDock(presets, Dock.Right);
            row.Children.Add(presets);

            var reset = new Button { Content = "Reset", Padding = new Thickness(6, 1, 6, 1), ToolTip = "Put every setting in this list back to its default" };
            reset.Click += (_, _) => source.Set.ResetToDefaults();
            DockPanel.SetDock(reset, Dock.Right);
            row.Children.Add(reset);
        }

        string kind = source.Owner.Kind switch
                      {
                          LabOwnerKind.Aspect => "Aspect",
                          LabOwnerKind.Tool   => "Tool",
                          LabOwnerKind.Part   => "Part",
                          LabOwnerKind.Lab    => "Lab",
                          _                   => string.Empty
                      };
        var title = new TextBlock
                    {
                        Text              = kind.Length == 0 ? source.Title : $"{kind}: {source.Title}",
                        FontWeight        = FontWeights.Bold,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming      = TextTrimming.CharacterEllipsis
                    };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        row.Children.Add(title);

        var box = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 3), Child = row };
        box.SetResourceReference(Border.BorderBrushProperty, "Theme.TextMuted");
        return box;
    }

    private void ShowPresets(Button anchor, LabParameterSet set)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        var save = new MenuItem { Header = "Save current as...", Icon = LabUi.Icon("TbSave") };
        save.Click += (_, _) =>
                      {
                          string? name = LabUi.Ask("Preset", $"Keep these {set.Title} settings under the name:");
                          if (string.IsNullOrWhiteSpace(name)) return;

                          try
                          {
                              set.SavePreset(name);
                          }
                          catch (Exception ex)
                          {
                              Log.Warning(ex, "Saving the preset failed");
                              MessageBox.Show(Window.GetWindow(this), ex.Message, "Preset", MessageBoxButton.OK, MessageBoxImage.Warning);
                          }
                      };
        menu.Items.Add(save);

        var names = set.PresetNames();
        if (names.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (string name in names)
            {
                string captured = name;
                var    load     = new MenuItem { Header = captured.Replace("_", "__"), ToolTip = "Put the settings back as this preset has them" };
                load.Click += (_, _) =>
                              {
                                  if (!set.LoadPreset(captured)) Log.Warning($"There is no preset {captured}");
                              };
                menu.Items.Add(load);
            }

            menu.Items.Add(new Separator());
            var delete = new MenuItem { Header = "Delete preset", Icon = LabUi.Icon("TbDelete") };
            foreach (string name in names)
            {
                string captured = name;
                var    item     = new MenuItem { Header = captured.Replace("_", "__") };
                item.Click += (_, _) =>
                              {
                                  if (LabUi.Confirm($"Forget the preset '{captured}'?", "Preset")) set.DeletePreset(captured);
                              };
                delete.Items.Add(item);
            }

            menu.Items.Add(delete);
        }

        menu.IsOpen = true;
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

        if (p.Kind is not (LabParameterKind.Bool or LabParameterKind.Action))
            panel.Children.Add(new TextBlock { Text = p.Title, FontWeight = FontWeights.SemiBold });

        switch (p.Kind)
        {
            case LabParameterKind.Action:
                var run = new Button { Content = p.Title, Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = p.Hint };
                if (p.Linked is { } linked)
                {
                    run.Command          = linked.Command;
                    run.CommandParameter = linked.Parameter;
                }

                panel.Children.Add(run);
                break;

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

        if (p.Description.Length > 0 && p.Kind != LabParameterKind.Action)
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
