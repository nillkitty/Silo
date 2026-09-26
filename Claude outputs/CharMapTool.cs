using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Telefrag.DI;

namespace Silo.Tools;

/// <summary>
///     A Unicode character map: a searchable, virtualized glyph browser with cross-font
///     preview, <c>\uXXXX</c> / Alt-code display for whatever character is selected, and a
///     most-recently-used strip sized to actually be useful (unlike, say, Windows' emoji
///     picker, whose MRU row holds a handful of postage-stamp icons and no scrollback).
/// </summary>
/// <remarks>
///     <para>
///     Glyphs are browsed a Unicode block at a time (<see cref="UnicodeBlocks.All" />) rather
///     than as one flat 0..0x10FFFF sweep, both because most of the codespace beyond the BMP
///     is of no practical interest here and because it keeps "how many tiles does this view
///     have" bounded to something a person would actually pick from. Within a block - or
///     across every block, for a text search (see <see cref="ApplySearch" />) - the tile grid
///     is still virtualized: rows of <see cref="ColumnsPerRow" /> glyphs are pre-chunked into
///     <see cref="CharRow" /> objects and handed to a <see cref="ListBox" /> whose default
///     <see cref="VirtualizingPanel" /> items host only realizes the rows actually on screen.
///     That's what keeps a 20,000-plus-glyph block like CJK Unified Ideographs scrolling
///     smoothly instead of building 20,000 <see cref="Button" /> controls up front.
///     </para>
///     <para>
///     There's no bundled Unicode Character Database in this app, so "search by name" is
///     intentionally modest: it matches Unicode block names (with a few common synonyms - see
///     <see cref="BlockKeywordAliases" />) and a small curated dictionary of the names people
///     actually type (<see cref="NamedChars" /> - "copyright", "em dash", "bullet",
///     "checkmark", "smile", ...). Typing or pasting a codepoint directly - <c>U+2022</c>,
///     <c>0x2022</c>, <c>•</c>, an HTML entity, or the literal character itself - jumps
///     straight to it. Swapping in a real UCD name lookup later only means replacing the
///     keyword branch of <see cref="ApplySearch" />; the row-virtualized grid, the details
///     panel, and the MRU are all agnostic to how a <see cref="CharGlyphItem" /> was found.
///     </para>
///     <para>
///     "Cross-font preview" means: whatever glyph is selected is re-rendered, live, in the
///     grid's current font plus a short list of other common fonts (<see cref="CompareFonts" />),
///     each checked via <see cref="GlyphTypeface.CharacterToGlyphMap" /> so a font that simply
///     doesn't have that codepoint is called out ("no glyph in this font") instead of silently
///     showing whatever fallback-font tofu box the text layer substituted in.
///     </para>
/// </remarks>
/// <example>
///     Registered as a singleton (like <see cref="ConnectionDiagramTool" />, and for the same
///     reason): the recent-glyphs strip and the "characters to copy" output box are exactly
///     the kind of state a person expects to survive closing and reopening the tool's window
///     within a session, so there's deliberately only ever one instance of it.
/// </example>
[Singleton(typeof(ITool))]
public class CharMapTool : ToolBase
{
    private const int ColumnsPerRow = 16;
    private const int MaxMru = 48;
    private const int MaxSearchHits = 6000;

    private const double GridTileSize = 34;
    private const double GridTileFontSize = 18;
    private const double MruTileSize = 42;
    private const double MruTileFontSize = 20;

    /// <summary>
    ///     Reference fonts always offered in the cross-font preview panel, in addition to
    ///     whichever font the main grid is currently showing. A mix of the fonts most likely
    ///     to be installed and most likely to disagree about symbol/emoji coverage.
    /// </summary>
    private static readonly string[] CompareFonts =
    [
        "Segoe UI", "Segoe UI Symbol", "Segoe UI Emoji", "Segoe UI Historic",
        "Arial", "Times New Roman", "Consolas", "Cambria Math"
    ];

