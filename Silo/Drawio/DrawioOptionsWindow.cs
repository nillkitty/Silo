using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using Silo.Design;

namespace Silo.Drawio;

/// <summary>
///     The Author-level control panel for a diagram pane: every <see cref="DrawioOptions" /> property
///     as a control (generated from the options' metadata, so a new option appears here by being
///     declared), grouped by category, plus which profile the pane uses and whether edits apply to the
///     whole profile or only to this one diagram. Changes take effect immediately.
/// </summary>
/// <remarks>
///     Options above the current <see cref="DesignLevel" /> are hidden; raise the level (View -> User
///     Level) to reach them. The runtime section (which draw.io release to download, from where) needs the
///     Developer level.
/// </remarks>
public sealed class DrawioOptionsWindow : Window
{
    private readonly List<Action>   _refreshers = [];
    private readonly StackPanel     _body       = new();
    private readonly DrawioView     _view;
    private          bool           _loading;
    private          bool           _perDiagram;
    private readonly RadioButton    _profileScope;
    private readonly RadioButton    _diagramScope;
    private readonly ComboBox       _profilePicker;

    private DrawioOptionsWindow(DrawioView view)
    {
        _view  = view;
        Title  = "Diagram options";
        Width  = 520;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar         = false;

        _perDiagram = view.Override is not null;

        var root = new DockPanel { Margin = new Thickness(10) };

        // header: profile + scope
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);

        var profileRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        profileRow.Children.Add(new TextBlock { Text = "Profile:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _profilePicker = new ComboBox { MinWidth = 200, ItemsSource = DrawioProfiles.Instance.All.Select(p => p.Name).ToList(), SelectedItem = view.Profile.Name };
        _profilePicker.SelectionChanged += (_, _) =>
                                           {
                                               if (_loading || _profilePicker.SelectedItem is not string name || name == _view.Profile.Name) return;
                                               _view.SetProfile(name);
                                               Reload();
                                           };
        profileRow.Children.Add(_profilePicker);
        header.Children.Add(profileRow);

        header.Children.Add(new TextBlock
                            {
                                Text         = view.Profile.Description,
                                Opacity      = 0.7,
                                TextWrapping = TextWrapping.Wrap,
                                Margin       = new Thickness(0, 0, 0, 6)
                            });

        _profileScope = new RadioButton { Content = "Changes apply to the whole profile (every diagram that uses it)", GroupName = "scope", IsChecked = !_perDiagram };
        _diagramScope = new RadioButton { Content = "Changes apply to this diagram only", GroupName = "scope", IsChecked = _perDiagram };
        _profileScope.Checked += (_, _) => SwitchScope(false);
        _diagramScope.Checked += (_, _) => SwitchScope(true);
        header.Children.Add(_profileScope);
        header.Children.Add(_diagramScope);
        root.Children.Add(header);

        // footer
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(MakeButton("Reset to defaults", ResetDefaults));
        footer.Children.Add(MakeButton("Reload editor", async () => await _view.ReloadEditorAsync()));
        footer.Children.Add(MakeButton("Close", Close));
        root.Children.Add(footer);

        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body });
        Content = root;

        Silo.Ui.Windows.WindowPlacements.Track(this, App.Instance?.ActiveSilo, Silo.Ui.Windows.WindowPlacements.Dialog, "silo:dialog/diagram-options");

