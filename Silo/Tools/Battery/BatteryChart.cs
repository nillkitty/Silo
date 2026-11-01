using System.Windows.Media;

namespace Silo.Tools.Battery;

/// <summary>The charge over a stretch of time as a line: blue on battery, green charging, orange on mains power without charging, red where it fell on mains power.</summary>
internal sealed class BatteryChart : FrameworkElement
{
    private static readonly Brush Back      = Frozen(0x1E, 0x1F, 0x22);
    private static readonly Brush Grid      = Frozen(0x55, 0x58, 0x5E, 0x90);
    private static readonly Brush OnBattery = Frozen(0x3B, 0x82, 0xD6);
    private static readonly Brush Charging  = Frozen(0x3F, 0xA9, 0x5B);
    private static readonly Brush Plugged   = Frozen(0xE0, 0xA0, 0x30);
    private static readonly Brush Falling   = Frozen(0xE5, 0x48, 0x4D);

    private IReadOnlyList<BatterySample> _samples = [];
    private TimeSpan                     _span    = TimeSpan.FromHours(6);

    public TimeSpan Span
    {
        get => _span;
        set
        {
            _span = value;
            InvalidateVisual();
        }
    }

    public void Show(IReadOnlyList<BatterySample> samples)
    {
        _samples = samples;
        InvalidateVisual();
    }

    private static Brush Frozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Back, null, new Rect(0, 0, w, h));

        var gridPen = new Pen(Grid, 1);
        foreach (int level in new[] { 25, 50, 75 })
        {
            double y = h - h * level / 100.0;
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        if (_samples.Count < 2) return;

        var start = DateTime.UtcNow - _span;
        Point At(BatterySample s) => new(w * (s.Time - start).TotalSeconds / _span.TotalSeconds, h - (h - 2) * s.Percent / 100.0 - 1);

        // a piece of line for each step, coloured by what the computer was doing; a step down while on mains power is red
        for (int i = 1; i < _samples.Count; i++)
        {
            var a     = _samples[i - 1];
            var b     = _samples[i];
            var brush = b.OnAc && b.Percent < a.Percent ? Falling : b.Charging ? Charging : b.OnAc ? Plugged : OnBattery;
            dc.DrawLine(new Pen(brush, ReferenceEquals(brush, Falling) ? 2.5 : 1.8), At(a), At(b));
        }
    }
}
