using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Silo.Converters;
using Silo.Labs.Menus;
using Silo.Toolbar;

namespace Silo.Labs;

/// <summary>
///     Lays its children out in equal-width columns (as many as fit), so a list of check boxes lines up as a grid instead
///     of ragged
///     wrapped rows. The column is as wide as the widest child.
/// </summary>
internal sealed class EqualColumnPanel : Panel
{
    private double _cell;
    private int    _columns = 1;
    private double _rowHeight;

    /// <summary>The most columns to use (0: as many as fit).</summary>
    public int MaxColumns { get; set; }

    protected override Size MeasureOverride(Size available)
    {
        double width  = 0;
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width  = Math.Max(width,  child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        int count = InternalChildren.Count;
        if (count == 0 || width <= 0)
        {
            _columns = 1;
            _cell    = 0;
            return default;
        }

        int columns = double.IsInfinity(available.Width) ? count : Math.Max(1, (int)(available.Width / width));
        columns = Math.Min(columns, count);
        if (MaxColumns > 0) columns = Math.Min(columns, MaxColumns);
        _columns   = columns;
        _cell      = width;
        _rowHeight = height;
        int rows = (count + columns - 1) / columns;
        return new Size(columns          * width, rows * height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(i % _columns * _cell, i / _columns * _rowHeight, _cell, _rowHeight));
            i++;
        }

        int count = InternalChildren.Count;
        return count == 0 ? default : new Size(_columns * _cell, (count + _columns - 1) / _columns * _rowHeight);
    }
}

/// <summary>
///     Names the icon a button gets when its caller gave none, from the words on it, so every button in the Labs carries
///     an icon and its text
///     (never one without the other).
/// </summary>
internal static class LabIcons
{
    private static readonly (string[] Words, string Icon)[] Map =
    [
        (["new", "add", "insert", "create", "plus"], "Plus"),
        (["open", "load", "import"], "TbOpen"),
        (["save as"], "TbSaveAs"),
        (["save", "keep", "store"], "TbSave"),
        (["copy", "duplicate", "clone"], "TbCopy"),
        (["cut"], "TbCut"),
        (["paste"], "TbPaste"),
        (["delete", "remove", "discard", "drop"], "TbDelete"),
        (["clear", "reset", "empty"], "Clear"),
        (["undo", "back"], "Undo"),
        (["redo", "forward"], "TbRedo"),
        (["run all", "test all", "all tests"], "RunAll"),
        (["failing"], "RunFailing"),
        (["run", "play", "start", "go", "apply", "execute", "try"], "Run"),
        (["stop", "cancel", "halt"], "Stop"),
        (["pause"], "Pause"),
        (["record", "capture"], "Record"),
        (["build"], "Build"),
        (["compile"], "Compile"),
        (["export", "download", "write", "send", "generate", "render"],
         "TbExport"),
        (["find", "search", "next", "previous", "locate", "match"], "Search"),
        (["refresh", "reload", "sync", "update", "rescan"], "Refresh"),
        (["check", "validate", "verify", "ok", "accept", "approve"], "Check"),
        (["settings", "options", "preferences", "configure"], "Sliders"),
        (["template", "built in", "library", "example", "sample"],
         "Template"),
        (["up", "earlier", "raise"], "ArrowUp"),
        (["down", "later", "lower"], "ArrowDown"),
        (["indent", "into"], "Indent"),
        (["outdent", "out of"], "Outdent"),
        (["separator", "line"], "Separator"),
        (["submenu", "sub"], "Submenu"),
        (["select all", "all"], "TbSelectAll"),
        (["help", "explain", "about", "what"], "TbHelp"),
        (["link", "join", "connect"], "Link"),
        (["lock", "protect"], "Lock"),
        (["hide", "close"], "Hide"),
        (["rename", "edit", "change", "set"], "Property")
    ];

    /// <summary>The key of an icon that fits the text on a button.</summary>
    public static string For(string? text)
    {
        string t = (text ?? string.Empty).Replace("_", string.Empty)
                                         .Replace("&", string.Empty)
                                         .Replace("…", string.Empty)
                                         .Replace(".", string.Empty)
                                         .Trim()
                                         .ToLowerInvariant();
        foreach (var (words, icon) in Map)
        {
            foreach (string word in words)
                if (t == word || t.StartsWith(word + " ", StringComparison.Ordinal) || t.EndsWith(" " + word, StringComparison.Ordinal) ||
                    (word.Contains(' ') && t.Contains(word, StringComparison.Ordinal)))
                    return icon;
        }

        return "Command";
    }
}