    /// <summary>
    ///     A small curated dictionary of the names people actually type when hunting for a
    ///     character - substring-matched against the typed search text, so "arrow" finds
    ///     "arrow right" etc. This is not, and doesn't try to be, the Unicode Character
    ///     Database; see the class remarks.
    /// </summary>
    private static readonly Dictionary<string, int> NamedChars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["copyright"] = 0x00A9, ["registered"] = 0x00AE, ["trademark"] = 0x2122,
        ["degree"] = 0x00B0, ["section"] = 0x00A7, ["pilcrow"] = 0x00B6, ["paragraph mark"] = 0x00B6,
        ["bullet"] = 0x2022, ["em dash"] = 0x2014, ["en dash"] = 0x2013, ["ellipsis"] = 0x2026,
        ["euro"] = 0x20AC, ["pound sign"] = 0x00A3, ["yen"] = 0x00A5, ["cent"] = 0x00A2, ["currency sign"] = 0x00A4,
        ["plus minus"] = 0x00B1, ["not equal"] = 0x2260, ["infinity"] = 0x221E, ["approx"] = 0x2248,
        ["check mark"] = 0x2713, ["checkmark"] = 0x2713, ["cross mark"] = 0x2717, ["x mark"] = 0x2717,
        ["heart"] = 0x2665, ["star"] = 0x2605, ["smile"] = 0x1F600, ["grinning"] = 0x1F600,
        ["arrow right"] = 0x2192, ["arrow left"] = 0x2190, ["arrow up"] = 0x2191, ["arrow down"] = 0x2193,
        ["fraction one half"] = 0x00BD, ["fraction one quarter"] = 0x00BC,
        ["multiplication"] = 0x00D7, ["division sign"] = 0x00F7, ["micro sign"] = 0x00B5,
        ["ohm"] = 0x2126, ["angstrom"] = 0x212B, ["sharp"] = 0x266F, ["flat"] = 0x266D,
        ["snowman"] = 0x2603, ["skull"] = 0x1F480, ["thumbs up"] = 0x1F44D, ["fire"] = 0x1F525,
        ["left double quote"] = 0x201C, ["right double quote"] = 0x201D,
        ["left single quote"] = 0x2018, ["right single quote"] = 0x2019
    };

    /// <summary>
    ///     Synonyms expanding a typed search term into one or more Unicode block-name
    ///     substrings, so "emoji" finds the Emoticons/Pictographs blocks and "chinese" finds
    ///     "CJK", without needing a full category classifier per character.
    /// </summary>
    private static readonly Dictionary<string, string[]> BlockKeywordAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["emoji"] = ["Emoticons", "Pictographs", "Transport and Map", "Symbols and Pictographs"],
        ["emoticon"] = ["Emoticons"],
        ["chinese"] = ["CJK"], ["kanji"] = ["CJK"], ["hanzi"] = ["CJK"],
        ["korean"] = ["Hangul"],
        ["japanese"] = ["Hiragana", "Katakana"],
        ["math"] = ["Mathematical"],
        ["symbol"] = ["Symbols", "Dingbats"], ["symbols"] = ["Symbols", "Dingbats"]
    };

    private ComboBox? _blockCombo;
    private ComboBox? _fontCombo;
    private TextBox? _searchBox;
    private TextBlock? _statusText;
    private TextBlock? _bigPreview;
    private ListBox? _grid;
    private ListBox? _mruList;
    private ItemsControl? _crossFontPanel;
    private TextBox? _outputBox;

    private TextBlock? _detailCodepoint, _detailEscape, _detailUtf8, _detailHtml, _detailAlt, _detailWordAlt, _detailCategory, _detailBlock;

    private CharGlyphItem? _selected;
    private bool _suppressSearch;

    /// <summary>
    ///     Recently-used glyphs, newest first. Populated only by an actual "use" (Copy, or
    ///     Add to Output) - not by merely clicking around the grid to preview a glyph - so it
    ///     tracks what the person has actually reached for, the way any real MRU should.
    /// </summary>
    public ObservableCollection<CharGlyphItem> Mru { get; } = [];

    public CharMapTool() : base("charmap")
    {
        DisplayName = "_Character Map";
        GroupName   = "Reference";
    }

    protected override UIElement OnBuildContent()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        var main = BuildMainContent();
        Grid.SetRow(main, 1);
        root.Children.Add(main);

        var mru = BuildMruStrip();
        Grid.SetRow(mru, 2);
        root.Children.Add(mru);

        var output = BuildOutputBar();
        Grid.SetRow(output, 3);
        root.Children.Add(output);

        // A single bubbling handler for every glyph tile (main grid rows and MRU tiles both
        // use the same Button-with-a-CharGlyphItem-Tag template) instead of wiring a
        // per-instance Click handler, which a shared DataTemplate doesn't let us do anyway.
        root.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnTileClicked));

        // Deterministic initial state, independent of exactly when ComboBox SelectionChanged
        // happens to fire during construction.
        if (_blockCombo?.SelectedItem is UnicodeBlock initialBlock) LoadBlock(initialBlock);

        return root;
    }

    private FrameworkElement BuildToolbar()
    {
        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 8, 8, 4) };

        var blockLabel = new TextBlock { Text = "Block:", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(blockLabel, Dock.Left);
        dock.Children.Add(blockLabel);

        var blockCombo = new ComboBox
                          {
                              ItemsSource         = UnicodeBlocks.All,
                              DisplayMemberPath   = nameof(UnicodeBlock.Name),
                              Width               = 230,
                              Margin              = new Thickness(0, 0, 12, 0),
                              VerticalAlignment   = VerticalAlignment.Center
                          };
        DockPanel.SetDock(blockCombo, Dock.Left);
        dock.Children.Add(blockCombo);

        var fontLabel = new TextBlock { Text = "Font:", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(fontLabel, Dock.Left);
        dock.Children.Add(fontLabel);

        var fontFamilies = Fonts.SystemFontFamilies.OrderBy(f => f.Source, StringComparer.OrdinalIgnoreCase).ToList();
        var fontCombo = new ComboBox
                         {
                             ItemsSource       = fontFamilies,
                             Width             = 180,
                             Margin            = new Thickness(0, 0, 12, 0),
                             VerticalAlignment = VerticalAlignment.Center
                         };
        DockPanel.SetDock(fontCombo, Dock.Left);
        dock.Children.Add(fontCombo);

        var searchLabel = new TextBlock { Text = "Search:", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(searchLabel, Dock.Left);
        dock.Children.Add(searchLabel);

        var statusText = new TextBlock
                          {
                              Margin            = new Thickness(12, 0, 0, 0),
                              VerticalAlignment = VerticalAlignment.Center,
                              Foreground        = SystemColors.GrayTextBrush
                          };
        DockPanel.SetDock(statusText, Dock.Right);
        dock.Children.Add(statusText);

        // Not docked, and added last: with LastChildFill this one fills whatever width is
        // left between the left-docked controls and the right-docked status text.
        var searchBox = new TextBox
                         {
                             VerticalAlignment = VerticalAlignment.Center,
                             ToolTip = "Block or glyph name, U+XXXX / 0xXXXX / \\uXXXX / &#xXXXX;, or paste a character"
                         };
        dock.Children.Add(searchBox);

        _fontCombo  = fontCombo;
        _searchBox  = searchBox;
        _statusText = statusText;
        _blockCombo = blockCombo;

        fontCombo.SelectionChanged += (_, _) =>
                                       {
                                           if (fontCombo.SelectedItem is not FontFamily family) return;
                                           if (_grid != null) _grid.FontFamily = family;
                                           if (_bigPreview != null) _bigPreview.FontFamily = family;
                                           if (_selected != null) RefreshCrossFontPanel(_selected);
                                       };

        blockCombo.SelectionChanged += (_, _) =>
                                        {
                                            if (blockCombo.SelectedItem is not UnicodeBlock block) return;
                                            _suppressSearch = true;
                                            searchBox.Text  = string.Empty;
                                            _suppressSearch = false;
                                            LoadBlock(block);
                                        };

        searchBox.TextChanged += (_, _) =>
                                  {
                                      if (_suppressSearch) return;
                                      ApplySearch(searchBox.Text);
                                  };

        var defaultFont = fontFamilies.FirstOrDefault(f => string.Equals(f.Source, "Segoe UI", StringComparison.OrdinalIgnoreCase))
                        ?? fontFamilies.FirstOrDefault()
                        ?? SystemFonts.MessageFontFamily;
        fontCombo.SelectedItem = defaultFont;

        blockCombo.SelectedIndex = 0;

        return dock;
    }

    private FrameworkElement BuildMainContent()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        _grid = new ListBox
                {
                    ItemTemplate    = MakeRowTemplate(MakeTileTemplate(GridTileSize, GridTileFontSize)),
                    BorderThickness = new Thickness(0),
                    Background      = Brushes.Transparent,
                    Margin          = new Thickness(8, 4, 4, 8)
                };

        // The default ListBox items host is already a virtualizing panel; these are set
        // explicitly so that stays true even if a future implicit ListBox style changes the
        // default - it's the whole reason a multi-thousand-glyph block scrolls smoothly.
        VirtualizingPanel.SetIsVirtualizing(_grid, true);
        VirtualizingPanel.SetVirtualizationMode(_grid, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_grid, ScrollUnit.Pixel);

        Grid.SetColumn(_grid, 0);
        grid.Children.Add(_grid);

        var sidebar = BuildSidebar();
        Grid.SetColumn(sidebar, 1);
        grid.Children.Add(sidebar);

        return grid;
    }

    private FrameworkElement BuildSidebar()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 8, 8) };

        var previewBorder = new Border
                             {
                                 BorderThickness = new Thickness(1),
                                 BorderBrush     = SystemColors.ActiveBorderBrush,
                                 Height          = 96,
                                 Margin          = new Thickness(0, 0, 0, 6)
                             };
        _bigPreview = new TextBlock
                       {
                           FontSize          = 64,
                           HorizontalAlignment = HorizontalAlignment.Center,
                           VerticalAlignment   = VerticalAlignment.Center,
                           TextAlignment       = TextAlignment.Center
                       };
        previewBorder.Child = _bigPreview;
        stack.Children.Add(previewBorder);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        var copyBtn = new Button { Content = "Copy", Width = 80, Margin = new Thickness(0, 0, 4, 0) };
        copyBtn.Click += (_, _) => CopySelected();
        var addBtn = new Button { Content = "Add to Output", Width = 110 };
        addBtn.Click += (_, _) => { if (_selected is { } s) AddToOutput(s); };
        actions.Children.Add(copyBtn);
        actions.Children.Add(addBtn);
        stack.Children.Add(actions);

        var details = new Grid();
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        AddDetailRow(details, 0, "Codepoint",      out _detailCodepoint);
        AddDetailRow(details, 1, "UTF-16 escape",  out _detailEscape);
        AddDetailRow(details, 2, "UTF-8 bytes",    out _detailUtf8);
        AddDetailRow(details, 3, "HTML entity",    out _detailHtml);
        AddDetailRow(details, 4, "Alt code (ANSI)", out _detailAlt);
        AddDetailRow(details, 5, "Word entry",     out _detailWordAlt);
        AddDetailRow(details, 6, "Category",       out _detailCategory);
        AddDetailRow(details, 7, "Block",          out _detailBlock);

        stack.Children.Add(details);

        stack.Children.Add(new TextBlock { Text = "Preview in other fonts", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 14, 0, 4) });

        _crossFontPanel = new ItemsControl { ItemTemplate = MakeCrossFontRowTemplate() };
        stack.Children.Add(_crossFontPanel);

        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void AddDetailRow(Grid grid, int row, string label, out TextBlock valueBlock)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var labelBlock = new TextBlock { Text = label + ":", Margin = new Thickness(0, 2, 6, 2), Foreground = SystemColors.GrayTextBrush };
        Grid.SetRow(labelBlock, row);
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        var value = new TextBlock { Margin = new Thickness(0, 2, 6, 2), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        var copySmall = new Button
                         {
                             Content = "⧉", // two joined squares - a plain "copy" glyph with no font-coverage risk beyond BMP
                             Width   = 22,
                             Height  = 20,
                             Padding = new Thickness(0),
                             ToolTip = $"Copy {label}"
                         };
        copySmall.Click += (_, _) =>
                            {
                                if (string.IsNullOrEmpty(value.Text)) return;
                                Clipboard.SetText(value.Text);
                                Status($"Copied {label} to clipboard.");
                            };
        Grid.SetRow(copySmall, row);
        Grid.SetColumn(copySmall, 2);
        grid.Children.Add(copySmall);

        valueBlock = value;
    }

    private FrameworkElement BuildMruStrip()
    {
        var border = new Border { BorderThickness = new Thickness(0, 1, 0, 1), BorderBrush = SystemColors.ActiveBorderBrush, Padding = new Thickness(8, 4, 8, 4) };

        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = "Recent:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(label, 0);
        outer.Children.Add(label);

        // Bigger tiles than a typical emoji-picker MRU, and a real horizontal scrollbar
        // instead of wrapping or truncating - up to MaxMru recent glyphs are all reachable,
        // not just the first handful.
        _mruList = new ListBox
                   {
                       ItemsSource     = Mru,
                       Height          = MruTileSize + 12,
                       BorderThickness = new Thickness(0),
                       Background      = Brushes.Transparent,
                       ItemTemplate    = MakeTileTemplate(MruTileSize, MruTileFontSize)
                   };
        var mruPanel = new FrameworkElementFactory(typeof(StackPanel));
        mruPanel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        _mruList.ItemsPanel = new ItemsPanelTemplate(mruPanel);
        ScrollViewer.SetHorizontalScrollBarVisibility(_mruList, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_mruList, ScrollBarVisibility.Disabled);
        Grid.SetColumn(_mruList, 1);
        outer.Children.Add(_mruList);

        var clearBtn = new Button { Content = "Clear", Width = 60, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        clearBtn.Click += (_, _) => Mru.Clear();
        Grid.SetColumn(clearBtn, 2);
        outer.Children.Add(clearBtn);

        border.Child = outer;
        return border;
    }

    private FrameworkElement BuildOutputBar()
    {
        var border = new Border { Padding = new Thickness(8) };
        var grid   = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = "Output:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        _outputBox = new TextBox { FontSize = 18, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(_outputBox, 1);
        grid.Children.Add(_outputBox);

        var copyAll = new Button { Content = "Copy All", Width = 80, Margin = new Thickness(0, 0, 4, 0) };
        copyAll.Click += (_, _) =>
                          {
                              if (_outputBox is { Text.Length: > 0 })
                              {
                                  Clipboard.SetText(_outputBox.Text);
                                  Status("Copied output to clipboard.");
                              }
                          };
        Grid.SetColumn(copyAll, 2);
        grid.Children.Add(copyAll);

        var clear = new Button { Content = "Clear", Width = 60 };
        clear.Click += (_, _) => { if (_outputBox != null) _outputBox.Text = string.Empty; };
        Grid.SetColumn(clear, 3);
        grid.Children.Add(clear);

        border.Child = grid;
        return border;
    }

    // --- Templates -----------------------------------------------------------------------

    /// <summary>
    ///     Builds the shared per-glyph tile template used both by the main grid's per-row
    ///     <see cref="ItemsControl" /> and by the MRU strip - a plain <see cref="Button" />
    ///     whose content is the glyph text itself (so it inherits <see cref="Control.FontFamily" />
    ///     from whichever ancestor sets it, rather than needing a per-tile font binding), with
    ///     its <see cref="FrameworkElement.Tag" /> bound to the whole <see cref="CharGlyphItem" />
    ///     so the single bubbling click handler (<see cref="OnTileClicked" />) can read it back.
    /// </summary>
    private static DataTemplate MakeTileTemplate(double size, double fontSize)
    {
        var button = new FrameworkElementFactory(typeof(Button));
        button.SetValue(FrameworkElement.WidthProperty, size);
        button.SetValue(FrameworkElement.HeightProperty, size);
        button.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
        button.SetValue(Control.FontSizeProperty, fontSize);
        button.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center);
        button.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center);
        button.SetBinding(ContentControl.ContentProperty, new Binding(nameof(CharGlyphItem.Text)));
        button.SetBinding(FrameworkElement.TagProperty, new Binding("."));
        button.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(CharGlyphItem.CodepointHex)));

        return new DataTemplate(typeof(CharGlyphItem)) { VisualTree = button };
    }

    /// <summary>
    ///     Builds the per-<see cref="CharRow" /> template: a fixed <see cref="ColumnsPerRow" />-
    ///     column <see cref="UniformGrid" /> of glyph tiles. A short last row still lines its
    ///     tiles up under the columns above it rather than centering or left-packing, matching
    ///     the fixed-grid feel of a traditional character-map dialog.
    /// </summary>
    private static DataTemplate MakeRowTemplate(DataTemplate tileTemplate)
    {
        var items = new FrameworkElementFactory(typeof(ItemsControl));
        items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(CharRow.Items)));
        items.SetValue(ItemsControl.ItemTemplateProperty, tileTemplate);

        var panel = new FrameworkElementFactory(typeof(UniformGrid));
        panel.SetValue(UniformGrid.ColumnsProperty, ColumnsPerRow);
        panel.SetValue(UniformGrid.RowsProperty, 1);
        items.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(panel));

        return new DataTemplate(typeof(CharRow)) { VisualTree = items };
    }

    private static DataTemplate MakeCrossFontRowTemplate()
    {
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(CrossFontRow.DisplayName)));
        name.SetValue(FrameworkElement.WidthProperty, 130.0);
        name.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetBinding(TextBlock.TextProperty, new Binding(nameof(CrossFontRow.Text)));
        glyph.SetBinding(TextBlock.FontFamilyProperty, new Binding(nameof(CrossFontRow.Family)));
        glyph.SetValue(TextBlock.FontSizeProperty, 22.0);
        glyph.SetValue(FrameworkElement.WidthProperty, 40.0);
        glyph.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

        var note = new FrameworkElementFactory(typeof(TextBlock));
        note.SetBinding(TextBlock.TextProperty, new Binding(nameof(CrossFontRow.Note)));
        note.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);
        note.SetValue(TextBlock.FontStyleProperty, FontStyles.Italic);
        note.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2));
        row.AppendChild(name);
        row.AppendChild(glyph);
        row.AppendChild(note);

        return new DataTemplate(typeof(CrossFontRow)) { VisualTree = row };
    }

    // --- Behavior --------------------------------------------------------------------------

    private void OnTileClicked(object sender, RoutedEventArgs e)
    {
        if (e.Source is Button { Tag: CharGlyphItem item })
            Select(item);
    }

    private void Select(CharGlyphItem item)
    {
        _selected = item;
        if (_bigPreview != null) _bigPreview.Text = item.Text;
        RefreshDetailsPanel(item);
        RefreshCrossFontPanel(item);
    }

    private void RefreshDetailsPanel(CharGlyphItem item)
    {
        if (_detailCodepoint is null) return;
        _detailCodepoint!.Text = item.CodepointHex;
        _detailEscape!.Text    = item.EscapeLiteral;
        _detailUtf8!.Text      = item.Utf8Hex;
        _detailHtml!.Text      = item.HtmlEntity;
        _detailAlt!.Text       = item.AltCodeAnsi ?? "(not in Windows-1252)";
        _detailWordAlt!.Text   = item.WordAltX;
        _detailCategory!.Text  = item.CategoryName;
        _detailBlock!.Text     = item.BlockName;
    }

    private void RefreshCrossFontPanel(CharGlyphItem item)
    {
        if (_crossFontPanel is null) return;

        var rows = new List<CrossFontRow>();
        var current = _fontCombo?.SelectedItem as FontFamily;
        if (current != null)
            rows.Add(BuildCrossFontRow($"{current.Source} (current)", current, item));

        foreach (var name in CompareFonts)
        {
            if (current != null && string.Equals(current.Source, name, StringComparison.OrdinalIgnoreCase))
                continue;
            rows.Add(BuildCrossFontRow(name, new FontFamily(name), item));
        }

        _crossFontPanel.ItemsSource = rows;
    }

    private static CrossFontRow BuildCrossFontRow(string displayName, FontFamily family, CharGlyphItem item) =>
        new(displayName, family, item.Text, Supports(family, item.Codepoint));

    /// <summary>
    ///     Whether <paramref name="family" /> actually has a glyph for <paramref name="codepoint" />,
    ///     rather than just falling back to whatever font WPF's text layer substitutes in. A
    ///     missing entry in the font's own <see cref="GlyphTypeface.CharacterToGlyphMap" /> is
    ///     the real signal here - the map's keys are full Unicode codepoints, so this works
    ///     the same for a BMP symbol and a supplementary-plane emoji.
    /// </summary>
    private static bool Supports(FontFamily family, int codepoint)
    {
        try
        {
            return new Typeface(family).TryGetGlyphTypeface(out var glyphTypeface)
                && glyphTypeface.CharacterToGlyphMap.ContainsKey(codepoint);
        }
        catch
        {
            return false;
        }
    }

    private void CopySelected()
    {
        if (_selected is not { } item) return;
        Clipboard.SetText(item.Text);
        Touch(item);
        Status($"Copied {item.CodepointHex} to clipboard.");
    }

    private void AddToOutput(CharGlyphItem item)
    {
        if (_outputBox is null) return;
        _outputBox.Text       += item.Text;
        _outputBox.CaretIndex =  _outputBox.Text.Length;
        Touch(item);
    }

    /// <summary>Moves <paramref name="item" /> to the front of <see cref="Mru" />, de-duplicated by codepoint, capped at <see cref="MaxMru" />.</summary>
    private void Touch(CharGlyphItem item)
    {
        var existing = Mru.FirstOrDefault(x => x.Codepoint == item.Codepoint);
        if (existing != null) Mru.Remove(existing);
        Mru.Insert(0, item);
        while (Mru.Count > MaxMru) Mru.RemoveAt(Mru.Count - 1);
    }

    private void Status(string text)
    {
        if (_statusText != null) _statusText.Text = text;
    }

    private void LoadBlock(UnicodeBlock block)
    {
        var items = new List<CharGlyphItem>(block.End - block.Start + 1);
        for (int cp = block.Start; cp <= block.End; cp++)
            if (IsBrowsable(cp))
                items.Add(new CharGlyphItem(cp, block.Name));

        ShowItems(items, $"{block.Name} — {items.Count:N0} character{(items.Count == 1 ? "" : "s")}");
    }

    /// <summary>
    ///     Interprets the search box's text: a direct codepoint address (any of the forms
    ///     <see cref="TryParseCodepoint" /> understands, including a pasted literal character)
    ///     jumps straight there; anything else is matched against <see cref="NamedChars" /> and
    ///     block names (via <see cref="BlockKeywordAliases" />), capped at <see cref="MaxSearchHits" />
    ///     total hits so a broad term like a single letter can't try to materialize the entire
    ///     Unicode range at once.
    /// </summary>
    private void ApplySearch(string raw)
    {
        string text = raw.Trim();
        if (text.Length == 0)
        {
            if (_blockCombo?.SelectedItem is UnicodeBlock current) LoadBlock(current);
            return;
        }

        if (TryParseCodepoint(text, out int cp))
        {
            var item = new CharGlyphItem(cp, BlockNameFor(cp));
            ShowItems([item], $"{item.CodepointHex} in {item.BlockName}");
            Select(item);
            return;
        }

        var seen = new HashSet<int>();
        var hits = new List<CharGlyphItem>();

        foreach (var (name, namedCp) in NamedChars)
            if (name.Contains(text, StringComparison.OrdinalIgnoreCase) && seen.Add(namedCp))
                hits.Add(new CharGlyphItem(namedCp, BlockNameFor(namedCp)));

        string[] terms = BlockKeywordAliases.TryGetValue(text, out var aliases) ? aliases : [text];
        foreach (var block in UnicodeBlocks.All)
        {
            if (!terms.Any(t => block.Name.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;

            for (int bcp = block.Start; bcp <= block.End && hits.Count < MaxSearchHits; bcp++)
                if (IsBrowsable(bcp) && seen.Add(bcp))
                    hits.Add(new CharGlyphItem(bcp, block.Name));
        }

        if (hits.Count == 0)
        {
            ShowItems([], $"No matches for \"{text}\"");
            return;
        }

        string truncated = hits.Count >= MaxSearchHits ? $" (showing first {MaxSearchHits:N0})" : "";
        ShowItems(hits, $"{hits.Count:N0} match{(hits.Count == 1 ? "" : "es")} for \"{text}\"{truncated}");
    }

    private void ShowItems(List<CharGlyphItem> items, string status)
    {
        if (_grid != null) _grid.ItemsSource = Chunk(items);
        Status(status);
    }

    private static List<CharRow> Chunk(List<CharGlyphItem> items)
    {
        var rows = new List<CharRow>((items.Count + ColumnsPerRow - 1) / Math.Max(ColumnsPerRow, 1));
        for (int i = 0; i < items.Count; i += ColumnsPerRow)
        {
            int take = Math.Min(ColumnsPerRow, items.Count - i);
            rows.Add(new CharRow(items.GetRange(i, take)));
        }

        return rows;
    }

    private static string BlockNameFor(int cp) =>
        UnicodeBlocks.All.FirstOrDefault(b => cp >= b.Start && cp <= b.End)?.Name ?? "(unlisted range)";

    /// <summary>Excludes unassigned/control/surrogate codepoints from a block or keyword browse (a direct codepoint jump - <see cref="TryParseCodepoint" /> - is allowed to land on these anyway).</summary>
    private static bool IsBrowsable(int cp)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(cp);
        return category is not (UnicodeCategory.OtherNotAssigned or UnicodeCategory.Control or UnicodeCategory.Surrogate);
    }

    /// <summary>
    ///     Parses <paramref name="text" /> as a single codepoint address: a lone character (or
    ///     UTF-16 surrogate pair) pasted in as-is, or one of <c>U+XXXX</c>, <c>0xXXXX</c>,
    ///     <c>\uXXXX</c>, or an <c>&amp;#xXXXX;</c> HTML entity. Deliberately does not accept a
    ///     bare number - "101" is ambiguous between hex and decimal, so it's left to fall
    ///     through to the keyword search instead of guessing.
    /// </summary>
    private static bool TryParseCodepoint(string text, out int codepoint)
    {
        codepoint = -1;

        if (text.Length == 1 && IsUsable(text[0]))
        {
            codepoint = text[0];
            return true;
        }

        if (text.Length == 2 && char.IsSurrogatePair(text[0], text[1]))
        {
            codepoint = char.ConvertToUtf32(text[0], text[1]);
            return true;
        }

        string? hex = null;
        if (text.StartsWith("U+", StringComparison.OrdinalIgnoreCase)) hex = text[2..];
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = text[2..];
        else if (text.StartsWith("\\u", StringComparison.OrdinalIgnoreCase)) hex = text[2..];
        else if (text.StartsWith("&#x", StringComparison.OrdinalIgnoreCase) && text.EndsWith(';')) hex = text[3..^1];

        if (hex is null) return false;
        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int parsed)) return false;
        if (!IsUsable(parsed)) return false;

        codepoint = parsed;
        return true;

        static bool IsUsable(int value) => value is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF);
    }
}

