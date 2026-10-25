using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Silo.Design;
using Silo.Ui.Windows;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     A palette's window: a small always-visible grid of characters, on one or more pages (tabs). It never takes the focus (so the window the person was
///     typing in keeps it), and a click on a character copies it and, as the Preferences say, types it into that window.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>Its size snaps to whole tiles while it is resized, so there is no strip of dead space beside the last column or under the last row.</item>
///         <item>Tiles can be dragged to rearrange them, onto another palette or the explorer's collection, or into another program (as text).</item>
///         <item>
///             Lock Palette (right-click) stops changes; what it stops (size, position, tiles, tabs) and the tile size, colors, taskbar button and
///             on-top choice are on its Author-mode menu (Ctrl+right-click).
///         </item>
///     </list>
/// </remarks>
internal sealed class PaletteWindow : Window
{
    private const int    GwlExStyle     = -20;
    private const int    WsExNoActivate = 0x08000000;
    private const int    WsExAppWindow  = 0x00040000;
    private const int    WmSizing       = 0x0214;
    private const int    WmMoving       = 0x0216;
    private const double PanelPadding   = 4;  // the tile panel's margin on each side
    private const double TileMargin     = 1;  // each tile's margin on each side (UiKit.Cell)

    private readonly PaletteService    _service;
    private readonly PaletteDefinition _palette;
    private readonly WrapPanel         _panel = new() { Margin = new Thickness(PanelPadding) };
    private readonly WrapPanel         _tabs  = new() { Margin = new Thickness(4, 4, 4, 0) };
    private readonly ScrollViewer      _scroll;
    private readonly TextBlock         _hint  = UiKit.Text("Empty. Right-click to add characters, or drag them here.", muted: true, wrap: true);

    private Point? _pressAt;      // where the left button went down on a tile (a click picks it; a drag moves it)
    private int    _pressIndex = -1;
    private int    _snapping;     // re-snaps in a row (a scroll bar coming or going changes the room); stops a loop

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLong(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    public PaletteWindow(PaletteService service, PaletteDefinition palette)
    {
        _service = service;
        _palette = palette;

        ShowActivated = false;
        MinWidth      = 80;
        MinHeight     = 60;
        Focusable     = false;
        this.SetResourceReference(BackgroundProperty, "Theme.Background");
        this.SetResourceReference(ForegroundProperty, "Theme.Text");

        var (w, h) = PaletteRules.SuggestSize(palette.Page().CodePoints.Count, Pitch);
        Width  = palette.Width ?? w;
        Height = palette.Height ?? h;
        if (palette.Left is { } l && palette.Top is { } t)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            (Left, Top)           = (l, t);
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _panel, Focusable = false };
        var tiles = new Grid { Background = Brushes.Transparent, AllowDrop = true }; // transparent: the empty area takes drops too
        _hint.Margin = new Thickness(10);
        tiles.Children.Add(_hint);
        tiles.Children.Add(_scroll);
        tiles.DragEnter += OnTileDragOver;
        tiles.DragOver  += OnTileDragOver;
        tiles.DragLeave += (_, _) => GlyphDrag.Unmark(_panel);
        tiles.Drop      += OnTileDrop;

        var root = new DockPanel { DataContext = new DesignSource(this) }; // Ctrl+right-click: the Author menu (see DesignSource)
        DockPanel.SetDock(_tabs, Dock.Top);
        root.Children.Add(_tabs);
        root.Children.Add(tiles);
        Content = root;

        ContextMenu = new ContextMenu();
        ContextMenuOpening += (_, _) => FillWindowMenu(ContextMenu);
        PreviewMouseDown   += (_, _) => _service.Select(_palette);

        SourceInitialized += (_, _) =>
                             {
                                 var hwnd = new WindowInteropHelper(this).Handle;
                                 HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
                                 Apply();
                             };
        Loaded          += (_, _) => Dispatcher.BeginInvoke(Snap, System.Windows.Threading.DispatcherPriority.Loaded);
        LocationChanged += (_, _) => KeepOnScreen();
        SizeChanged     += (_, _) => KeepOnScreen();
        _scroll.ScrollChanged += (_, e) =>
                                 {
                                     // a scroll bar came or went: the room for tiles changed, so the size is snapped again (once or twice, not forever)
                                     if (e.ViewportWidthChange != 0 && _snapping < 2) Dispatcher.BeginInvoke(Snap, System.Windows.Threading.DispatcherPriority.Loaded);
                                 };
        Closing += (_, _) => Remember();
        Rebuild();
    }

