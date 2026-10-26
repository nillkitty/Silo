using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;
using Silo.Design;
using Silo.Model;
using Silo.Settings;

namespace Silo.Views.SettingsPanes;

/// <summary>Who a Settings pane is for. Its header is tinted by it, and it is shown only from the matching User Level up.</summary>
public enum SettingsAudience
{
    /// <summary>Everyone (blue).</summary>
    Any,

    /// <summary>Authors: View ▸ User Level ▸ Author or above (pink).</summary>
    Author,

    /// <summary>Developers and debugging: View ▸ User Level ▸ Developer or above (purple).</summary>
    Developer
}

/// <summary>What follows from a <see cref="SettingsAudience" />.</summary>
public static class SettingsAudiences
{
    /// <summary>The lowest User Level that sees it.</summary>
    public static DesignLevel MinLevel(this SettingsAudience audience)
    {
        return audience switch
               {
                   SettingsAudience.Author    => DesignLevel.Author,
                   SettingsAudience.Developer => DesignLevel.Developer,
                   _                          => DesignLevel.User
               };
    }

    /// <summary>Whether the current User Level sees it.</summary>
    public static bool IsShown(this SettingsAudience audience)
    {
        return AppDesignLevel.Instance.IsAtLeast(audience.MinLevel());
    }

    /// <summary>The theme resource key of its header fill (<c>Theming/Theme.*.xaml</c>).</summary>
    public static string FillKey(this SettingsAudience audience) => "Settings." + audience + ".Fill";

    /// <summary>The theme resource key of its border.</summary>
    public static string StrokeKey(this SettingsAudience audience) => "Settings." + audience + ".Stroke";
}

/// <summary>
///     One Settings section as a pane: a rounded box whose header is tinted by its <see cref="Audience" />, holding the
///     section's controls. A pane is shown on its own (the section's page) and, tiled with its siblings, on its group's page
///     (<see cref="SettingsPage" />).
/// </summary>
/// <remarks>
///     Built in code like the other views. The controls are made by <see cref="Build" />, again each time the pane is shown
///     and when the active Silo changes, so they always show what is stored now. Each control writes its value back the
///     moment it is changed; there is no OK/Apply.
/// </remarks>
public abstract class SettingsPane : UserControl
{
    // glyph colours of the mock-up's check lists
    protected static readonly Brush Pink   = Frozen(0xE0, 0x45, 0x7B);
    protected static readonly Brush Purple = Frozen(0x8E, 0x6C, 0xD9);
    protected static readonly Brush Red    = Frozen(0xD9, 0x41, 0x5A);
    protected static readonly Brush Orange = Frozen(0xF0, 0x7A, 0x3A);
    protected static readonly Brush Amber  = Frozen(0xF0, 0xA0, 0x30);
    protected static readonly Brush Green  = Frozen(0x4C, 0xAF, 0x6A);
    protected static readonly Brush Blue   = Frozen(0x5B, 0x8D, 0xEF);

    private readonly StackPanel _body = new() { Margin = new Thickness(14, 10, 14, 14) };
    private          bool       _hooked;

    /// <summary>The section's name, shown in the header.</summary>
    public string Title { get; }

    /// <summary>Who it is for.</summary>
    public SettingsAudience Audience { get; }

    /// <summary>The width the pane takes when it is tiled on a group's page.</summary>
    public virtual double PreferredWidth => 500;

    /// <summary>Whether the pane shows something of the active Silo (and so is built again when it changes).</summary>
    protected virtual bool FollowsActiveSilo => true;

    protected static OpenSilo? ActiveSilo => SiloSettings.ActiveSilo;

    /// <summary>Makes the pane's controls, into <paramref name="body" /> (a vertical stack).</summary>
    protected abstract void Build(Panel body);

    /// <summary>Makes the controls again, from what is stored now.</summary>
    public void Rebuild()
    {
        _body.Children.Clear();
        _body.IsEnabled = true;
        try
        {
            Build(_body);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The settings pane {title} could not be built", Title);
            _body.Children.Clear();
            _body.Children.Add(Note("This section could not be shown: " + ex.Message));
        }
    }