/// <summary>One contiguous, named Unicode block, used to scope the glyph browser.</summary>
public sealed record UnicodeBlock(string Name, int Start, int End);

/// <summary>
///     The Unicode block boundaries the character map understands. Public block ranges are
///     numeric facts published by the Unicode Consortium, not application logic, so this is a
///     plain reference table rather than anything computed - a handful of blocks that would
///     mostly just be noise (private-use areas, the surrogate ranges themselves, most of the
///     rarer historic scripts) are left out on purpose.
/// </summary>
public static class UnicodeBlocks
{
    public static readonly IReadOnlyList<UnicodeBlock> All =
    [
        new("Basic Latin", 0x0000, 0x007F),
        new("Latin-1 Supplement", 0x0080, 0x00FF),
        new("Latin Extended-A", 0x0100, 0x017F),
        new("Latin Extended-B", 0x0180, 0x024F),
        new("IPA Extensions", 0x0250, 0x02AF),
        new("Spacing Modifier Letters", 0x02B0, 0x02FF),
        new("Combining Diacritical Marks", 0x0300, 0x036F),
        new("Greek and Coptic", 0x0370, 0x03FF),
        new("Cyrillic", 0x0400, 0x04FF),
        new("Cyrillic Supplement", 0x0500, 0x052F),
        new("Armenian", 0x0530, 0x058F),
        new("Hebrew", 0x0590, 0x05FF),
        new("Arabic", 0x0600, 0x06FF),
        new("Syriac", 0x0700, 0x074F),
        new("Arabic Supplement", 0x0750, 0x077F),
        new("Thaana", 0x0780, 0x07BF),
        new("Devanagari", 0x0900, 0x097F),
        new("Bengali", 0x0980, 0x09FF),
        new("Gurmukhi", 0x0A00, 0x0A7F),
        new("Gujarati", 0x0A80, 0x0AFF),
        new("Oriya", 0x0B00, 0x0B7F),
        new("Tamil", 0x0B80, 0x0BFF),
        new("Telugu", 0x0C00, 0x0C7F),
        new("Kannada", 0x0C80, 0x0CFF),
        new("Malayalam", 0x0D00, 0x0D7F),
        new("Sinhala", 0x0D80, 0x0DFF),
        new("Thai", 0x0E00, 0x0E7F),
        new("Lao", 0x0E80, 0x0EFF),
        new("Tibetan", 0x0F00, 0x0FFF),
        new("Myanmar", 0x1000, 0x109F),
        new("Georgian", 0x10A0, 0x10FF),
        new("Hangul Jamo", 0x1100, 0x11FF),
        new("Ethiopic", 0x1200, 0x137F),
        new("Cherokee", 0x13A0, 0x13FF),
        new("Unified Canadian Aboriginal Syllabics", 0x1400, 0x167F),
        new("Ogham", 0x1680, 0x169F),
        new("Runic", 0x16A0, 0x16FF),
        new("Khmer", 0x1780, 0x17FF),
        new("Mongolian", 0x1800, 0x18AF),
        new("Latin Extended Additional", 0x1E00, 0x1EFF),
        new("Greek Extended", 0x1F00, 0x1FFF),
        new("General Punctuation", 0x2000, 0x206F),
        new("Superscripts and Subscripts", 0x2070, 0x209F),
        new("Currency Symbols", 0x20A0, 0x20CF),
        new("Combining Diacritical Marks for Symbols", 0x20D0, 0x20FF),
        new("Letterlike Symbols", 0x2100, 0x214F),
        new("Number Forms", 0x2150, 0x218F),
        new("Arrows", 0x2190, 0x21FF),
        new("Mathematical Operators", 0x2200, 0x22FF),
        new("Miscellaneous Technical", 0x2300, 0x23FF),
        new("Control Pictures", 0x2400, 0x243F),
        new("Optical Character Recognition", 0x2440, 0x245F),
        new("Enclosed Alphanumerics", 0x2460, 0x24FF),
        new("Box Drawing", 0x2500, 0x257F),
        new("Block Elements", 0x2580, 0x259F),
        new("Geometric Shapes", 0x25A0, 0x25FF),
        new("Miscellaneous Symbols", 0x2600, 0x26FF),
        new("Dingbats", 0x2700, 0x27BF),
        new("Miscellaneous Mathematical Symbols-A", 0x27C0, 0x27EF),
        new("Supplemental Arrows-A", 0x27F0, 0x27FF),
        new("Braille Patterns", 0x2800, 0x28FF),
        new("Supplemental Arrows-B", 0x2900, 0x297F),
        new("Miscellaneous Mathematical Symbols-B", 0x2980, 0x29FF),
        new("Supplemental Mathematical Operators", 0x2A00, 0x2AFF),
        new("Miscellaneous Symbols and Arrows", 0x2B00, 0x2BFF),
        new("Glagolitic", 0x2C00, 0x2C5F),
        new("Latin Extended-C", 0x2C60, 0x2C7F),
        new("Coptic", 0x2C80, 0x2CFF),
        new("Georgian Supplement", 0x2D00, 0x2D2F),
        new("Tifinagh", 0x2D30, 0x2D7F),
        new("Ethiopic Extended", 0x2D80, 0x2DDF),
        new("Cyrillic Extended-A", 0x2DE0, 0x2DFF),
        new("Supplemental Punctuation", 0x2E00, 0x2E7F),
        new("CJK Radicals Supplement", 0x2E80, 0x2EFF),
        new("Kangxi Radicals", 0x2F00, 0x2FDF),
        new("Ideographic Description Characters", 0x2FF0, 0x2FFF),
        new("CJK Symbols and Punctuation", 0x3000, 0x303F),
        new("Hiragana", 0x3040, 0x309F),
        new("Katakana", 0x30A0, 0x30FF),
        new("Bopomofo", 0x3100, 0x312F),
        new("Hangul Compatibility Jamo", 0x3130, 0x318F),
        new("Kanbun", 0x3190, 0x319F),
        new("Enclosed CJK Letters and Months", 0x3200, 0x32FF),
        new("CJK Compatibility", 0x3300, 0x33FF),
        new("CJK Unified Ideographs Extension A", 0x3400, 0x4DBF),
        new("Yijing Hexagram Symbols", 0x4DC0, 0x4DFF),
        new("CJK Unified Ideographs", 0x4E00, 0x9FFF),
        new("Yi Syllables", 0xA000, 0xA48F),
        new("Hangul Syllables", 0xAC00, 0xD7AF),
        new("CJK Compatibility Ideographs", 0xF900, 0xFAFF),
        new("Alphabetic Presentation Forms", 0xFB00, 0xFB4F),
        new("Arabic Presentation Forms-A", 0xFB50, 0xFDFF),
        new("Variation Selectors", 0xFE00, 0xFE0F),
        new("Vertical Forms", 0xFE10, 0xFE1F),
        new("Combining Half Marks", 0xFE20, 0xFE2F),
        new("CJK Compatibility Forms", 0xFE30, 0xFE4F),
        new("Small Form Variants", 0xFE50, 0xFE6F),
        new("Arabic Presentation Forms-B", 0xFE70, 0xFEFF),
        new("Halfwidth and Fullwidth Forms", 0xFF00, 0xFFEF),
        new("Specials", 0xFFF0, 0xFFFF),
        new("Musical Symbols", 0x1D100, 0x1D1FF),
        new("Mathematical Alphanumeric Symbols", 0x1D400, 0x1D7FF),
        new("Playing Cards", 0x1F0A0, 0x1F0FF),
        new("Enclosed Alphanumeric Supplement", 0x1F100, 0x1F1FF),
        new("Enclosed Ideographic Supplement", 0x1F200, 0x1F2FF),
        new("Miscellaneous Symbols and Pictographs", 0x1F300, 0x1F5FF),
        new("Emoticons", 0x1F600, 0x1F64F),
        new("Transport and Map Symbols", 0x1F680, 0x1F6FF),
        new("Alchemical Symbols", 0x1F700, 0x1F77F),
        new("Geometric Shapes Extended", 0x1F780, 0x1F7FF),
        new("Supplemental Symbols and Pictographs", 0x1F900, 0x1F9FF),
        new("Symbols and Pictographs Extended-A", 0x1FA70, 0x1FAFF)
    ];
}

