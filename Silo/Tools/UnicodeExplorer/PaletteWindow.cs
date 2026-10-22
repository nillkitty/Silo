using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using Silo.Hotkeys;
using Silo.Ui.Windows;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     A palette's window: a small always-visible grid of characters. It never takes the focus (so the window the person was typing in keeps it), and
///     a click on a character copies it and, as the Preferences say, types it into that window.
/// </summary>
internal sealed class PaletteWindow : Window
{
    private const int GwlExStyle      = -20;
    private const int WsExNoActivate  = 0x08000000;
    private const double Cell         = 40;

    private readonly PaletteService    _service;
    private readonly PaletteDefinition _palette;
    private readonly WrapPanel         _panel = new() { Margin = new Thickness(4) };
    private readonly TextBlock         _hint  = UiKit.Text("Empty. Right-click to add characters.", muted: true, wrap: true);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLong(IntPtr hWnd, int index, IntPtr value);

    public PaletteWindow(PaletteService service, PaletteDefinition palette)
    {
        _service = service;
        _palette = palette;

        WindowStyle           = WindowStyle.ToolWindow;
        ShowInTaskbar         = false;
        ShowActivated         = false;
        ResizeMode            = ResizeMode.CanResizeWithGrip;
        MinWidth              = 120;
        MinHeight             = 90;
        Focusable             = false;
        this.SetResourceReference(BackgroundProperty, "Theme.Background");
        this.SetResourceReference(ForegroundProperty, "Theme.Text");

        var (w, h) = PaletteRules.SuggestSize(palette.CodePoints.Count, Cell);
        Width  = palette.Width ?? w;
        Height = palette.Height ?? h;
        if (palette.Left is { } l && palette.Top is { } t)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            (Left, Top)           = (l, t);
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _panel, Focusable = false };
        var grid = new Grid();
        _hint.Margin = new Thickness(10);
        grid.Children.Add(_hint);
        grid.Children.Add(scroll);
        Content = grid;

        ContextMenu = new ContextMenu();
        ContextMenuOpening += (_, _) => FillWindowMenu(ContextMenu);
        PreviewMouseDown   += (_, _) => _service.Select(_palette);

        SourceInitialized += (_, _) =>
                             {
                                 var hwnd = new WindowInteropHelper(this).Handle;
                                 SetWindowLong(hwnd, GwlExStyle, (IntPtr)((long)GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate));
                                 Apply();
                             };
        LocationChanged += (_, _) => KeepOnScreen();
        SizeChanged     += (_, _) => KeepOnScreen();
        Closing         += (_, _) => Remember();
        Rebuild();
    }

    /// <summary>Applies the palette's choices that the window shows (on top, on screen).</summary>
    public void Apply()
    {
        Topmost = _palette.AlwaysOnTop;
        Title   = _palette.Name;
        KeepOnScreen();
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

    /// <summary>Draws the characters again (after the palette changed).</summary>
    public void Rebuild()
    {
        _panel.Children.Clear();
        foreach (int cp in _palette.CodePoints)
        {
            int value = cp;
            string text = new CodePoint(value).Text;
            var cell = UiKit.Cell(text, 20, Cell, Cell);
            cell.ToolTip = new CodePoint(value).Hex;
            cell.MouseEnter += (_, _) => UiKit.Paint(cell, false, hot: true);
            cell.MouseLeave += (_, _) => UiKit.Paint(cell, false);
            cell.MouseLeftButtonDown += (_, e) =>
                                        {
                                            _service.Pick(text);
                                            e.Handled = true;
                                        };
            cell.ContextMenu = new ContextMenu();
            cell.ContextMenuOpening += (_, _) =>
                                       {
                                           cell.ContextMenu!.Items.Clear();
                                           cell.ContextMenu.Items.Add(UiKit.Item($"Remove {new CodePoint(value).Hex}", () => _service.Remove(_palette, value)));
                                           cell.ContextMenu.Items.Add(UiKit.Rule());
                                           FillCommon(cell.ContextMenu);
                                       };
            _panel.Children.Add(cell);
        }

        _hint.Visibility = _palette.CodePoints.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FillWindowMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        FillCommon(menu);
    }

    private void FillCommon(ContextMenu menu)
    {
        menu.Items.Add(UiKit.Item("Add Characters…", AddCharacters, tip: "Type or paste the characters to add"));
        menu.Items.Add(UiKit.Item("Rename Palette…", () => _service.Rename(_palette, this)));
        menu.Items.Add(UiKit.Rule());
        menu.Items.Add(Toggle("Always on Top", _palette.AlwaysOnTop, v => _palette.AlwaysOnTop = v));
        menu.Items.Add(Toggle("Keep on-screen", _palette.KeepOnScreen, v => _palette.KeepOnScreen = v));
        menu.Items.Add(Toggle("Show on repo load", _palette.ShowOnLoad, v => _palette.ShowOnLoad = v));
        menu.Items.Add(UiKit.Rule());
        menu.Items.Add(UiKit.Item("Close", Close));
        menu.Items.Add(UiKit.Item("Delete Palette…", () => _service.Delete(_palette, this)));
    }

    private MenuItem Toggle(string header, bool on, Action<bool> set)
    {
        var item = new MenuItem { Header = header, IsChecked = on };
        item.Click += (_, _) =>
                      {
                          set(!on);
                          _service.Save();
                          Apply();
                      };
        return item;
    }

    private void AddCharacters()
    {
        string? text = PromptDialog.Ask("Add characters", "The characters to add to " + _palette.Name + " (type or paste them; spaces are skipped):", "Add", string.Empty);
        if (string.IsNullOrEmpty(text)) return;

        _service.Add(_palette, CodePoint.Enumerate(text).Select(c => c.Value).Where(v => v > 0xFFFF || !char.IsWhiteSpace((char)v)));
    }
}