    /// <summary>Makes the controls again once the current input has been handled (safe to call from a control's own event).</summary>
    protected void RebuildLater()
    {
        Dispatcher.BeginInvoke(new Action(Rebuild), DispatcherPriority.Background);
    }

    /// <summary>Greys the whole body out (a read-only Silo, a section only the owner may change), keeping it readable.</summary>
    protected void DisableBody()
    {
        _body.IsEnabled = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked && FollowsActiveSilo && App.Instance is { } app)
        {
            app.ActiveSiloChanged += OnActiveSiloChanged;
            _hooked               =  true;
        }

        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hooked && App.Instance is { } app) app.ActiveSiloChanged -= OnActiveSiloChanged;
        _hooked = false;
    }

    private void OnActiveSiloChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(Rebuild), DispatcherPriority.Background);
    }

    protected SettingsPane(string title, SettingsAudience audience)
    {
        Title    = title;
        Audience = audience;

        var header = new Border
                     {
                         BorderThickness = new Thickness(0, 0, 0, 1),
                         Padding         = new Thickness(8, 4, 8, 4),
                         CornerRadius    = new CornerRadius(4, 4, 0, 0),
                         Child           = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold }
                     };
        header.SetResourceReference(Border.BackgroundProperty,  audience.FillKey());
        header.SetResourceReference(Border.BorderBrushProperty, audience.StrokeKey());
        DockPanel.SetDock(header, Dock.Top);

        var dock = new DockPanel();
        dock.Children.Add(header);
        dock.Children.Add(_body);

        var frame = new Border
                    {
                        BorderThickness = new Thickness(1.5),
                        CornerRadius    = new CornerRadius(5),
                        Child           = dock
                    };
        frame.SetResourceReference(Border.BorderBrushProperty, audience.StrokeKey());
        frame.SetResourceReference(Border.BackgroundProperty,  "Theme.Surface");

        Content   =  frame;
        Loaded    += OnLoaded;
        Unloaded  += OnUnloaded;
    }

    // ---- building blocks ------------------------------------------------------------------------

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>A sub-heading inside the pane.</summary>
    protected static TextBlock Heading(string text, double top = 4)
    {
        return new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 4) };
    }

    /// <summary>A line of muted, wrapping text.</summary>
    protected static TextBlock Note(string text, double left = 0)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(left, 0, 0, 6), FontSize = 11.5 };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        return note;
    }

    /// <summary>A check box that calls <paramref name="set" /> whenever it is ticked or cleared.</summary>
    protected static CheckBox Check(string text, bool value, Action<bool> set, double left = 0, string? toolTip = null)
    {
        return Check(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, value, set, left, toolTip);
    }

    /// <summary>A check box with any content (a glyph and a label, say).</summary>
    protected static CheckBox Check(object content, bool value, Action<bool> set, double left = 0, string? toolTip = null)
    {
        var box = new CheckBox
                  {
                      Content                  = content,
                      IsChecked                = value,
                      Margin                   = new Thickness(left, 2, 0, 2),
                      VerticalContentAlignment = VerticalAlignment.Center,
                      ToolTip                  = toolTip
                  };
        box.Checked   += (_, _) => set(true);
        box.Unchecked += (_, _) => set(false);
        return box;
    }

    /// <summary>A check-list line: a coloured glyph (<c>♥</c>, <c>■</c>) and a label.</summary>
    protected static CheckBox GlyphCheck(string glyph, Brush glyphBrush, string text, bool value, Action<bool> set)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, Foreground = glyphBrush, Margin = new Thickness(0, 0, 6, 0), FontSize = 11 });
        content.Children.Add(new TextBlock { Text = text });
        var box = Check(content, value, set);
        box.Margin = new Thickness(0, 1, 0, 1);
        return box;
    }

    /// <summary>Enables <paramref name="dependents" /> only while <paramref name="master" /> is ticked (and enabled).</summary>
    protected static void DependOn(CheckBox master, params UIElement[] dependents)
    {
        void Apply()
        {
            bool on = master.IsChecked == true && master.IsEnabled;
            foreach (var d in dependents) d.IsEnabled = on;
        }

        master.Checked          += (_, _) => Apply();
        master.Unchecked        += (_, _) => Apply();
        master.IsEnabledChanged += (_, _) => Apply();
        Apply();
    }

    /// <summary>
    ///     A button. With no <paramref name="click" /> it is disabled, and its tooltip says why
    ///     (<paramref name="unavailable" />).
    /// </summary>
    protected static Button MakeButton(string text, Action? click, string? unavailable = null, string? toolTip = null)
    {
        var button = new Button
                     {
                         Content             = text,
                         Padding             = new Thickness(10, 2, 10, 2),
                         Margin              = new Thickness(0, 0, 0, 4),
                         MinWidth            = 90,
                         HorizontalAlignment = HorizontalAlignment.Stretch,
                         HorizontalContentAlignment = HorizontalAlignment.Left
                     };
        if (click is null)
        {
            button.IsEnabled = false;
            button.ToolTip   = unavailable ?? "Not available yet.";
            ToolTipService.SetShowOnDisabled(button, true);
        }
        else
        {
            button.Click   += (_, _) => Run(text, click);
            button.ToolTip =  toolTip;
        }

        return button;
    }

    /// <summary>Runs a button's action, showing what went wrong instead of letting it escape.</summary>
    protected static void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            App.Alert(ex, what.TrimEnd('.', '…'));
        }
    }

    /// <summary>A list box of a fixed height.</summary>
    protected static ListBox MakeList(double height = 70)
    {
        return new ListBox { Height = height, Margin = new Thickness(0, 0, 0, 6) };
    }

    /// <summary>Something on the left (a list) and a column of buttons on its right.</summary>
    protected static Grid WithButtons(UIElement left, params Button[] buttons)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (left is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 10, 0);
        grid.Children.Add(left);

        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        foreach (var b in buttons) column.Children.Add(b);
        Grid.SetColumn(column, 1);
        grid.Children.Add(column);
        return grid;
    }

    /// <summary>A labelled sub-box (the mock-up's "Tooltip Aspects", "Customizations"...): a tinted header over its content.</summary>
    protected static Border Box(string title, SettingsAudience tint, UIElement content, double left = 0)
    {
        var header = new Border
                     {
                         BorderThickness = new Thickness(0, 0, 0, 1),
                         Padding         = new Thickness(6, 2, 6, 2),
                         CornerRadius    = new CornerRadius(3, 3, 0, 0),
                         Child           = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 11.5 }
                     };
        header.SetResourceReference(Border.BackgroundProperty,  tint.FillKey());
        header.SetResourceReference(Border.BorderBrushProperty, tint.StrokeKey());
        DockPanel.SetDock(header, Dock.Top);

        if (content is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(6, 4, 6, 4);

        var dock = new DockPanel();
        dock.Children.Add(header);
        dock.Children.Add(content);

        var box = new Border
                  {
                      BorderThickness     = new Thickness(1.5),
                      CornerRadius        = new CornerRadius(4),
                      Margin              = new Thickness(left, 4, 0, 10),
                      HorizontalAlignment = HorizontalAlignment.Left,
                      MinWidth            = 180,
                      Child               = dock
                  };
        box.SetResourceReference(Border.BorderBrushProperty, "Theme.BorderStrong");
        return box;
    }

    /// <summary>A yellow banner with a message and, optionally, a button.</summary>
    protected static Border Banner(string text, Button? button = null)
    {
        var dock = new DockPanel();
        if (button is not null)
        {
            button.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(button, Dock.Right);
            dock.Children.Add(button);
        }

        dock.Children.Add(new TextBlock { Text = "⚠  " + text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });

        var banner = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 6, 4), Margin = new Thickness(0, 8, 0, 4), Child = dock };
        banner.SetResourceReference(Border.BackgroundProperty,  "Settings.Warn.Fill");
        banner.SetResourceReference(Border.BorderBrushProperty, "Settings.Warn.Stroke");
        return banner;
    }

    /// <summary>A horizontal rule.</summary>
    protected static Border Rule()
    {
        var rule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
        rule.SetResourceReference(Border.BackgroundProperty, "Theme.BorderStrong");
        return rule;
    }

    /// <summary>A label and a control on one line.</summary>
    protected static Grid Row(string label, UIElement control, double labelWidth = 170, double left = 0)
    {
        var grid = new Grid { Margin = new Thickness(left, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>A whole-number box that writes back <paramref name="set" /> when a valid value in range is typed.</summary>
    protected static TextBox NumberBox(int value, int min, int max, Action<int> set)
    {
        int current = value;
        var box     = new TextBox { Text = value.ToString(), Width = 70, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = $"{min} to {max}" };
        box.LostKeyboardFocus += (_, _) =>
                                 {
                                     if (int.TryParse(box.Text.Trim(), out int n))
                                     {
                                         current  = Math.Clamp(n, min, max);
                                         box.Text = current.ToString();
                                         set(current);
                                     }
                                     else
                                         box.Text = current.ToString();
                                 };
        box.KeyDown += (_, e) =>
                       {
                           if (e.Key == System.Windows.Input.Key.Enter) box.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
                       };
        return box;
    }

    /// <summary>A two-column, read-only list (name and value).</summary>
    protected static ListView PairList(double height, string nameHeader, string valueHeader, double nameWidth = 200)
    {
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = nameHeader,  DisplayMemberBinding = new System.Windows.Data.Binding("Name"),  Width = nameWidth });
        view.Columns.Add(new GridViewColumn { Header = valueHeader, DisplayMemberBinding = new System.Windows.Data.Binding("Value"), Width = 200 });
        return new ListView { View = view, Height = height, Margin = new Thickness(0, 0, 0, 6) };
    }
}