/// <summary>
///     A single browsable glyph. Everything shown in the details sidebar - <c>\uXXXX</c>
///     escape, HTML entity, UTF-8 bytes, and the two Alt-code entry conventions - is derived
///     eagerly in the constructor, since a <see cref="CharGlyphItem" /> is small, immutable,
///     and (thanks to the row-virtualized grid) only ever constructed for glyphs that are
///     actually about to be shown or matched.
/// </summary>
public sealed class CharGlyphItem
{
    public int Codepoint { get; }

    /// <summary>The glyph itself, as a .NET string - a single <see cref="char" /> for a BMP codepoint, or a UTF-16 surrogate pair beyond it.</summary>
    public string Text { get; }

    public string BlockName { get; }
    public string CategoryName { get; }

    public string CodepointHex => Codepoint <= 0xFFFF ? $"U+{Codepoint:X4}" : $"U+{Codepoint:X5}";

    /// <summary>The <c>\uXXXX</c> escape - a surrogate pair (plus the equivalent single <c>\U</c> form) for anything past the BMP.</summary>
    public string EscapeLiteral { get; }

    public string HtmlEntity => $"&#x{Codepoint:X};";

    public string Utf8Hex { get; }

    /// <summary>
    ///     The classic Windows "Alt+0###" numeric-keypad entry method, valid only for
    ///     codepoints with a single-byte Windows-1252 ("ANSI") encoding - <see langword="null" />
    ///     for anything else (there, only the Word-style <see cref="WordAltX" /> entry applies).
    /// </summary>
    public string? AltCodeAnsi { get; }