        Build();
    }

    /// <summary>Opens the options window for a pane.</summary>
    public static void ShowFor(DrawioView view)
    {
        new DrawioOptionsWindow(view) { Owner = GetWindow(view) }.Show();
    }

    private DrawioOptions Target => _perDiagram ? _view.BeginOverride() : _view.Profile.Options;

    private static Button MakeButton(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private void SwitchScope(bool perDiagram)
    {
        if (_loading || perDiagram == _perDiagram) return;

        _perDiagram = perDiagram;
        if (!perDiagram) _view.ClearOverride();
        else _view.BeginOverride();
        Reload();
    }

    private void ResetDefaults()
    {
        if (_perDiagram) _view.ClearOverride();
        else _view.Profile.ResetToDefaults();

        _perDiagram = false;
        Reload();
    }

    private void Reload()
    {
        _loading = true;
        try
        {
            _profileScope.IsChecked = !_perDiagram;
            _diagramScope.IsChecked = _perDiagram;
            _profilePicker.SelectedItem = _view.Profile.Name;
            foreach (var r in _refreshers) r();
        }
        finally
        {
            _loading = false;
        }
    }

    private void Commit(Action<DrawioOptions> edit)
    {
        if (_loading) return;

        edit(Target);

        if (_perDiagram) _view.NotifyOverrideChanged();
        else _view.Profile.NotifyChanged();
    }

    // ---- generated controls ------------------------------------------------------------------

    private void Build()
    {
        var level  = AppDesignLevel.Instance.Level;
        int hidden = 0;

        foreach (var group in DrawioOptionDescriptor.All.GroupBy(d => d.Meta.Category))
        {
            var visible = group.Where(d => d.Meta.MinLevel <= level).ToList();
            hidden += group.Count() - visible.Count;
            if (visible.Count == 0) continue;

            var panel = new StackPanel { Margin = new Thickness(6) };
            foreach (var d in visible) panel.Children.Add(MakeRow(d));

            _body.Children.Add(new Expander { Header = group.Key, Content = panel, IsExpanded = group.Key == DrawioOptions.CatInterface, Margin = new Thickness(0, 0, 0, 4) });
        }

        if (level >= DesignLevel.Developer) _body.Children.Add(BuildRuntimeSection());

        if (hidden > 0)
            _body.Children.Add(new TextBlock
                               {
                                   Text         = $"{hidden} more option(s) are available at a higher user level (View -> User Level).",
                                   Opacity      = 0.6,
                                   FontStyle    = FontStyles.Italic,
                                   TextWrapping = TextWrapping.Wrap,
                                   Margin       = new Thickness(6, 8, 6, 0)
                               });
    }

    private UIElement MakeRow(DrawioOptionDescriptor d)
    {
        switch (d.Kind)
        {
            case DrawioOptionDescriptor.ValueKind.Bool:
            {
                var box = new CheckBox { Content = d.Meta.Title, ToolTip = d.Meta.Description, Margin = new Thickness(0, 2, 0, 2) };
                box.Checked   += (_, _) => Commit(o => d.Set(o, true));
                box.Unchecked += (_, _) => Commit(o => d.Set(o, false));
                _refreshers.Add(() => box.IsChecked = (bool)(d.Get(Target) ?? false));
                _refreshers[^1]();
                return box;
            }

            case DrawioOptionDescriptor.ValueKind.Enum:
            {
                var combo = new ComboBox { ItemsSource = Enum.GetValues(d.Type), MinWidth = 140 };
                combo.SelectionChanged += (_, _) =>
                                          {
                                              if (combo.SelectedItem is { } v) Commit(o => d.Set(o, v));
                                          };
                _refreshers.Add(() => combo.SelectedItem = d.Get(Target));
                _refreshers[^1]();
                return Labeled(d, combo);
            }

            default:
            {
                var text = new TextBox { MinWidth = 220, ToolTip = d.Meta.Description };
                void CommitText()
                {
                    if (_loading) return;

                    bool ok = false;
                    Commit(o => ok = d.TrySetText(o, text.Text));
                    if (!ok) text.Text = d.GetText(Target);
                }

                text.LostFocus += (_, _) => CommitText();
                text.KeyDown   += (_, e) =>
                                  {
                                      if (e.Key == Key.Enter) CommitText();
                                  };
                _refreshers.Add(() => text.Text = d.GetText(Target));
                _refreshers[^1]();
                return Labeled(d, text);
            }
        }
    }

    private static UIElement Labeled(DrawioOptionDescriptor d, FrameworkElement control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), ToolTip = d.Meta.Description };
        row.Children.Add(new TextBlock { Text = d.Meta.Title + ":", Width = 200, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(control);
        return row;
    }

    private UIElement BuildRuntimeSection()
    {
        var s     = DrawioRuntime.Settings;
        var panel = new StackPanel { Margin = new Thickness(6) };

        panel.Children.Add(new TextBlock
                           {
                               Text         = $"Installed to: {DrawioRuntime.InstallDirectory}  ({(DrawioRuntime.IsInstalled ? "present" : "not downloaded")}). " +
                                              "Changes to the release or URLs take effect the next time a pane starts.",
                               TextWrapping = TextWrapping.Wrap,
                               Opacity      = 0.7,
                               Margin       = new Thickness(0, 0, 0, 6)
                           });

        var auto = new CheckBox { Content = "Download automatically on first use", IsChecked = s.AutoInstall, Margin = new Thickness(0, 2, 0, 2) };
        auto.Checked   += (_, _) => { s.AutoInstall = true; DrawioRuntime.SaveSettings(); };
        auto.Unchecked += (_, _) => { s.AutoInstall = false; DrawioRuntime.SaveSettings(); };
        panel.Children.Add(auto);

        panel.Children.Add(RuntimeField("Release", () => s.Version, v => s.Version = v));
        panel.Children.Add(RuntimeField("draw.war URL", () => s.WarUrl, v => s.WarUrl = v));
        panel.Children.Add(RuntimeField("Source archive URL", () => s.SourceUrl, v => s.SourceUrl = v));
        panel.Children.Add(RuntimeField("Pinned SHA-256", () => s.Sha256, v => s.Sha256 = v));

        return new Expander { Header = "Runtime (draw.io download)", Content = panel, Margin = new Thickness(0, 0, 0, 4) };
    }

    private static UIElement RuntimeField(string title, Func<string> get, Action<string> set)
    {
        var text = new TextBox { Text = get(), MinWidth = 260 };
        void Save()
        {
            string v = text.Text.Trim();
            if (v == get()) return;
            set(v);
            DrawioRuntime.SaveSettings();
        }

        text.LostFocus += (_, _) => Save();
        text.KeyDown   += (_, e) =>
                          {
                              if (e.Key == Key.Enter) Save();
                          };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock { Text = title + ":", Width = 140, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(text);
        return row;
    }
}