/// <summary>A name and a value, for <see cref="SettingsPane.PairList" />.</summary>
public sealed record SettingsPair(string Name, string Value, object? Tag = null);

/// <summary>
///     A pane of settings kept in the active Silo. With no Silo open it says so; when the Silo cannot be changed (it was
///     opened read-only, or the section is the owner's and the person is not) its controls are shown greyed out.
/// </summary>
public abstract class SiloSettingsPane(string title, SettingsAudience audience) : SettingsPane(title, audience)
{
    /// <summary>Whether only the Silo's owner may change these settings.</summary>
    protected virtual bool OwnerOnly => false;

    /// <summary>Makes the controls of the open Silo's settings.</summary>
    protected abstract void BuildFor(OpenSilo silo, Panel body);

    protected sealed override void Build(Panel body)
    {
        if (ActiveSilo is not { } silo)
        {
            body.Children.Add(Note("No Silo is open. These settings are kept in the Silo itself: open one to see them."));
            return;
        }

        BuildFor(silo, body);

        if (!silo.IsWritable)
        {
            body.Children.Insert(0, Note("This Silo is open read-only, so its settings cannot be changed."));
            DisableBody();
        }
        else if (OwnerOnly && !silo.IsOwner)
        {
            body.Children.Insert(0, Note("Only the Silo's owner can change these settings."));
            DisableBody();
        }
    }
}

/// <summary>The pane of a section whose settings have not been designed yet: what it will hold.</summary>
public sealed class NotBuiltPane(string title, string about, SettingsAudience audience) : SettingsPane(title, audience)
{
    protected override bool FollowsActiveSilo => false;

    protected override void Build(Panel body)
    {
        body.Children.Add(Note(about));
        body.Children.Add(Note("This settings section is not built yet."));
    }
}