    /// <summary>Microsoft Word's Unicode entry method: type the hex codepoint, then press Alt+X. Works for any codepoint.</summary>
    public string WordAltX => $"{Codepoint:X4}, Alt+X";

    public CharGlyphItem(int codepoint, string blockName)
    {
        Codepoint    = codepoint;
        Text         = char.ConvertFromUtf32(codepoint);
        BlockName    = blockName;
        CategoryName = FriendlyCategory(CharUnicodeInfo.GetUnicodeCategory(codepoint));
        Utf8Hex      = string.Join(' ', System.Text.Encoding.UTF8.GetBytes(Text).Select(b => b.ToString("X2")));
        AltCodeAnsi  = Windows1252.TryGetByte(codepoint, out byte b) ? $"Alt+0{b:D3}" : null;

        EscapeLiteral = codepoint <= 0xFFFF
            ? $"\\u{codepoint:X4}"
            : $"\\u{(int)Text[0]:X4}\\u{(int)Text[1]:X4} (\\U{codepoint:X8})";
    }

    private static string FriendlyCategory(UnicodeCategory c) => c switch
    {
        UnicodeCategory.UppercaseLetter => "Letter, Uppercase",
        UnicodeCategory.LowercaseLetter => "Letter, Lowercase",
        UnicodeCategory.TitlecaseLetter => "Letter, Titlecase",
        UnicodeCategory.ModifierLetter => "Letter, Modifier",
        UnicodeCategory.OtherLetter => "Letter, Other",
        UnicodeCategory.NonSpacingMark => "Mark, Non-Spacing",
        UnicodeCategory.SpacingCombiningMark => "Mark, Spacing Combining",
        UnicodeCategory.EnclosingMark => "Mark, Enclosing",
        UnicodeCategory.DecimalDigitNumber => "Number, Decimal Digit",
        UnicodeCategory.LetterNumber => "Number, Letter",
        UnicodeCategory.OtherNumber => "Number, Other",
        UnicodeCategory.SpaceSeparator => "Separator, Space",
        UnicodeCategory.LineSeparator => "Separator, Line",
        UnicodeCategory.ParagraphSeparator => "Separator, Paragraph",
        UnicodeCategory.Control => "Control",
        UnicodeCategory.Format => "Format",
        UnicodeCategory.Surrogate => "Surrogate",
        UnicodeCategory.PrivateUse => "Private Use",
        UnicodeCategory.ConnectorPunctuation => "Punctuation, Connector",
        UnicodeCategory.DashPunctuation => "Punctuation, Dash",
        UnicodeCategory.OpenPunctuation => "Punctuation, Open",
        UnicodeCategory.ClosePunctuation => "Punctuation, Close",
        UnicodeCategory.InitialQuotePunctuation => "Punctuation, Initial Quote",
        UnicodeCategory.FinalQuotePunctuation => "Punctuation, Final Quote",
        UnicodeCategory.OtherPunctuation => "Punctuation, Other",
        UnicodeCategory.MathSymbol => "Symbol, Math",
        UnicodeCategory.CurrencySymbol => "Symbol, Currency",
        UnicodeCategory.ModifierSymbol => "Symbol, Modifier",
        UnicodeCategory.OtherSymbol => "Symbol, Other",
        UnicodeCategory.OtherNotAssigned => "(Unassigned)",
        _ => c.ToString()
    };
}

