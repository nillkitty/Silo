using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Serilog;
using Silo.DataEditing.Compression;
using Silo.Labs.Parser;
using Silo.Providers.Data;
using Telefrag.DI;

namespace Silo.DataEditing.Sprites;

/// <summary>
///     The Sonic Mappings Editor as a data editor: opens a window that edits sprite mappings (and DPLC files) through the binary data
///     specification that asks for it (<see cref="SpriteMappingSpecs.EditorKind" />), so any game whose mappings a specification describes
///     with the same field names (or maps them in its editor settings, <see cref="SpriteBindings" />) is edited the same way.
/// </summary>
[Singleton(typeof(IDataEditor))]
public sealed class SonicMappingsEditor : IDataEditor
{
    public string Kind => SpriteMappingSpecs.EditorKind;

    public string DisplayName => "Sonic Mappings Editor";

    public string IconKey => "Data1";

    public IReadOnlyDictionary<string, string> DefaultSettings => SpriteBindings.Defaults;

    public bool CanEdit(IDataSpec spec, IDataEditorSpec editor) => spec is ParserDataSpec { Doc.Mode: SpecMode.Binary };

    public IReadOnlyList<string> Problems(IDataSpec spec, IDataEditorSpec editor)
    {
        if (spec is not ParserDataSpec { Doc.Mode: SpecMode.Binary } parser) return ["The specification is not a binary one."];

        // the editor writes a few records and reads them back: whatever the specification does with the names shows up
        try
        {
            var b    = new SpriteBindings(editor);
            var map  = new SpriteMappings { Frames = [new SpriteFrame { Pieces = [new SpritePiece { X = -8, Y = -8, Width = 2, Height = 2, Tile = 4, Palette = 1, XFlip = true }] }] };
            var back = SpriteCodec.ReadMappings(parser, SpriteCodec.WriteMappings(parser, map, b), b);
            var p    = back.Frames.Count == 1 && back.Frames[0].Pieces.Count == 1 ? back.Frames[0].Pieces[0] : null;
            return p is { X: -8, Y: -8, Width: 2, Height: 2, Tile: 4, Palette: 1, XFlip: true } ? [] : ["The specification does not keep every field the editor sets (check the field names in the editor settings)."];
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return ["The specification does not fit the editor: " + ex.Message];
        }
    }

    public void Open(IDataSpec spec, IDataEditorSpec editor)
    {
        var window = new SonicMappingsEditorWindow(spec, editor);
        window.Show();
        window.Activate();
    }
}

/// <summary>The Sonic Mappings Editor's window: frames, pieces, a canvas that draws them with the art and palette, tiles to pick from, and DPLC requests.</summary>
public sealed class SonicMappingsEditorWindow : Window
{
    private const int UndoLimit = 100;

    private readonly IDataSpec         _spec;
    private readonly IDataEditorSpec   _editor;
    private readonly SpriteBindings    _bindings;
    private readonly ParserDataSpec?   _dplcSpec;

    private SpriteMappings _map  = new();
    private DplcFile?      _dplc;
    private TileSheet      _art  = new([]);
    private uint[]         _palette = MdPalette.Greys();

    private string? _mapPath, _artPath, _paletteFile, _dplcPath;
    private bool    _mapKos, _artKos, _dplcKos; // the file is Kosinski-compressed (read so, and written back so)
    private bool    _mapAsm, _dplcAsm;          // the file is the disassembly's assembler text (read so, and written back so)
    private IReadOnlyList<string> _loadNotes = [];
    private int     _frame = -1, _piece = -1;
    private bool    _dirty, _loading;

    private readonly Stack<(SpriteMappings Map, DplcFile? Dplc)> _undo = new();
    private readonly Stack<(SpriteMappings Map, DplcFile? Dplc)> _redo = new();

