using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using Silo.Design;

namespace Silo.Views.SettingsPanes;

/// <summary>
///     One node of Settings in the navigation: a section (with the pane that shows it) or a group of sections. Built by
///     <c>AppNavTree.BuildSettings</c> alongside the nav nodes.
/// </summary>
/// <param name="Title">The node's text.</param>
/// <param name="Path">Where it sits: <c>Settings / Silo Settings / Snapshots</c>.</param>
/// <param name="About">One line on what it holds.</param>
/// <param name="Audience">Who it is for (a group: <see cref="SettingsAudience.Any" />).</param>
/// <param name="Pane">Makes the section's pane; null for a group, or a section not designed yet.</param>
public sealed record SettingsSection(string Title, string Path, string About, SettingsAudience Audience, Func<SettingsPane>? Pane)
{
    /// <summary>The sections and groups under a group, in order.</summary>
    public List<SettingsSection> Children { get; } = [];

    /// <summary>Whether it is a group.</summary>
    public bool IsGroup => Children.Count > 0;

    /// <summary>Its pane: the designed one, or one saying what it will hold.</summary>
    public SettingsPane CreatePane() => Pane?.Invoke() ?? new NotBuiltPane(Title, About, Audience);

    // records compare every member; two sections are the same only when they are the same object
    public bool Equals(SettingsSection? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>
///     The document a Settings node opens: a section's pane, or -- for a group -- the panes of all its sections tiled,
///     under a heading per sub-group. Panes the current User Level does not reach are left out; the page is made again
///     when the level changes.
/// </summary>
public sealed class SettingsPage : UserControl
{
    private readonly StackPanel      _content = new() { Margin = new Thickness(24, 20, 24, 24) };
    private readonly SettingsSection _section;
    private          bool            _hooked;

    private void Rebuild()
    {
        _content.Children.Clear();

        _content.Children.Add(new TextBlock { Text = _section.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        var path = new TextBlock { Text = _section.Path, Margin = new Thickness(0, 0, 0, 2), FontSize = 11.5 };
        path.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        _content.Children.Add(path);
        var about = new TextBlock { Text = _section.About, TextWrapping = TextWrapping.Wrap, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        about.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        _content.Children.Add(about);

        if (!_section.IsGroup)
        {
            if (!_section.Audience.IsShown())
            {
                _content.Children.Add(new TextBlock
                                      {
                                          Text         = $"These settings are for {Who(_section.Audience)}: choose View ▸ User Level ▸ {_section.Audience.MinLevel()} to see them.",
                                          TextWrapping = TextWrapping.Wrap
                                      });
                return;
            }

            var pane = _section.CreatePane();
            pane.MaxWidth            = Math.Max(pane.PreferredWidth, 760);
            pane.HorizontalAlignment = HorizontalAlignment.Left;
            _content.Children.Add(pane);
            return;
        }

        // the group's own sections first, then a heading and the tiles of each sub-group
        AddTiles(_section.Children.Where(c => !c.IsGroup));
        foreach (var group in _section.Children.Where(c => c.IsGroup))
        {
            if (!group.Children.Any(c => c.Audience.IsShown())) continue;

            _content.Children.Add(new TextBlock { Text = group.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6) });
            AddTiles(group.Children.Where(c => !c.IsGroup));
        }
    }

    private void AddTiles(IEnumerable<SettingsSection> sections)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var section in sections.Where(s => s.Audience.IsShown()))
        {
            var pane = section.CreatePane();
            pane.Width             = pane.PreferredWidth;
            pane.Margin            = new Thickness(0, 0, 14, 14);
            pane.VerticalAlignment = VerticalAlignment.Top;
            wrap.Children.Add(pane);
        }

        if (wrap.Children.Count > 0) _content.Children.Add(wrap);
    }

    private static string Who(SettingsAudience audience) => audience switch
                                                            {
                                                                SettingsAudience.Author    => "authors",
                                                                SettingsAudience.Developer => "developers",
                                                                _                          => "everyone"
                                                            };

    private void OnLevelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppDesignLevel.Level)) Dispatcher.BeginInvoke(new Action(Rebuild), DispatcherPriority.Background);
    }

    public SettingsPage(SettingsSection section)
    {
        _section = section;
        Content = new ScrollViewer
                  {
                      VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                      HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                      Content                       = _content
                  };

        Loaded += (_, _) =>
                  {
                      if (!_hooked) AppDesignLevel.Instance.PropertyChanged += OnLevelChanged;
                      _hooked = true;
                      Rebuild();
                  };
        Unloaded += (_, _) =>
                    {
                        AppDesignLevel.Instance.PropertyChanged -= OnLevelChanged;
                        _hooked                                 =  false;
                    };
    }
}
