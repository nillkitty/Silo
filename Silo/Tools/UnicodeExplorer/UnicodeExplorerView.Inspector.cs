using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

public sealed partial class UnicodeExplorerView
{
    private readonly TextBlock  _bigGlyph    = new() { FontFamily = UiKit.GlyphFont, FontSize = 64, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly TextBlock  _bigName     = UiKit.Text(string.Empty, size: 14, weight: FontWeights.SemiBold, wrap: true);
    private readonly TextBlock  _bigCode     = UiKit.Text(string.Empty, muted: true);
    private readonly StackPanel _detail      = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly Border     _bigGlyphBox = new() { Width = 88, Height = 88, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ToolTip = "Click to copy the character" };
    private readonly Button     _addInspected = UiKit.Btn("Add to collection", () => { }, "Add this character to the collection below (Enter in the list)");

    /// <summary>The character shown in the details; null before the first one is chosen.</summary>
    private UnicodeCharacter? Inspected { get; set; }

    /// <summary>Whether the character shown was picked in the collection (then a color chosen in the details goes to the collection's selection).</summary>
    private bool _inspectedFromTray;

    private UIElement BuildInspector()
    {
        var root = new DockPanel { Margin = new Thickness(4, 0, 0, 0) };

        var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyphBox = _bigGlyphBox;
        glyphBox.Child = _bigGlyph;
        glyphBox.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        glyphBox.SetResourceReference(Border.BackgroundProperty, "Theme.Surface");
        glyphBox.Cursor = Cursors.Hand;
        _bigGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        glyphBox.MouseLeftButtonUp += (_, _) =>
                                      {
                                          if (Inspected is { } c) Copy(c.Text, "character");
                                      };
        Grid.SetColumn(glyphBox, 0);
        head.Children.Add(glyphBox);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(_bigName);
        _bigCode.FontFamily = UiKit.Mono;
        names.Children.Add(_bigCode);
        Grid.SetColumn(names, 1);
        head.Children.Add(names);

        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        _addInspected.Click += (_, _) =>
                               {
                                   if (Inspected is { } c) AddToTray(c.CodePoint);
                               };
        actions.Children.Add(_addInspected);
        actions.Children.Add(UiKit.Btn("Copy", () =>
                                               {
                                                   if (Inspected is { } c) Copy(c.Text, "character");
                                               }, "Copy the character"));
        actions.Children.Add(UiKit.Btn("Copy U+", () =>
                                                  {
                                                      if (Inspected is { } c) Copy(c.CodePoint.Hex, "code point");
                                                  }, "Copy the code point, like U+1F7E2"));

        var top = new StackPanel();
        top.Children.Add(head);
        top.Children.Add(actions);
        top.Children.Add(BuildColorStrip());
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _detail };
        var frame  = new Border { BorderThickness = new Thickness(1), Child = scroll, Padding = new Thickness(6, 2, 6, 6) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        frame.SetResourceReference(Border.BackgroundProperty, "Theme.BackgroundAlt");
        root.Children.Add(frame);
        return root;
    }

    /// <summary>Shows everything known about a code point.</summary>
    private void Inspect(int codePoint)
    {
        if (_db is null) return;

        var c = _db.Get(codePoint);
        Inspected          = c;
        _inspectedFromTray = false; // the collection sets it again after picking (see SelectTray)

        _bigGlyph.Text  = c.Glyph;
        _bigName.Text   = c.Name;
        _bigCode.Text   = c.CodePoint.Hex;
        _detail.Children.Clear();

        // properties
        var props = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        props.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        props.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;

        void Prop(string label, UIElement value)
        {
            props.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = UiKit.Text(label, muted: true);
            l.VerticalAlignment = VerticalAlignment.Top;
            l.Margin            = new Thickness(0, 1, 6, 1);
            Grid.SetRow(l, row);
            props.Children.Add(l);
            if (value is FrameworkElement fe) fe.Margin = new Thickness(0, 1, 0, 1);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            props.Children.Add(value);
            row++;
        }

        void Plain(string label, string value) => Prop(label, Wrapped(value));

        Prop("Category", Link($"{c.Category} – {c.CategoryName}", () => SearchFor("cat:" + c.Category), $"Show every {c.CategoryName}"));
        if (c.Block is { } block)
            Prop("Block", Link($"{block.Name}  (U+{block.Start:X4}..U+{block.End:X4})", () => SearchFor("block:\"" + block.Name + "\""), "Show the whole block"));
        Prop("Script", Link(c.Script, () => SearchFor("script:" + c.ScriptCode), "Show every character in this script"));
        if (c.Age is { } age) Prop("Added in", Link("Unicode " + age, () => SearchFor("age:" + age), "Show the characters added in this version"));
        Plain("Plane", $"{c.Plane} – {c.PlaneName}");
        if (c.IsAssigned)
        {
            Plain("Bidi", $"{c.BidiClass} – {c.BidiClassName}");
            Plain("Width", $"{c.EastAsianWidth} – {c.EastAsianWidthName}");
            if (c.CombiningClass != 0) Plain("Combining", c.CombiningClass.ToString());
            if (c.IsMirrored) Plain("Mirrored", "yes");
            if (c.NumericValue is { } num) Plain("Numeric value", num);
        }

        if (c.IsEmoji)
        {
            var kinds = new List<string> { "emoji" };
            if (c.IsEmojiPresentation) kinds.Add("shown as a picture by default");
            if (c.IsEmojiModifier) kinds.Add("skin tone modifier");
            Prop("Emoji", c.EmojiGroup.Length > 0
                              ? Link(string.Join(", ", kinds) + " · " + c.EmojiGroup, () => SearchFor("is:emoji \"" + c.EmojiGroup + "\""), "Show emoji in this group")
                              : Wrapped(string.Join(", ", kinds)));
        }
        else if (c.IsExtendedPictographic)
            Plain("Emoji", "pictograph (not an emoji)");

        if (c.DecompositionCodePoints.Count > 0)
        {
            var parts = new WrapPanel();
            if (c.DecompositionKind.Length > 0) parts.Children.Add(UiKit.Text("<" + c.DecompositionKind + ">  ", muted: true));
            foreach (var part in c.DecompositionCodePoints)
            {
                var p = part;
                parts.Children.Add(Link($"{p.Hex} {_db.Get(p).Glyph}  ", () => ShowCodePoint(p), "Show this character"));
            }

            Prop("Decomposes to", parts);
        }

        if (c.Aliases.Count > 0) Plain("Also called", string.Join("; ", c.Aliases));
        if (c.HtmlEntities.Count > 0)
        {
            var ents = new WrapPanel();
            foreach (string e in c.HtmlEntities)
            {
                string entity = e;
                ents.Children.Add(Link(entity + "  ", () => Copy(entity, "entity"), "Copy"));
            }

            Prop("HTML entity", ents);
        }

        _detail.Children.Add(props);

        // keywords: every one searches
        if (c.Keywords.Count > 0)
        {
            _detail.Children.Add(Heading("Tags"));
            var tags = new WrapPanel();
            foreach (string k in c.Keywords)
            {
                string keyword = k;
                tags.Children.Add(Chip(keyword, () => SearchFor(keyword.Contains(' ') ? "\"" + keyword + "\"" : keyword), "Search for this tag"));
            }

            _detail.Children.Add(tags);
        }

        // encodings: click to copy
        if (c.IsKnown)
        {
            var entries = UnicodeEncodings.Describe(c.CodePoint, c);
            foreach (string group in UnicodeEncodings.Groups)
            {
                var inGroup = UnicodeEncodings.InGroup(entries, group).ToList();
                if (inGroup.Count == 0) continue;

                _detail.Children.Add(Heading(group));
                foreach (var entry in inGroup) _detail.Children.Add(EncodingRow(entry));
            }
        }

        UpdateColorStrip();
    }

    // ---- small pieces --------------------------------------------------------------------------------------------

    private static TextBlock Wrapped(string text)
    {
        var t = UiKit.Text(text, wrap: true);
        t.VerticalAlignment = VerticalAlignment.Top;
        return t;
    }

    private static TextBlock Heading(string text)
    {
        var t = UiKit.Text(text, size: 12, weight: FontWeights.SemiBold);
        t.Margin = new Thickness(0, 8, 0, 2);
        return t;
    }

    /// <summary>Text that acts as a link: accent colored, underlined when the mouse is over it.</summary>
    private static TextBlock Link(string text, Action click, string? tip = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Accent");
        if (tip is not null) t.ToolTip = tip;
        t.MouseEnter         += (_, _) => t.TextDecorations = TextDecorations.Underline;
        t.MouseLeave         += (_, _) => t.TextDecorations = null;
        t.MouseLeftButtonUp  += (_, _) => click();
        return t;
    }

    private static Border Chip(string text, Action click, string tip)
    {
        var label = new TextBlock { Text = text, FontSize = 11 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        var chip = new Border
                   {
                       Child = label, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(8),
                       BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = tip
                   };
        UiKit.Paint(chip, false);
        chip.MouseEnter        += (_, _) => UiKit.Paint(chip, false, hot: true);
        chip.MouseLeave        += (_, _) => UiKit.Paint(chip, false);
        chip.MouseLeftButtonUp += (_, _) => click();
        return chip;
    }

    private Border EncodingRow(EncodingEntry entry)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = UiKit.Text(entry.Name, muted: true);
        name.VerticalAlignment = VerticalAlignment.Top;
        name.TextTrimming      = TextTrimming.CharacterEllipsis;
        name.ToolTip           = entry.Note.Length > 0 ? entry.Note : entry.Name;

        var value = new TextBlock { Text = entry.Value, FontFamily = UiKit.Mono, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");

        Grid.SetColumn(value, 1);
        grid.Children.Add(name);
        grid.Children.Add(value);

        var row = new Border { Child = grid, Padding = new Thickness(2, 1, 2, 1), Cursor = Cursors.Hand, ToolTip = (entry.Note.Length > 0 ? entry.Note + " — " : string.Empty) + "click to copy", Background = Brushes.Transparent };
        row.MouseEnter        += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Theme.SurfaceHover");
        row.MouseLeave        += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => Copy(entry.Value, entry.Name);
        return row;
    }
}
