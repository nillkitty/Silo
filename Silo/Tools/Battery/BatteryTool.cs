using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Telefrag.DI;

namespace Silo.Tools.Battery;

/// <summary>
///     Battery Monitor: a small palette that stays on top of other windows, by default at the bottom right of the screen's work area, just
///     above the taskbar clock. It draws the battery's charge over the last hour, six hours or day, and says so when the charge falls while
///     the computer is on mains power or charging.
/// </summary>
[Singleton(typeof(ITool))]
public sealed class BatteryTool : ToolBase
{
    private const double Margin = 8;

    private BatteryHistory? _history;
    private BatteryChart?   _chart;
    private TextBlock?      _status;
    private TextBlock?      _warning;
    private Window?         _palette;
    private bool            _snapped = true;

    public BatteryTool() : base("batterymonitor")
    {
        DisplayName = "_Battery Monitor";
        GroupName   = "System";
        Icon        = IconRef.FromKey("BatteryCharge");
    }

    public override bool IsChecked => _palette?.IsVisible ?? false;

    protected override UIElement OnBuildContent()
    {
        _history         ??= new BatteryHistory();
        _history.Changed +=  (_, _) => Application.Current?.Dispatcher.BeginInvoke(Refresh);

        var chart = _chart = new BatteryChart { Height = 56 };
        _status = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
        _warning = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var panel = new StackPanel { Margin = new Thickness(8, 6, 8, 6), Background = Brushes.Transparent };
        panel.Children.Add(_status);
        panel.Children.Add(chart);
        panel.Children.Add(_warning);

        var menu = new ContextMenu();
        foreach (var (title, span) in new[] { ("Last hour", TimeSpan.FromHours(1)), ("Last 6 hours", TimeSpan.FromHours(6)), ("Last 24 hours", TimeSpan.FromHours(24)) })
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) =>
                          {
                              chart.Span = span;
                              Refresh();
                          };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var snap = new MenuItem { Header = "Snap to the clock" };
        snap.Click += (_, _) => Snap();
        menu.Items.Add(snap);
        var close = new MenuItem { Header = "Close" };
        close.Click += (_, _) => Toggle();
        menu.Items.Add(close);
        panel.ContextMenu = menu;

        Refresh();
        return panel;
    }

    private void Refresh()
    {
        if (_history is null || _chart is null || _status is null || _warning is null) return;

        if (!_history.HasBattery)
        {
            _status.Text = "No battery";
            _chart.Show([]);
            _warning.Visibility = Visibility.Collapsed;
            return;
        }

        var now = _history.Latest;
        _chart.Show(_history.Last(_chart.Span));
        if (now is null)
        {
            _status.Text = "Reading the battery...";
            return;
        }

        string state = now.Charging ? "charging" : now.OnAc ? "on AC" : "on battery";
        string left  = !now.OnAc && _history.TimeLeft is { } t ? $"  ·  {(int)t.TotalHours}h {t.Minutes:00}m left" : string.Empty;
        _status.Text = $"{now.Percent}%  {state}{left}";

        string? warning = _history.Warning();
        _warning.Text       = warning ?? string.Empty;
        _warning.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Opens the palette, or closes it when it is open.</summary>
    public override Window? Toggle()
    {
        var d = App.Instance!.Dispatcher;
        if (!d.CheckAccess()) return d.Invoke(Toggle);

        if (_palette is { IsVisible: true })
        {
            _palette.Hide();
            OnPropertyChanged(nameof(IsChecked));
            return null;
        }

        if (_palette is null)
        {
            var frame = new Border
                        {
                            Child           = BuildPaneContent(),
                            BorderThickness = new Thickness(1),
                            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x55, 0x58, 0x5E)),
                            Background      = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22))
                        };
            _palette = new Window
                       {
                           Title         = "Battery Monitor",
                           Content       = frame,
                           WindowStyle   = WindowStyle.None,
                           ResizeMode    = ResizeMode.NoResize,
                           ShowInTaskbar = false,
                           Topmost       = true,
                           ShowActivated = false,
                           SizeToContent = SizeToContent.WidthAndHeight,
                           MinWidth      = 250
                       };
            _palette.MouseLeftButtonDown += (_, e) =>
                                            {
                                                if (e.ButtonState != MouseButtonState.Pressed) return;

                                                _snapped = false; // moved by hand: it stays where it was put
                                                _palette.DragMove();
                                            };
            _palette.SizeChanged += (_, _) =>
                                    {
                                        if (_snapped) Snap();
                                    };
            _palette.IsVisibleChanged += (_, _) => OnPropertyChanged(nameof(IsChecked));
            SystemParameters.StaticPropertyChanged += (_, e) =>
                                                      {
                                                          if (e.PropertyName == nameof(SystemParameters.WorkArea) && _snapped) _palette.Dispatcher.BeginInvoke(Snap);
                                                      };
        }

        _snapped = true;
        _palette.Show();
        Snap();
        Refresh();
        OnPropertyChanged(nameof(IsChecked));
        return _palette;
    }

    // the bottom right of the work area (the screen less the taskbar): the corner by the taskbar clock when the taskbar is along the bottom
    private void Snap()
    {
        if (_palette is null) return;

        var area = SystemParameters.WorkArea;
        _palette.Left = area.Right - _palette.ActualWidth - Margin;
        _palette.Top  = area.Bottom - _palette.ActualHeight - Margin;
        _snapped      = true;
    }
}