internal static partial class LabUi
{
    /// <summary>An icon of the app's icon set, drawn in the inherited foreground color; null when there is no such icon.</summary>
    public static AppIcon? Icon(string? key, double size = 16)
    {
        var geometry = ToolbarIcons.Find(key);
        return geometry is null ? null : new AppIcon { Icon = geometry, Size = size, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>
    ///     A button with an icon and its text. No icon is named: one that fits the text is used, so no button is just
    ///     text.
    /// </summary>
    public static Button Tool(string text, string? icon, Action click, string? tip = null, bool enabled = true)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (Icon(icon ?? LabIcons.For(text), 14) is { } glyph)
        {
            glyph.Margin = new Thickness(0, 0, 5, 0);
            content.Children.Add(glyph);
        }

        content.Children.Add(new TextBlock { Text = text.Replace("_", string.Empty), VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
                     {
                         Content = content, Padding = new Thickness(7, 2, 8, 2), Margin = new Thickness(2, 0, 2, 0),
                         ToolTip = tip ?? text.Replace("_", string.Empty), IsEnabled = enabled
                     };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A button that is just an icon (the tip says what it does).</summary>
    public static Button IconButton(string icon, Action click, string tip, bool enabled = true)
    {
        var button = new Button
                     {
                         Content = Icon(icon, 15), Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(2, 0, 2, 0),
                         ToolTip = tip, IsEnabled          = enabled, MinWidth                 = 26
                     };
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>Check boxes laid out in aligned columns.</summary>
    public static EqualColumnPanel Checks(IEnumerable<UIElement> boxes, int maxColumns = 0)
    {
        var panel = new EqualColumnPanel { Margin = new Thickness(4, 2, 4, 2), MaxColumns = maxColumns };
        foreach (var box in boxes)
        {
            if (box is FrameworkElement fe) fe.Margin = new Thickness(2, 2, 12, 2);
            panel.Children.Add(box);
        }

        return panel;
    }

    /// <summary>
    ///     The banner over a pane full of ready-made entries (templates, built-in examples): the template icon and a line
    ///     saying these are
    ///     not data of the Silo.
    /// </summary>
    public static Border TemplateBanner(string text = "Templates: built in to the app, not saved in your Silo")
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (Icon("Template", 14) is { } glyph)
        {
            glyph.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(glyph);
        }

        row.Children.Add(Label(text, true));
        var border = new Border
                     {
                         Child   = row, Padding = new Thickness(8, 3, 8, 3), BorderThickness = new Thickness(0, 0, 0, 1),
                         ToolTip = "These come with the app. Use one as a starting point; it is yours once you save it."
                     };
        border.SetResourceReference(Border.BackgroundProperty,  "Theme.Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        return border;
    }

    /// <summary>
    ///     A heading between groups of a list. <paramref name="template" /> marks a group of ready-made entries with the
    ///     template icon.
    /// </summary>
    public static ListBoxItem Section(string text, bool template = false, string? icon = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 1) };
        if (Icon(template ? "Template" : icon, 12) is { } glyph)
        {
            glyph.Margin  = new Thickness(0, 0, 5, 0);
            glyph.Opacity = 0.7;
            row.Children.Add(glyph);
        }

        var label = Label(text.ToUpperInvariant(), bold: true);
        label.FontSize = 10;
        label.Muted();
        row.Children.Add(label);
        if (template) row.ToolTip = "Ready-made entries that come with the app: they are not in your Silo";
        return new ListBoxItem { Content = row, IsEnabled = false };
    }

    /// <summary>A line of explanation in a list ("No Silo is open"); it cannot be chosen.</summary>
    public static ListBoxItem Note(string text)
    {
        return new ListBoxItem { Content = Label(text, true, wrap: true), IsEnabled = false };
    }

    /// <summary>
    ///     An entry of a list: <paramref name="row" /> (see <see cref="Row" />) that gives <paramref name="tag" /> when
    ///     it is chosen.
    /// </summary>
    public static ListBoxItem Item(object? tag, UIElement row, string? tip = null)
    {
        return new ListBoxItem { Tag = tag, Content = row, ToolTip = tip, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    /// <summary>
    ///     One entry of a list: [indent and expander] icon | name | kind (a column all the rows share, so the kinds line up) |
    ///     detail. The list
    ///     (or a parent) must have <see cref="Grid.IsSharedSizeScopeProperty" /> set; <see cref="List" /> does.
    /// </summary>
    /// <param name="icon">The icon key of what the entry is.</param>
    /// <param name="name">What it is called.</param>
    /// <param name="kind">What kind of thing it is, in the column of its own (never in parentheses after the name).</param>
    /// <param name="detail">More, at the end of the row.</param>
    /// <param name="template">The entry is a ready-made one that comes with the app (it gets the template mark).</param>
    /// <param name="tree">The list is a tree: the row has room for the indent and the expander.</param>
    /// <param name="level">How deep the entry is in the tree.</param>
    /// <param name="expanded">
    ///     Null: not expandable; else whether it is open (the expander is clicked to
    ///     <paramref name="toggle" />).
    /// </param>
    /// <param name="iconBrush">A color for the icon (a test that failed is red); null for the text color.</param>
    public static Grid Row(string? icon, string name, string? kind = null, string? detail = null, bool template = false, bool tree = false,
                           int     level = 0, bool? expanded = null, Action? toggle = null, Brush? iconBrush = null, bool bold = false,
                           bool    mono  = false)
    {
        var grid = new Grid { Margin = new Thickness(2, 1, 2, 1), Background = Brushes.Transparent };
        if (tree)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(level * 14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        }

        int first = grid.ColumnDefinitions.Count;
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 60 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup               = "kind" });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (tree && expanded is { } open)
        {
            var arrow = new TextBlock
                        {
                            Text              = open ? "\u25BE" : "\u25B8", HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center, Cursor                = Cursors.Hand
                        };
            arrow.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
            arrow.MouseLeftButtonDown += (_, e) =>
                                         {
                                             e.Handled = true;
                                             toggle?.Invoke();
                                         };
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(arrow);
        }

        if (Icon(icon) is { } glyph)
        {
            if (iconBrush is not null) glyph.Foreground = iconBrush;
            Grid.SetColumn(glyph, first);
            grid.Children.Add(glyph);
        }

        var title = Label(name, bold: bold, mono: mono);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(title, first + 1);
        grid.Children.Add(title);

        var type = Label(kind ?? string.Empty, true);
        type.Margin = new Thickness(12, 0, 8, 0);
        Grid.SetColumn(type, first + 2);
        grid.Children.Add(type);

        if (!string.IsNullOrEmpty(detail) || template)
        {
            var end = new StackPanel { Orientation = Orientation.Horizontal };
            if (template && Icon("Template", 12) is { } badge)
            {
                badge.ToolTip = "A template that comes with the app";
                badge.Opacity = 0.6;
                badge.Margin  = new Thickness(0, 0, 4, 0);
                end.Children.Add(badge);
            }

            if (!string.IsNullOrEmpty(detail))
            {
                var more = Label(detail, true);
                more.TextTrimming = TextTrimming.CharacterEllipsis;
                more.MaxWidth     = 220;
                end.Children.Add(more);
            }

            Grid.SetColumn(end, first + 3);
            grid.Children.Add(end);
        }

        return grid;
    }

    /// <summary>
    ///     A box that takes a key or a combination as it is pressed and reports it the way the Menu Lab writes shortcuts
    ///     ("Ctrl+Shift+K").
    ///     Backspace or Delete alone empties it.
    /// </summary>
    public static TextBox KeyBox(string? text, Action<string> changed, double width = 140, string? tip = null)
    {
        var box = new TextBox
                  {
                      Text                     = text ?? string.Empty,
                      MinWidth                 = width,
                      Margin                   = new Thickness(2, 0, 2, 0),
                      IsReadOnly               = true,
                      IsReadOnlyCaretVisible   = false,
                      VerticalContentAlignment = VerticalAlignment.Center,
                      ToolTip                  = tip ?? "Click here and press the key or combination (Backspace clears it)",
                      Cursor                   = Cursors.Hand
                  };
        box.SetResourceReference(Control.BackgroundProperty, "Theme.Background");
        box.SetResourceReference(Control.ForegroundProperty, "Theme.Text");
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        box.PreviewMouseLeftButtonDown += (_, e) =>
                                          {
                                              if (!box.IsKeyboardFocused)
                                              {
                                                  box.Focus();
                                                  e.Handled = true;
                                              }
                                          };
        box.PreviewKeyDown += (_, e) =>
                              {
                                  var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
                                  if (key is Key.Tab) return;

                                  e.Handled = true;
                                  var mods = Keyboard.Modifiers;
                                  if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                                          or Key.LWin or Key.RWin or Key.None) return;

                                  if (mods == ModifierKeys.None && key is Key.Back or Key.Delete)
                                  {
                                      box.Text = string.Empty;
                                      changed(string.Empty);
                                      return;
                                  }

                                  var parts = new List<string>();
                                  if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
                                  if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
                                  if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
                                  if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
                                  parts.Add(KeyName(key));
                                  string typed = string.Join("+", parts);
                                  if (MenuGesture.TryNormalize(typed, out string normal, out string? why))
                                  {
                                      box.Text    = normal;
                                      box.ToolTip = tip ?? "Click here and press the key or combination (Backspace clears it)";
                                      changed(normal);
                                  }
                                  else
                                  {
                                      box.Text    = typed;
                                      box.ToolTip = why;
                                  }
                              };
        return box;
    }

    private static string KeyName(Key key)
    {
        return key switch
               {
                   >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
                   Key.Return              => "Enter",
                   Key.Prior               => "PageUp",
                   Key.Next                => "PageDown",
                   Key.Oem1                => "OemSemicolon",
                   Key.Oem2                => "OemQuestion",
                   Key.Oem3                => "OemTilde",
                   Key.Oem4                => "OemOpenBrackets",
                   Key.Oem5                => "OemPipe",
                   Key.Oem6                => "OemCloseBrackets",
                   Key.Oem7                => "OemQuotes",
                   _                       => key.ToString()
               };
    }
}