    private readonly ListBox         _frames   = new() { MinWidth = 150 };
    private readonly ListBox         _pieces   = new() { MinHeight = 80 };
    private readonly ListBox         _requests = new() { MinHeight = 60 };
    private readonly SpriteFrameView _view     = new() { MinWidth = 300, MinHeight = 300 };
    private readonly TilePickerView  _tiles    = new();
    private readonly TextBlock       _status   = new() { Margin = new Thickness(8, 2, 8, 2), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock       _stats    = new() { Margin = new Thickness(8, 2, 8, 2) };

    private readonly TextBox  _x = new() { Width = 60 }, _y = new() { Width = 60 }, _tile = new() { Width = 70 };
    private readonly ComboBox _w = new() { Width = 50 }, _h = new() { Width = 50 }, _pal = new() { Width = 50 }, _paletteLine = new() { Width = 50 };
    private readonly CheckBox _xFlip = new() { Content = "X flip" }, _yFlip = new() { Content = "Y flip" }, _priority = new() { Content = "Priority" };
    private readonly TextBox  _reqTiles = new() { Width = 50 }, _reqOffset = new() { Width = 70 };
    private readonly StackPanel _dplcPanel = new();

    public SonicMappingsEditorWindow(IDataSpec spec, IDataEditorSpec editor)
    {
        _spec     = spec;
        _editor   = editor;
        _bindings = new SpriteBindings(editor);
        _dplcSpec = LoadDplcSpec(_bindings["dplc.spec"]);

        Title  = $"{(editor.Title is { Length: > 0 } t ? t : "Sonic Mappings Editor")} - {spec.Name}";
        Width  = 1180;
        Height = 760;
        SetResourceReference(BackgroundProperty, "Theme.Background");
        SetResourceReference(ForegroundProperty, "Theme.Text");

        Content = Build();
        WirePieceFields();
        WireView();

        PreviewKeyDown += OnPreviewKeyDown;
        Closing        += OnClosing;
        Loaded         += (_, _) => ReopenLastFiles();
        UpdateTitle();
    }

    private static ParserDataSpec? LoadDplcSpec(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var doc = SpecStore.IsAvailable ? SpecStore.Load(name) : null;
        if (doc is null && string.Equals(name, SpriteMappingSpecs.Sonic2Dplc().Name, StringComparison.OrdinalIgnoreCase)) doc = SpriteMappingSpecs.Sonic2Dplc();
        return doc is null ? null : new ParserDataSpec(doc);
    }

    // ---- layout ----------------------------------------------------------------------------------

    private UIElement Build()
    {
        var root = new DockPanel();

        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);

        var bar = new DockPanel();
        DockPanel.SetDock(_stats, Dock.Right);
        bar.Children.Add(_stats);
        bar.Children.Add(_status);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        var grid = new Grid { Margin = new Thickness(6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });

        // frames
        var left = new DockPanel();
        var frameButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        frameButtons.Children.Add(Button("Add", AddFrame, "Add an empty frame"));
        frameButtons.Children.Add(Button("Copy", DuplicateFrame, "Duplicate the frame"));
        frameButtons.Children.Add(Button("Delete", DeleteFrame, "Delete the frame (the frames after it are renumbered)"));
        DockPanel.SetDock(frameButtons, Dock.Bottom);
        left.Children.Add(new TextBlock { Text = "Frames", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
        DockPanel.SetDock(left.Children[0], Dock.Top);
        left.Children.Add(frameButtons);
        left.Children.Add(_frames);
        _frames.SelectionChanged += (_, _) =>
                                    {
                                        if (_loading) return;

                                        _frame = _frames.SelectedIndex;
                                        _piece = -1;
                                        ShowFrame();
                                    };
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // canvas
        var centre = new DockPanel { Margin = new Thickness(6, 0, 6, 0) };
        var border = new Border { Child = _view, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        centre.Children.Add(border);
        Grid.SetColumn(centre, 1);
        grid.Children.Add(centre);

        // right: pieces, properties, tiles, DPLC
        var right = new DockPanel();
        var pieceButtons = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        pieceButtons.Children.Add(Button("Add", AddPiece, "Add a piece"));
        pieceButtons.Children.Add(Button("Copy", DuplicatePiece, "Duplicate the piece"));
        pieceButtons.Children.Add(Button("Delete", DeletePiece, "Delete the piece"));
        pieceButtons.Children.Add(Button("▲", () => MovePiece(-1), "Draw it earlier (further to the front)"));
        pieceButtons.Children.Add(Button("▼", () => MovePiece(1), "Draw it later (further to the back)"));

        var props = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        foreach (var c in new[] { _w, _h }) for (int i = 1; i <= 4; i++) c.Items.Add(i);
        for (int i = 0; i < 4; i++)
        {
            _pal.Items.Add(i);
            _paletteLine.Items.Add(i);
        }

        _paletteLine.SelectedIndex = 0;
        props.Children.Add(Row("X", _x, "Y", _y));
        props.Children.Add(Row("Width", _w, "Height", _h));
        props.Children.Add(Row("Tile", _tile, "Palette", _pal));
        props.Children.Add(Row(_xFlip, _yFlip, _priority));

        _dplcPanel.Children.Add(new TextBlock { Text = "DPLC requests of this frame", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 4) });
        _dplcPanel.Children.Add(_requests);
        _dplcPanel.Children.Add(Row("Tiles", _reqTiles, "Offset", _reqOffset));
        var reqButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        reqButtons.Children.Add(Button("Add", AddRequest, "Ask for more tiles"));
        reqButtons.Children.Add(Button("Delete", DeleteRequest, "Remove the request"));
        _dplcPanel.Children.Add(reqButtons);
        _dplcPanel.Visibility = Visibility.Collapsed;

        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Pieces (first is on top)", FontWeight = FontWeights.Bold });
        top.Children.Add(_pieces);
        top.Children.Add(pieceButtons);
        top.Children.Add(props);
        top.Children.Add(_dplcPanel);
        top.Children.Add(new TextBlock { Text = "Tiles (click to set the piece's tile)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 4) });
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);
        right.Children.Add(new ScrollViewer { Content = _tiles, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        _pieces.SelectionChanged += (_, _) =>
                                    {
                                        if (_loading) return;

                                        _piece = _pieces.SelectedIndex;
                                        ShowPiece();
                                    };
        _requests.SelectionChanged += (_, _) => ShowRequest();
        _tiles.TilePicked          += slot => { if (SelectedPiece is not null) EditPiece(p => p.Tile = slot); };

        root.Children.Add(grid);
        return root;
    }

    private Menu BuildMenu()
    {
        var menu = new Menu();

        MenuItem Item(string header, Action run, string? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => Guard(run);
            return item;
        }

        var file = new MenuItem { Header = "_File" };
        file.Items.Add(Item("Open _Mappings...", OpenMappings, "Ctrl+O"));
        file.Items.Add(Item("Open _Art...", OpenArt));
        file.Items.Add(Item("Open _Palette...", OpenPalette));
        file.Items.Add(Item("Open _DPLC...", OpenDplc));
        file.Items.Add(new Separator());
        file.Items.Add(Item("_Save Mappings", SaveMappings, "Ctrl+S"));
        file.Items.Add(Item("Save Mappings _As...", SaveMappingsAs));
        file.Items.Add(Item("Save D_PLC", SaveDplc));
        file.Items.Add(new Separator());
        file.Items.Add(Item("Convert _Kosinski to BIN...", () => ConvertKosinski(false)));
        file.Items.Add(Item("Convert BIN to Kosi_nski...", () => ConvertKosinski(true)));
        file.Items.Add(new Separator());
        file.Items.Add(Item("_Close", Close));

        var edit = new MenuItem { Header = "_Edit" };
        edit.Items.Add(Item("_Undo", Undo, "Ctrl+Z"));
        edit.Items.Add(Item("_Redo", Redo, "Ctrl+Y"));
        edit.Items.Add(new Separator());
        edit.Items.Add(Item("_Add Piece", AddPiece));
        edit.Items.Add(Item("D_uplicate Piece", DuplicatePiece, "Ctrl+D"));
        edit.Items.Add(Item("_Delete Piece", DeletePiece, "Del"));
        edit.Items.Add(Item("Move Piece to _Front", () => MovePiece(-1)));
        edit.Items.Add(Item("Move Piece to _Back", () => MovePiece(1)));
        edit.Items.Add(new Separator());
        edit.Items.Add(Item("Add _Frame", AddFrame));
        edit.Items.Add(Item("Duplicate _Frame", DuplicateFrame));
        edit.Items.Add(Item("Delete F_rame", DeleteFrame));

        var view = new MenuItem { Header = "_View" };
        view.Items.Add(Item("Zoom _In", () => _view.Zoom++));
        view.Items.Add(Item("Zoom _Out", () => _view.Zoom--));
        var outlines = Item("Piece _Outlines", () => { _view.ShowOutlines = !_view.ShowOutlines; _view.InvalidateVisual(); });
        outlines.IsCheckable = true;
        outlines.IsChecked   = true;
        var grid = Item("_Grid", () => { _view.ShowGrid = !_view.ShowGrid; _view.InvalidateVisual(); });
        grid.IsCheckable = true;
        grid.IsChecked   = true;
        view.Items.Add(outlines);
        view.Items.Add(grid);
        view.Items.Add(new Separator());
        var line = new MenuItem { Header = "Palette _line shown in the tiles" };
        for (int i = 0; i < 4; i++)
        {
            int n  = i;
            var it = Item($"Line {i}", () => { _tiles.PaletteLine = n; _tiles.Refresh(); });
            line.Items.Add(it);
        }

        view.Items.Add(line);

        menu.Items.Add(file);
        menu.Items.Add(edit);
        menu.Items.Add(view);
        return menu;
    }

    private static Button Button(string text, Action run, string tip)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), ToolTip = tip };
        b.Click += (_, _) => run();
        return b;
    }

    private static StackPanel Row(params object[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var item in items)
            row.Children.Add(item is string s ? new TextBlock { Text = s, Width = 48, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) } : (UIElement)item);
        foreach (UIElement c in row.Children) if (c is FrameworkElement { Width: > 0 and < 80 } f && f is not TextBlock) f.Margin = new Thickness(0, 0, 10, 0);
        return row;
    }

    // ---- state -----------------------------------------------------------------------------------

    private SpriteFrame? CurrentFrame => _frame >= 0 && _frame < _map.Frames.Count ? _map.Frames[_frame] : null;

    private SpritePiece? SelectedPiece => CurrentFrame is { } f && _piece >= 0 && _piece < f.Pieces.Count ? f.Pieces[_piece] : null;

    private List<DplcRequest>? CurrentRequests => _dplc is not null && _frame >= 0 && _frame < _dplc.Frames.Count ? _dplc.Frames[_frame] : null;

    private void Say(string text) => _status.Text = text;

    private void Guard(Action run)
    {
        try
        {
            run();
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            Say(ex.Message);
            Log.Warning(ex, "The Sonic Mappings Editor could not do that");
            MessageBox.Show(this, ex.Message, "Sonic Mappings Editor");
        }
    }

    private void UpdateTitle()
    {
        string name = _mapPath is null ? "(no file)" : Path.GetFileName(_mapPath);
        Title = $"{(_editor.Title is { Length: > 0 } t ? t : "Sonic Mappings Editor")} - {name}{(_dirty ? " *" : string.Empty)} ({_spec.Name})";
    }

    private void Snapshot()
    {
        _undo.Push((_map.Clone(), _dplc?.Clone()));
        if (_undo.Count > UndoLimit * 2) Trim(); // a Stack cannot drop its oldest: rebuild it now and then
        _redo.Clear();
        _dirty = true;
        UpdateTitle();
    }

    private void Trim()
    {
        var keep = _undo.Take(UndoLimit).Reverse().ToList();
        _undo.Clear();
        foreach (var s in keep) _undo.Push(s);
    }

    private void Undo()
    {
        if (!_undo.TryPop(out var previous)) return;

        _redo.Push((_map.Clone(), _dplc?.Clone()));
        (_map, _dplc) = previous;
        _dirty       = true;
        AfterRestore();
    }

    private void Redo()
    {
        if (!_redo.TryPop(out var next)) return;

        _undo.Push((_map.Clone(), _dplc?.Clone()));
        (_map, _dplc) = next;
        _dirty       = true;
        AfterRestore();
    }

    private void AfterRestore()
    {
        _frame = Math.Min(_frame, _map.Frames.Count - 1);
        _piece = -1;
        FillFrames();
        UpdateTitle();
    }

    // ---- showing ---------------------------------------------------------------------------------

    private void FillFrames()
    {
        _loading = true;
        _frames.Items.Clear();
        for (int i = 0; i < _map.Frames.Count; i++) _frames.Items.Add($"Frame {i}  ({_map.Frames[i].Pieces.Count})");
        _frames.SelectedIndex = _frame;
        _loading = false;
        ShowFrame();
    }

    private void ShowFrame()
    {
        _dplcPanel.Visibility = _dplc is null ? Visibility.Collapsed : Visibility.Visible;
        FillPieces();
        FillRequests();
        RefreshArt();
    }

    private void FillPieces()
    {
        _loading = true;
        _pieces.Items.Clear();
        if (CurrentFrame is { } f)
            for (int i = 0; i < f.Pieces.Count; i++)
                _pieces.Items.Add(PieceText(i, f.Pieces[i]));

        _pieces.SelectedIndex = _piece;
        _loading              = false;
        ShowPiece();
    }

    private static string PieceText(int i, SpritePiece p) =>
        $"{i,2}  ({p.X},{p.Y})  {p.Width}x{p.Height}  tile ${p.Tile:X}  pal {p.Palette}{(p.XFlip ? " X" : string.Empty)}{(p.YFlip ? " Y" : string.Empty)}{(p.Priority ? " P" : string.Empty)}";

    private void ShowPiece()
    {
        var p = SelectedPiece;
        _loading = true;
        _x.Text            = p?.X.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _y.Text            = p?.Y.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _w.SelectedItem    = p?.Width;
        _h.SelectedItem    = p?.Height;
        _tile.Text         = p is null ? string.Empty : "$" + p.Tile.ToString("X", CultureInfo.InvariantCulture);
        _pal.SelectedItem  = p?.Palette;
        _xFlip.IsChecked   = p?.XFlip ?? false;
        _yFlip.IsChecked   = p?.YFlip ?? false;
        _priority.IsChecked = p?.Priority ?? false;
        foreach (UIElement c in new UIElement[] { _x, _y, _w, _h, _tile, _pal, _xFlip, _yFlip, _priority }) c.IsEnabled = p is not null;
        _loading = false;

        _view.SelectedIndex = _piece;
        _tiles.Highlight    = p is null ? (0, 0) : (p.Tile, p.TileCount);
        _view.InvalidateVisual();
        _tiles.InvalidateVisual();
    }

    private void FillRequests()
    {
        _loading = true;
        _requests.Items.Clear();
        if (CurrentRequests is { } list)
            for (int i = 0; i < list.Count; i++)
                _requests.Items.Add($"{i,2}  {list[i].Tiles} tile(s) from ${list[i].Offset:X}");
        _loading = false;
        ShowRequest();
    }

    private void ShowRequest()
    {
        if (_loading) return;

        var list = CurrentRequests;
        int at   = _requests.SelectedIndex;
        _loading = true;
        _reqTiles.Text   = list is not null && at >= 0 && at < list.Count ? list[at].Tiles.ToString(CultureInfo.InvariantCulture) : string.Empty;
        _reqOffset.Text  = list is not null && at >= 0 && at < list.Count ? "$" + list[at].Offset.ToString("X", CultureInfo.InvariantCulture) : string.Empty;
        _reqTiles.IsEnabled = _reqOffset.IsEnabled = list is not null && at >= 0 && at < list.Count;
        _loading = false;
    }

    /// <summary>The art, palette and DPLC of the frame changed, or the frame did: make the canvas and the tiles again.</summary>
    private void RefreshArt()
    {
        int[]? slots = CurrentRequests is { } r ? SpriteComposer.ExpandDplc(r) : null;

        _view.Art       = _art;
        _view.Palette   = _palette;
        _view.ArtTile   = slots is null ? null : SpriteComposer.ThroughDplc(slots);
        _view.Pieces    = CurrentFrame?.Pieces ?? [];
        _tiles.Art      = _art;
        _tiles.Palette  = _palette;
        _tiles.Slots    = slots;
        RefreshView();
        _tiles.Refresh();
    }

    private void RefreshView()
    {
        _view.Pieces = CurrentFrame?.Pieces ?? [];
        _view.Refresh();

        var s = SpriteComposer.Stats(_view.Pieces);
        _stats.Text       = $"{s.Pieces}/{SpriteStats.MaxSprites} sprites, busiest line {s.MaxPerLine}/{SpriteStats.MaxSpritesPerLine}, {s.Tiles} tiles";
        _stats.Foreground = s.OverLimit ? Brushes.IndianRed : (Brush)FindResource("Theme.Text");
        _stats.ToolTip    = "The VDP shows 80 sprites in all (64 in a 256-pixel-wide mode) and 20 on a scanline (16); a frame over that flickers or drops sprites.";
    }

    // ---- editing ---------------------------------------------------------------------------------

    private void WireView()
    {
        _view.PieceClicked += i =>
                              {
                                  _piece = i;
                                  _loading = true;
                                  _pieces.SelectedIndex = i;
                                  _loading = false;
                                  ShowPiece();
                              };
        _view.DragStarting += Snapshot;
        _view.PieceDragged += (i, x, y) =>
                              {
                                  if (CurrentFrame is not { } f || i >= f.Pieces.Count) return;

                                  f.Pieces[i].X = x;
                                  f.Pieces[i].Y = y;
                                  AfterPieceEdit(i);
                              };
    }

    private void WirePieceFields()
    {
        void Text(TextBox box, Action<SpritePiece, int> set)
        {
            void Apply()
            {
                if (_loading || SelectedPiece is null || !TryNumber(box.Text, out int v)) return;

                EditPiece(p => set(p, v));
            }

            box.LostFocus += (_, _) => Apply();
            box.KeyDown   += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        }

        Text(_x, (p, v) => p.X = Math.Clamp(v, -32768, 32767));
        Text(_y, (p, v) => p.Y = Math.Clamp(v, -128, 127));
        Text(_tile, (p, v) => p.Tile = Math.Clamp(v, 0, 0x7FF));
        _w.SelectionChanged   += (_, _) => { if (!_loading && _w.SelectedItem is int v) EditPiece(p => p.Width = v); };
        _h.SelectionChanged   += (_, _) => { if (!_loading && _h.SelectedItem is int v) EditPiece(p => p.Height = v); };
        _pal.SelectionChanged += (_, _) => { if (!_loading && _pal.SelectedItem is int v) EditPiece(p => p.Palette = v); };
        _xFlip.Click          += (_, _) => EditPiece(p => p.XFlip = _xFlip.IsChecked == true);
        _yFlip.Click          += (_, _) => EditPiece(p => p.YFlip = _yFlip.IsChecked == true);
        _priority.Click       += (_, _) => EditPiece(p => p.Priority = _priority.IsChecked == true);

        void Request(TextBox box, Func<DplcRequest, int, DplcRequest> set)
        {
            void Apply()
            {
                int i = _requests.SelectedIndex;
                if (_loading || CurrentRequests is not { } list || i < 0 || i >= list.Count || !TryNumber(box.Text, out int v)) return;

                Snapshot();
                list[i] = set(list[i], v);
                FillRequests();
                _requests.SelectedIndex = i;
                RefreshArt();
            }

            box.LostFocus += (_, _) => Apply();
            box.KeyDown   += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        }

        Request(_reqTiles, (r, v) => r with { Tiles = Math.Clamp(v, 1, 16) });
        Request(_reqOffset, (r, v) => r with { Offset = Math.Max(0, v) });
    }

    private static bool TryNumber(string text, out int value)
    {
        text = text.Trim();
        bool negative = text.StartsWith('-');
        if (negative) text = text[1..].Trim();

        bool ok;
        if (text.StartsWith('$') || text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            ok = int.TryParse(text.TrimStart('$').Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        else ok = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        if (negative) value = -value;
        return ok;
    }

    private void EditPiece(Action<SpritePiece> change)
    {
        if (SelectedPiece is not { } p) return;

        Snapshot();
        change(p);
        AfterPieceEdit(_piece);
        ShowPiece();
    }

    private void AfterPieceEdit(int index)
    {
        if (CurrentFrame is { } f && index >= 0 && index < f.Pieces.Count && index < _pieces.Items.Count)
        {
            _loading = true;
            _pieces.Items[index] = PieceText(index, f.Pieces[index]);
            _pieces.SelectedIndex = index;
            _loading = false;
        }

        RefreshView();
        if (SelectedPiece is { } p)
        {
            _tiles.Highlight = (p.Tile, p.TileCount);
            _tiles.InvalidateVisual();
        }

        _dirty = true;
        UpdateTitle();
    }

    private void AddPiece()
    {
        if (CurrentFrame is not { } f) return;

        Snapshot();
        int at = _piece >= 0 ? _piece + 1 : f.Pieces.Count;
        f.Pieces.Insert(at, new SpritePiece { X = -8, Y = -8, Width = 2, Height = 2 });
        _piece = at;
        RefreshFrameList();
    }

    private void DuplicatePiece()
    {
        if (SelectedPiece is not { } p || CurrentFrame is not { } f) return;

        Snapshot();
        var copy = p.Clone();
        copy.X += 8;
        copy.Y += 8;
        f.Pieces.Insert(_piece + 1, copy);
        _piece++;
        RefreshFrameList();
    }

    private void DeletePiece()
    {
        if (SelectedPiece is null || CurrentFrame is not { } f) return;

        Snapshot();
        f.Pieces.RemoveAt(_piece);
        _piece = Math.Min(_piece, f.Pieces.Count - 1);
        RefreshFrameList();
    }

    private void MovePiece(int by)
    {
        if (CurrentFrame is not { } f || SelectedPiece is not { } p) return;

        int to = _piece + by;
        if (to < 0 || to >= f.Pieces.Count) return;

        Snapshot();
        f.Pieces.RemoveAt(_piece);
        f.Pieces.Insert(to, p);
        _piece = to;
        RefreshFrameList();
    }

    /// <summary>The number of pieces of the frame changed (or their order): the lists show it.</summary>
    private void RefreshFrameList()
    {
        _loading = true;
        if (_frame >= 0 && _frame < _frames.Items.Count) _frames.Items[_frame] = $"Frame {_frame}  ({_map.Frames[_frame].Pieces.Count})";
        _frames.SelectedIndex = _frame;
        _loading = false;
        FillPieces();
        RefreshView();
        UpdateTitle();
    }

    private void AddFrame()
    {
        Snapshot();
        _map.Frames.Add(new SpriteFrame());
        _dplc?.Frames.Add([]);
        _frame = _map.Frames.Count - 1;
        _piece = -1;
        FillFrames();
    }

    private void DuplicateFrame()
    {
        if (CurrentFrame is not { } f) return;

        Snapshot();
        _map.Frames.Insert(_frame + 1, f.Clone());
        _dplc?.Frames.Insert(_frame + 1, CurrentRequests?.ToList() ?? []);
        _frame++;
        _piece = -1;
        FillFrames();
    }

    private void DeleteFrame()
    {
        if (CurrentFrame is null) return;
        if (MessageBox.Show(this, $"Delete frame {_frame}? The frames after it are renumbered, and the game's code refers to them by number.", "Sonic Mappings Editor", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

        Snapshot();
        _map.Frames.RemoveAt(_frame);
        if (_dplc is not null && _frame < _dplc.Frames.Count) _dplc.Frames.RemoveAt(_frame);
        _frame = Math.Min(_frame, _map.Frames.Count - 1);
        _piece = -1;
        FillFrames();
    }

    private void AddRequest()
    {
        if (CurrentRequests is not { } list) return;

        Snapshot();
        list.Add(new DplcRequest(1, list.Count == 0 ? 0 : list[^1].Offset + list[^1].Tiles));
        FillRequests();
        _requests.SelectedIndex = list.Count - 1;
        RefreshArt();
    }

    private void DeleteRequest()
    {
        if (CurrentRequests is not { } list || _requests.SelectedIndex < 0 || _requests.SelectedIndex >= list.Count) return;

        Snapshot();
        list.RemoveAt(_requests.SelectedIndex);
        FillRequests();
        RefreshArt();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool typing = Keyboard.FocusedElement is TextBox or ComboBox;
        bool ctrl   = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (ctrl && e.Key == Key.Z && !typing) { Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y && !typing) { Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { Guard(SaveMappings); e.Handled = true; }
        else if (ctrl && e.Key == Key.O) { Guard(OpenMappings); e.Handled = true; }
        else if (ctrl && e.Key == Key.D && !typing) { DuplicatePiece(); e.Handled = true; }
        else if (!typing && e.Key == Key.Delete) { DeletePiece(); e.Handled = true; }
        else if (!typing && SelectedPiece is not null && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 8 : 1;
            int dx   = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
            int dy   = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
            EditPiece(p =>
                      {
                          p.X += dx;
                          p.Y += dy;
                      });
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_dirty) return;

        var answer = MessageBox.Show(this, "Save the changes to the mappings?", "Sonic Mappings Editor", MessageBoxButton.YesNoCancel);
        if (answer == MessageBoxResult.Cancel) e.Cancel = true;
        else if (answer == MessageBoxResult.Yes) Guard(SaveMappings);
    }

    // ---- files -----------------------------------------------------------------------------------

    private static bool IsKosName(string path) => path.EndsWith(".kos", StringComparison.OrdinalIgnoreCase);

    /// <summary>The bytes of a file; a Kosinski file (<c>.kos</c>) is decompressed, and <paramref name="kosinski" /> says so, so that it is saved the same way.</summary>
    private byte[] ReadSource(string path, out bool kosinski)
    {
        byte[] raw = File.ReadAllBytes(path);
        kosinski = IsKosName(path);
        if (!kosinski) return raw;

        try
        {
            var data = Kosinski.Decompress(raw, out int used);
            Say($"Decompressed {Path.GetFileName(path)}: {raw.Length:N0} -> {data.Length:N0} bytes" + (used < raw.Length ? $" ({raw.Length - used} byte(s) after the end mark ignored)" : string.Empty));
            return data;
        }
        catch (InvalidDataException ex)
        {
            throw new FormatException($"{Path.GetFileName(path)} is not valid Kosinski data: {ex.Message}", ex);
        }
    }

    private static byte[] Encode(byte[] data, bool kosinski) => kosinski ? Kosinski.Compress(data) : data;

    /// <summary>Converts a file between Kosinski and plain bytes, whatever is open (the converter of the editor: for mappings, art, collision, anything a game keeps in Kosinski).</summary>
    private void ConvertKosinski(bool compress)
    {
        if (Ask(compress ? "Choose the file to compress" : "Choose the Kosinski file", compress ? "Data (*.bin)|*.bin" : "Kosinski (*.kos)|*.kos") is not { } path) return;

        byte[] input = File.ReadAllBytes(path);
        byte[] output;
        try
        {
            output = compress ? Kosinski.Compress(input) : Kosinski.Decompress(input);
        }
        catch (InvalidDataException ex)
        {
            throw new FormatException($"{Path.GetFileName(path)} is not valid Kosinski data: {ex.Message}", ex);
        }

        var dialog = new SaveFileDialog
                     {
                         Title    = compress ? "Save the Kosinski file" : "Save the decompressed file",
                         Filter   = compress ? "Kosinski (*.kos)|*.kos|All files (*.*)|*.*" : "Data (*.bin)|*.bin|All files (*.*)|*.*",
                         FileName = Path.ChangeExtension(path, compress ? ".kos" : ".bin")
                     };
        if (dialog.ShowDialog(this) != true) return;

        File.WriteAllBytes(dialog.FileName, output);
        Say($"{Path.GetFileName(path)} -> {Path.GetFileName(dialog.FileName)}: {input.Length:N0} -> {output.Length:N0} bytes");
    }

    private string? Ask(string title, string filter, string? current = null)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter + "|All files (*.*)|*.*", FileName = current ?? string.Empty };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void OpenMappings()
    {
        if (Ask("Open sprite mappings", "Mappings (*.asm;*.bin;*.kos)|*.asm;*.bin;*.kos", _mapPath) is { } path) LoadMappings(path);
    }

    private void OpenArt()
    {
        if (Ask("Open art (4 bits a pixel, 32 bytes a tile; .kos is decompressed)", "Art (*.bin;*.kos)|*.bin;*.kos", _artPath) is { } path) LoadArt(path);
    }

    private void OpenPalette()
    {
        if (Ask("Open a palette (32 bytes: one line; 128: all four)", "Palette (*.bin)|*.bin", _paletteFile) is { } path) LoadPalette(path);
    }

    private void OpenDplc()
    {
        if (Ask("Open a DPLC", "DPLC (*.asm;*.bin;*.kos)|*.asm;*.bin;*.kos", _dplcPath) is { } path) LoadDplc(path);
    }

    private void LoadMappings(string path)
    {
        _map     = ReadMappingsFile(path);
        _mapPath = path;
        _dirty   = false;
        _undo.Clear();
        _redo.Clear();
        _frame = _map.Frames.Count > 0 ? 0 : -1;
        _piece = -1;
        FillFrames();
        UpdateTitle();
        DataEditors.Remember(_spec, "mappings.path", path);
        Say($"Read {_map.Frames.Count} frame(s) from {Path.GetFileName(path)}" + (_loadNotes.Count > 0 ? ". Note: " + _loadNotes[0] : string.Empty));

        // the disassembly keeps an object's DPLC in spriteDPLC, under the same name as its mappings in sprite
        if (_mapAsm && Path.GetDirectoryName(path) is { } dir && string.Equals(Path.GetFileName(dir), "sprite", StringComparison.OrdinalIgnoreCase)
            && Path.Combine(Path.GetDirectoryName(dir)!, "spriteDPLC", Path.GetFileName(path)) is var sibling && File.Exists(sibling))
        {
            LoadDplc(sibling);
        }
        else if (_mapAsm)
        {
            _dplc = null;
            ShowFrame();
        }
    }

    private static bool IsAsmName(string path) => path.EndsWith(".asm", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads sprite mappings from a file: the disassembly's assembler text, or bytes (Kosinski-compressed when named so) through the specification. A file that is plainly something else is said to be.</summary>
    private SpriteMappings ReadMappingsFile(string path)
    {
        if (SpriteAsm.NotSpriteMappings(path) is { } why) throw new FormatException(why + " The Sonic Mappings Editor edits sprite mappings (the disassembly's mappings/sprite folder).");

        _mapKos    = false;
        _loadNotes = [];
        _mapAsm    = IsAsmName(path);
        if (_mapAsm)
        {
            string text = File.ReadAllText(path);
            if (SpriteAsm.IsDplc(text)) throw new FormatException($"{Path.GetFileName(path)} is a DPLC, not sprite mappings: open it with File > Open DPLC.");

            try
            {
                var map = SpriteAsm.ParseMappings(text, out var notes);
                _loadNotes = notes;
                return map;
            }
            catch (FormatException ex)
            {
                throw new FormatException($"{Path.GetFileName(path)}: {ex.Message}", ex);
            }
        }

        try
        {
            return SpriteCodec.ReadMappings(_spec, ReadSource(path, out _mapKos), _bindings);
        }
        catch (FormatException ex) when (!ex.Message.Contains("not valid Kosinski"))
        {
            throw new FormatException($"{Path.GetFileName(path)} does not read as sprite mappings with {_spec.Name}: {ex.Message}", ex);
        }
    }

    private void LoadArt(string path)
    {
        _art     = new TileSheet(ReadSource(path, out _artKos));
        _artPath = path;
        RefreshArt();
        DataEditors.Remember(_spec, "art.path", path);
        Say($"{_art.Count} tile(s) of art from {Path.GetFileName(path)}");
    }

    private void LoadPalette(string path)
    {
        int line = _paletteLine.SelectedIndex < 0 ? 0 : _paletteLine.SelectedIndex;
        byte[] data = File.ReadAllBytes(path);
        _palette     = (uint[])_palette.Clone();
        MdPalette.ReadInto(_palette, data, data.Length >= 128 ? 0 : line);
        _paletteFile = path;
        RefreshArt();
        DataEditors.Remember(_spec, "palette.path", path);
        Say($"Palette from {Path.GetFileName(path)}{(data.Length >= 128 ? string.Empty : $" into line {line}")}");
    }

    private void LoadDplc(string path)
    {
        _dplcKos = false;
        _dplcAsm = IsAsmName(path);
        if (_dplcAsm)
        {
            string text = File.ReadAllText(path);
            if (!SpriteAsm.IsDplc(text)) throw new FormatException($"{Path.GetFileName(path)} is not a DPLC (it has no dplcHeader).");

            try
            {
                _dplc = SpriteAsm.ParseDplc(text);
            }
            catch (FormatException ex)
            {
                throw new FormatException($"{Path.GetFileName(path)}: {ex.Message}", ex);
            }
        }
        else
        {
            if (_dplcSpec is null) throw new InvalidOperationException($"The mappings' editor settings name no DPLC specification ({_bindings["dplc.spec"]}) that can be found: keep one in the Silo, or use the Sonic 2 DPLC template of the Parser Lab.");

            _dplc = SpriteCodec.ReadDplc(_dplcSpec, ReadSource(path, out _dplcKos), _bindings);
        }

        _dplcPath = path;
        while (_dplc.Frames.Count < _map.Frames.Count) _dplc.Frames.Add([]);
        ShowFrame();
        DataEditors.Remember(_spec, "dplc.path", path);
        Say($"DPLC for {_dplc.Frames.Count} frame(s) from {Path.GetFileName(path)}");
    }

    private void SaveMappings()
    {
        if (_mapPath is null)
        {
            SaveMappingsAs();
            return;
        }

        if (_mapAsm) File.WriteAllText(_mapPath, SpriteAsm.WriteMappings(_map, SpriteAsm.TableName(_mapPath, false)));
        else File.WriteAllBytes(_mapPath, Encode(SpriteCodec.WriteMappings(_spec, _map, _bindings), _mapKos));
        _dirty = false;
        UpdateTitle();
        Say($"Saved {_map.Frames.Count} frame(s) to {Path.GetFileName(_mapPath)}");
    }

    private void SaveMappingsAs()
    {
        var dialog = new SaveFileDialog { Title = "Save sprite mappings", Filter = "Mappings (*.bin)|*.bin|Disassembly mappings (*.asm)|*.asm|Kosinski mappings (*.kos)|*.kos|All files (*.*)|*.*", FileName = _mapPath ?? string.Empty };
        if (dialog.ShowDialog(this) != true) return;

        _mapPath = dialog.FileName;
        _mapAsm  = IsAsmName(_mapPath);
        _mapKos  = IsKosName(_mapPath) || (_mapKos && !_mapPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
        DataEditors.Remember(_spec, "mappings.path", _mapPath);
        SaveMappings();
    }

    private void SaveDplc()
    {
        if (_dplc is null) throw new InvalidOperationException("There is no DPLC open.");

        if (_dplcPath is null)
        {
            var dialog = new SaveFileDialog { Title = "Save the DPLC", Filter = "DPLC (*.bin)|*.bin|Disassembly DPLC (*.asm)|*.asm|Kosinski DPLC (*.kos)|*.kos|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) != true) return;

            _dplcPath = dialog.FileName;
            _dplcKos  = IsKosName(_dplcPath);
            _dplcAsm  = IsAsmName(_dplcPath);
        }

        if (_dplcAsm) File.WriteAllText(_dplcPath, SpriteAsm.WriteDplc(_dplc, SpriteAsm.TableName(_dplcPath, true)));
        else if (_dplcSpec is null) throw new InvalidOperationException($"The mappings' editor settings name no DPLC specification ({_bindings["dplc.spec"]}) to write bytes with.");
        else File.WriteAllBytes(_dplcPath, Encode(SpriteCodec.WriteDplc(_dplcSpec, _dplc, _bindings), _dplcKos));
        Say($"Saved the DPLC to {Path.GetFileName(_dplcPath)}");
    }

    /// <summary>Opens again what the specification's editor settings say was open last time.</summary>
    private void ReopenLastFiles()
    {
        foreach (var (key, load) in new (string, Action<string>)[] { ("art.path", LoadArt), ("palette.path", LoadPalette), ("mappings.path", LoadMappings), ("dplc.path", LoadDplc) })
        {
            string path = DataEditorSpec.Setting(_editor, key);
            if (path.Length == 0 || !File.Exists(path)) continue;

            try
            {
                load(path);
            }
            catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException)
            {
                Say($"Could not reopen {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (_frames.Items.Count == 0) Say("Open a mappings file (File > Open Mappings), then the art and palette it uses.");
    }
}