/// <summary>A pre-chunked row of <see cref="CharMapTool.ColumnsPerRow" /> glyphs (the unit the main grid's virtualizing <see cref="ListBox" /> actually realizes/recycles).</summary>
public sealed class CharRow(IReadOnlyList<CharGlyphItem> items)
{
    public IReadOnlyList<CharGlyphItem> Items { get; } = items;
}

/// <summary>One row of the cross-font preview panel: a font, the selected glyph rendered in it, and whether that font actually has it.</summary>
internal sealed record CrossFontRow(string DisplayName, FontFamily Family, string Text, bool Supported)
{
    public string Note => Supported ? "" : "(no glyph in this font)";
}

/// <summary>
///     Minimal reverse lookup from a Unicode codepoint to its single-byte Windows-1252
///     ("ANSI") value, for the classic Alt+0### Windows Alt-code entry method - the leading
///     zero is what tells Windows to use the ANSI codepage rather than the OEM one. Bytes
///     0x00-0x7F and 0xA0-0xFF are the identity mapping (CP1252 matches ASCII/Latin-1 there);
///     0x80-0x9F is CP1252's block of curly quotes, dashes, and similar, hand-rolled here
///     rather than pulled from <see cref="System.Text.Encoding.GetEncoding(int)" /> so this
///     tool doesn't need to pull in the System.Text.Encoding.CodePages package just for 27 bytes.
/// </summary>
internal static class Windows1252
{
    private static readonly Dictionary<int, byte> ReverseHighRange = new()
    {
        [0x20AC] = 0x80, [0x201A] = 0x82, [0x0192] = 0x83, [0x201E] = 0x84, [0x2026] = 0x85,
        [0x2020] = 0x86, [0x2021] = 0x87, [0x02C6] = 0x88, [0x2030] = 0x89, [0x0160] = 0x8A,
        [0x2039] = 0x8B, [0x0152] = 0x8C, [0x017D] = 0x8E, [0x2018] = 0x91, [0x2019] = 0x92,
        [0x201C] = 0x93, [0x201D] = 0x94, [0x2022] = 0x95, [0x2013] = 0x96, [0x2014] = 0x97,
        [0x02DC] = 0x98, [0x2122] = 0x99, [0x0161] = 0x9A, [0x203A] = 0x9B, [0x0153] = 0x9C,
        [0x017E] = 0x9E, [0x0178] = 0x9F
    };

    public static bool TryGetByte(int codepoint, out byte value)
    {
        if (codepoint is >= 0x00 and <= 0x7F or >= 0xA0 and <= 0xFF)
        {
            value = (byte)codepoint;
            return true;
        }

        return ReverseHighRange.TryGetValue(codepoint, out value);
    }
}