    /// <summary>The distance from one tile to the next.</summary>
    private double Pitch => _palette.TileSize + 2 * TileMargin;

    internal PaletteDefinition Palette => _palette;

    /// <summary>Applies the palette's choices that the window shows (title, on top, taskbar, size lock, on screen).</summary>
    public void Apply()
    {
        Topmost       = _palette.AlwaysOnTop;
        Title         = _palette.Name;
        ShowInTaskbar = _palette.ShowInTaskbar;
        WindowStyle   = _palette.ShowInTaskbar ? WindowStyle.SingleBorderWindow : WindowStyle.ToolWindow; // a tool window never gets a taskbar button
        ResizeMode    = _palette.SizeIsLocked() ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        ApplyExtendedStyle();
        KeepOnScreen();
    }

    /// <summary>
    ///     The window never takes the focus (WS_EX_NOACTIVATE), and has a taskbar button only when asked (WS_EX_APPWINDOW: a window that is never
    ///     activated gets none otherwise). Set again after every change of style, since WPF writes the extended style itself when it changes one.
    /// </summary>
    private void ApplyExtendedStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        long style = (long)GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate;
        style = _palette.ShowInTaskbar ? style | WsExAppWindow : style & ~WsExAppWindow;
        SetWindowLong(hwnd, GwlExStyle, (IntPtr)style);
    }

    private void KeepOnScreen()
    {
        if (!_palette.KeepOnScreen || double.IsNaN(Left) || double.IsNaN(Top)) return;

        var (l, t) = PaletteRules.KeepOnScreen(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height,
                                               SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (Math.Abs(l - Left) > 0.5) Left = l;
        if (Math.Abs(t - Top) > 0.5) Top = t;
    }

    /// <summary>Notes where the window is, so it comes back there.</summary>
    private void Remember()
    {
        if (WindowState != WindowState.Normal) return;

        _palette.Left   = Left;
        _palette.Top    = Top;
        _palette.Width  = Width;
        _palette.Height = Height;
        _service.Save();
    }

    // ---- size: whole tiles -----------------------------------------------------------------------------------------

    /// <summary>What the window needs beyond the tiles themselves (its frame, the tabs, a scroll bar, the panel's padding), in DIPs; null before layout.</summary>
    private (double Width, double Height)? Overhead()
    {
        if (_scroll.ViewportWidth <= 0 || _scroll.ViewportHeight <= 0 || ActualWidth <= 0) return null;

        return (ActualWidth - _scroll.ViewportWidth + 2 * PanelPadding, ActualHeight - _scroll.ViewportHeight + 2 * PanelPadding);
    }

    /// <summary>Makes the window exactly a whole number of tiles wide and high (the nearest to its size now).</summary>
    private void Snap()
    {
        if (Overhead() is not { } over || WindowState != WindowState.Normal) return;

        _snapping++;
        try
        {
            int columns = PaletteRules.Fit(ActualWidth - over.Width, Pitch);
            int rows    = PaletteRules.Fit(ActualHeight - over.Height, Pitch);
            SetTiles(columns, rows, over);
        }
        finally
        {
            Dispatcher.BeginInvoke(() => _snapping = 0, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    private void SetTiles(int columns, int rows, (double Width, double Height) over)
    {
        double width  = over.Width + columns * Pitch;
        double height = over.Height + rows * Pitch;
        if (Math.Abs(width - Width) > 0.5) Width = width;
        if (Math.Abs(height - Height) > 0.5) Height = height;
    }

    /// <summary>A new tile size, keeping the same number of columns and rows.</summary>
    internal void SetTileSize(double size)
    {
        var over    = Overhead();
        int columns = over is { } o ? PaletteRules.Fit(ActualWidth - o.Width, Pitch) : 0;
        int rows    = over is { } o2 ? PaletteRules.Fit(ActualHeight - o2.Height, Pitch) : 0;

        _palette.TileSize = Math.Clamp(Math.Round(size), PaletteRules.MinTileSize, PaletteRules.MaxTileSize);
        Rebuild();
        if (over is { } known && !_palette.SizeIsLocked()) SetTiles(columns, rows, known);
        _service.Save();
    }

    /// <summary>
    ///     While the person drags an edge, the new size is rounded to whole tiles (WM_SIZING), moving the edge being dragged; while the position is
    ///     locked, a drag of the title bar leaves the window where it is (WM_MOVING).
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMoving && _palette.PositionIsLocked() && GetWindowRect(hwnd, out var here))
        {
            Marshal.StructureToPtr(here, lParam, false);
            handled = true;
            return new IntPtr(1);
        }

        if (msg == WmSizing && Overhead() is { } over)
        {
            var    rect  = Marshal.PtrToStructure<Rect32>(lParam);
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double scaleY = VisualTreeHelper.GetDpi(this).DpiScaleY;

            int columns = PaletteRules.Fit((rect.Right - rect.Left) / scale - over.Width, Pitch);
            int rows    = PaletteRules.Fit((rect.Bottom - rect.Top) / scaleY - over.Height, Pitch);
            int width   = (int)Math.Round((over.Width + columns * Pitch) * scale);
            int height  = (int)Math.Round((over.Height + rows * Pitch) * scaleY);

            int edge = wParam.ToInt32(); // 1 left, 2 right, 3 top, 4 top-left, 5 top-right, 6 bottom, 7 bottom-left, 8 bottom-right
            if (edge is 1 or 4 or 7) rect.Left = rect.Right - width;
            else rect.Right = rect.Left + width;
            if (edge is 3 or 4 or 5) rect.Top = rect.Bottom - height;
            else rect.Bottom = rect.Top + height;

            Marshal.StructureToPtr(rect, lParam, false);
            handled = true;
            return new IntPtr(1);
        }

        return IntPtr.Zero;
    }

    // ---- the tiles and the tabs ---------------------------------------------------------------------------------------

    /// <summary>Draws the tabs and the characters of the page shown again (after the palette changed).</summary>
    public void Rebuild()
    {
        GlyphDrag.Unmark(_panel);
        RebuildTabs();

        var page   = _palette.Page();
        var colors = _palette.ShowColors ? _service.Colors() : default;
        _panel.Children.Clear();
        for (int i = 0; i < page.CodePoints.Count; i++)
        {
            int    index = i;
            int    value = page.CodePoints[i];
            string text  = new CodePoint(value).Text;
            var    cell  = UiKit.Cell(text, Math.Round(_palette.TileSize * 0.5), _palette.TileSize, _palette.TileSize);
            cell.ToolTip = new CodePoint(value).Hex;
            if (_palette.ShowColors && colors.ColorOf is { } colorOf) UiKit.MarkCell(cell, colorOf(value), colors.Mode);

            cell.MouseEnter += (_, _) => UiKit.Paint(cell, false, hot: true);
            cell.MouseLeave += (_, _) => UiKit.Paint(cell, false);
            cell.MouseLeftButtonDown += (_, e) =>
                                        {
                                            _pressAt    = e.GetPosition(this);
                                            _pressIndex = index;
                                            e.Handled   = true;
                                        };
            cell.MouseMove += (_, e) =>
                              {
                                  if (_pressAt is not { } start || _pressIndex != index || e.LeftButton != MouseButtonState.Pressed) return;
                                  if (!GlyphDrag.FarEnough(start, e.GetPosition(this))) return;

                                  _pressAt = null;
                                  DragTile(cell, index, value);
                              };
            cell.MouseLeftButtonUp += (_, e) =>
                                      {
                                          if (_pressAt is not null && _pressIndex == index) _service.Pick(text); // a click, not a drag
                                          _pressAt  = null;
                                          e.Handled = true;
                                      };
            cell.ContextMenu = new ContextMenu();
            cell.ContextMenuOpening += (_, _) =>
                                       {
                                           cell.ContextMenu!.Items.Clear();
                                           cell.ContextMenu.Items.Add(UiKit.Item($"Remove {new CodePoint(value).Hex}", () => _service.Remove(_palette, value), !_palette.TilesAreLocked()));
                                           cell.ContextMenu.Items.Add(UiKit.Rule());
                                           FillCommon(cell.ContextMenu);
                                       };
            _panel.Children.Add(cell);
        }

        _hint.Visibility = page.CodePoints.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The tab strip: shown when the palette has more than one page; a click shows a page, a drag (when tabs are not locked) moves it.</summary>
    private void RebuildTabs()
    {
        _tabs.Children.Clear();
        _tabs.Visibility = _palette.Pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _palette.Pages.Count; i++)
        {
            int  index = i;
            bool shown = i == _palette.CurrentPage;
            var  tab   = new Border
                         {
                             Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 0, 2, 0), CornerRadius = new CornerRadius(3, 3, 0, 0),
                             BorderThickness = new Thickness(1, 1, 1, shown ? 0 : 1), Cursor = Cursors.Hand, AllowDrop = true,
                             Child = new TextBlock { Text = _palette.Pages[i].Name, FontWeight = shown ? FontWeights.SemiBold : FontWeights.Normal }
                         };
            tab.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
            tab.SetResourceReference(Border.BackgroundProperty, shown ? "Theme.Surface" : "Theme.Background");

            Point? down = null;
            tab.MouseLeftButtonDown += (_, e) =>
                                       {
                                           down      = e.GetPosition(this);
                                           e.Handled = true;
                                       };
            tab.MouseMove += (_, e) =>
                             {
                                 if (down is not { } start || e.LeftButton != MouseButtonState.Pressed || _palette.TabsAreLocked()) return;
                                 if (!GlyphDrag.FarEnough(start, e.GetPosition(this))) return;

                                 down = null;
                                 var data = new DataObject();
                                 data.SetData(PageDragFormat, new PageDrag(_palette, index), false);
                                 DragDrop.DoDragDrop(tab, data, DragDropEffects.Move);
                             };
            tab.MouseLeftButtonUp += (_, _) =>
                                     {
                                         if (down is not null) _service.ShowPage(_palette, index); // a click, not a drag (shown on release, so the tab is still there to drag)
                                         down = null;
                                     };

            // a page dragged onto a tab goes to its place; characters dragged onto a tab show that page, so they can be dropped on it
            tab.DragOver += (_, e) =>
                            {
                                if (e.Data.GetData(PageDragFormat) is PageDrag { } drag && ReferenceEquals(drag.Palette, _palette)) e.Effects = DragDropEffects.Move;
                                else if (GlyphDrag.Read(e.Data) is not null && index != _palette.CurrentPage)
                                {
                                    _service.ShowPage(_palette, index);
                                    e.Effects = DragDropEffects.None;
                                }
                                else e.Effects = DragDropEffects.None;

                                e.Handled = true;
                            };
            tab.Drop += (_, e) =>
                        {
                            if (e.Data.GetData(PageDragFormat) is PageDrag { } drag && ReferenceEquals(drag.Palette, _palette)) _service.MovePage(_palette, drag.Index, index);
                            e.Handled = true;
                        };

            tab.ContextMenu = new ContextMenu();
            tab.ContextMenuOpening += (_, _) =>
                                      {
                                          _service.ShowPage(_palette, index);
                                          tab.ContextMenu!.Items.Clear();
                                          foreach (var item in PageItems()) tab.ContextMenu.Items.Add(item);
                                      };
            _tabs.Children.Add(tab);
        }
    }

    private const string PageDragFormat = "Silo.PalettePage";

    private sealed record PageDrag(PaletteDefinition Palette, int Index);

    // ---- dragging tiles ----------------------------------------------------------------------------------------------

    private void DragTile(Border cell, int index, int codePoint)
    {
        var page = _palette.Page();
        DragDrop.DoDragDrop(cell, GlyphDrag.Create([codePoint], page, [index]), DragDropEffects.Copy | DragDropEffects.Move);
        GlyphDrag.Unmark(_panel);
    }

    private void OnTileDragOver(object sender, DragEventArgs e)
    {
        var payload = GlyphDrag.Read(e.Data);
        if (payload is null || _palette.TilesAreLocked())
        {
            e.Effects = DragDropEffects.None;
            GlyphDrag.Unmark(_panel);
        }
        else
        {
            e.Effects = ReferenceEquals(payload.Source, _palette.Page()) ? DragDropEffects.Move : DragDropEffects.Copy;
            GlyphDrag.Mark(_panel, GlyphDrag.DropIndex(_panel, e.GetPosition(_panel)));
        }

        e.Handled = true;
    }

    /// <summary>Tiles from this page move to where they are dropped; characters from anywhere else are added there (ones already on the page are not).</summary>
    private void OnTileDrop(object sender, DragEventArgs e)
    {
        GlyphDrag.Unmark(_panel);
        e.Handled = true;
        if (GlyphDrag.Read(e.Data) is not { } payload || _palette.TilesAreLocked()) return;

        int at = GlyphDrag.DropIndex(_panel, e.GetPosition(_panel));
        if (ReferenceEquals(payload.Source, _palette.Page()))
        {
            _service.MoveTiles(_palette, payload.Indexes, at);
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            _service.Insert(_palette, payload.CodePoints, at);
            e.Effects = DragDropEffects.Copy;
        }
    }

    // ---- menus -------------------------------------------------------------------------------------------------------

    private void FillWindowMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        FillCommon(menu);
    }

    private void FillCommon(ContextMenu menu)
    {
        menu.Items.Add(UiKit.Item("Add Characters…", AddCharacters, !_palette.TilesAreLocked(), "Type or paste the characters to add"));
        menu.Items.Add(UiKit.Item("Rename Palette…", () => _service.Rename(_palette, this)));
        if (_palette.Pages.Count > 1 || !_palette.TabsAreLocked())
        {
            var pages = new MenuItem { Header = "Pages" };
            foreach (var item in PageItems()) pages.Items.Add(item);
            menu.Items.Add(pages);
        }

        menu.Items.Add(UiKit.Rule());
        menu.Items.Add(Toggle("Lock Palette", _palette.Locked, v => _palette.Locked = v,
                              "Stop changes to this palette (what is locked is chosen in Author mode: Ctrl+right-click)"));
        menu.Items.Add(Toggle("Always on Top", _palette.AlwaysOnTop, v => _palette.AlwaysOnTop = v));
        menu.Items.Add(Toggle("Keep on-screen", _palette.KeepOnScreen, v => _palette.KeepOnScreen = v));
        menu.Items.Add(Toggle("Show on repo load", _palette.ShowOnLoad, v => _palette.ShowOnLoad = v));
        menu.Items.Add(UiKit.Rule());
        menu.Items.Add(UiKit.Item("Close", Close));
        menu.Items.Add(UiKit.Item("Delete Palette…", () => _service.Delete(_palette, this)));
    }

    /// <summary>Show a page, and (when tabs are not locked) add, rename or delete one.</summary>
    private IEnumerable<Control> PageItems()
    {
        bool editable = !_palette.TabsAreLocked();
        for (int i = 0; i < _palette.Pages.Count; i++)
        {
            int index = i;
            yield return new MenuItem { Header = _palette.Pages[i].Name, IsChecked = i == _palette.CurrentPage, Command = new PickPage(() => _service.ShowPage(_palette, index)) };
        }

        yield return UiKit.Rule();
        yield return UiKit.Item("Add Tab Page…", () => _service.AddPage(_palette, this), editable);
        yield return UiKit.Item("Rename Current Page…", () => _service.RenamePage(_palette, this), editable);
        yield return UiKit.Item("Delete Current Page…", () => _service.DeletePage(_palette, this), editable && _palette.Pages.Count > 1);
    }

    private sealed class PickPage(Action run) : ICommand
    {
        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run();

#pragma warning disable CS0067
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
    }

    private MenuItem Toggle(string header, bool on, Action<bool> set, string? tip = null)
    {
        var item = new MenuItem { Header = header, IsChecked = on, ToolTip = tip };
        item.Click += (_, _) =>
                      {
                          set(!on);
                          _service.Restyled(_palette);
                      };
        return item;
    }

    private void AddCharacters()
    {
        string? text = PromptDialog.Ask("Add characters", "The characters to add to " + _palette.Name + " (type or paste them; spaces are skipped):", "Add", string.Empty);
        if (string.IsNullOrEmpty(text)) return;

        _service.Add(_palette, CodePoint.Enumerate(text).Select(c => c.Value).Where(v => v > 0xFFFF || !char.IsWhiteSpace((char)v)));
    }

    // ---- Author mode --------------------------------------------------------------------------------------------------

    /// <summary>The palette's Author-mode menu (Ctrl+right-click anywhere in the window).</summary>
    private sealed class DesignSource(PaletteWindow window) : IDesignMenuSource
    {
        private PaletteDefinition P => window._palette;

        public string DesignTypeName => "PaletteWindow — " + P.Name;

        public IEnumerable<DesignMenuItem> DesignMenuItems()
        {
            var service = window._service;
            void Changed() => service.Restyled(P);

            yield return DesignMenuItem.Slider("Glyph size", DesignLevel.Author, PaletteRules.MinTileSize, PaletteRules.MaxTileSize,
                                               () => P.TileSize, window.SetTileSize, "{0:0} px");
            yield return DesignMenuItem.Separator();

            yield return DesignMenuItem.Info("Palette Lock");
            yield return DesignMenuItem.Toggle("Palette is locked", DesignLevel.Author, () => P.Locked, v => { P.Locked = v; Changed(); });
            yield return DesignMenuItem.Toggle("Lock size", DesignLevel.Author, () => P.LockSize, v => { P.LockSize = v; Changed(); });
            yield return DesignMenuItem.Toggle("Lock position", DesignLevel.Author, () => P.LockPosition, v => { P.LockPosition = v; Changed(); });
            yield return DesignMenuItem.Toggle("Lock tiles", DesignLevel.Author, () => P.LockTiles, v => { P.LockTiles = v; Changed(); });
            yield return DesignMenuItem.Toggle("Lock tabs", DesignLevel.Author, () => P.LockTabs, v => { P.LockTabs = v; Changed(); });
            yield return DesignMenuItem.Separator();

            yield return DesignMenuItem.Toggle("Show glyph colors", DesignLevel.Author, () => P.ShowColors, v => { P.ShowColors = v; Changed(); });
            yield return DesignMenuItem.Toggle("Show in taskbar", DesignLevel.Author, () => P.ShowInTaskbar, v => { P.ShowInTaskbar = v; Changed(); });
            yield return DesignMenuItem.Toggle("Topmost", DesignLevel.Author, () => P.AlwaysOnTop, v => { P.AlwaysOnTop = v; Changed(); });
            yield return DesignMenuItem.Separator();

            bool editable = !P.TabsAreLocked();
            yield return DesignMenuItem.Info(editable ? "Tabs (drag to reorder)" : "Tabs (locked)");
            yield return new DesignMenuItem { MinLevel = DesignLevel.Author, Content = TabList(editable) };
            yield return DesignMenuItem.Run("Add tab page…", () => service.AddPage(P, window), DesignLevel.Author, editable);
            yield return DesignMenuItem.Run("Rename current page…", () => service.RenamePage(P, window), DesignLevel.Author, editable);
            yield return DesignMenuItem.Run("Delete current page…", () => service.DeletePage(P, window), DesignLevel.Author, editable && P.Pages.Count > 1);
        }

        /// <summary>
        ///     The pages as a list in the menu: click one to show it, drag one up or down to move it (the menu stays open). Done with mouse capture,
        ///     not drag and drop, since the list lives in the menu's popup.
        /// </summary>
        private UIElement TabList(bool editable)
        {
            var list = new StackPanel { MinWidth = 180, Margin = new Thickness(0, 2, 0, 2) };
            int dragging = -1;

            void Fill()
            {
                list.Children.Clear();
                for (int i = 0; i < P.Pages.Count; i++)
                {
                    bool shown = i == P.CurrentPage;
                    list.Children.Add(new Border
                                      {
                                          Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 1, 0, 1), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
                                          BorderBrush = DesignMenu.PinkBorder, Background = shown ? DesignMenu.PinkBorder : Brushes.Transparent,
                                          Cursor = editable ? Cursors.SizeNS : Cursors.Hand,
                                          Child = new TextBlock { Text = (editable ? "≡  " : "") + P.Pages[i].Name, Foreground = shown ? Brushes.White : DesignMenu.PinkText }
                                      });
                }
            }

            int RowAt(MouseEventArgs e)
            {
                for (int i = 0; i < list.Children.Count; i++)
                    if (list.Children[i] is FrameworkElement row && e.GetPosition(row).Y < row.ActualHeight)
                        return i;
                return list.Children.Count - 1;
            }

            list.MouseLeftButtonDown += (_, e) =>
                                        {
                                            int row = RowAt(e);
                                            if (row < 0) return;

                                            window._service.ShowPage(P, row);
                                            dragging = editable ? row : -1;
                                            if (dragging >= 0) list.CaptureMouse();
                                            Fill();
                                            e.Handled = true;
                                        };
            list.MouseMove += (_, e) =>
                              {
                                  if (dragging < 0 || e.LeftButton != MouseButtonState.Pressed) return;

                                  int to = Math.Clamp(RowAt(e), 0, P.Pages.Count - 1);
                                  if (to == dragging) return;

                                  window._service.MovePage(P, dragging, to);
                                  dragging = to;
                                  Fill();
                              };
            list.MouseLeftButtonUp += (_, e) =>
                                      {
                                          dragging = -1;
                                          list.ReleaseMouseCapture();
                                          e.Handled = true;
                                      };
            Fill();
            return list;
        }
    }
}
