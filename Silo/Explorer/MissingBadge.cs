using System.Windows.Media;

namespace Silo.Explorer;

/// <summary>
///     The small red circle with a white X drawn in the lower-right corner of an icon for a thing that is known not to exist (a
///     domain name DNS answered NXDOMAIN for). Takes no room when <see cref="IsMissing" /> is false.
/// </summary>
public sealed class MissingBadge : FrameworkElement
{
    public static readonly DependencyProperty IsMissingProperty =
        DependencyProperty.Register(nameof(IsMissing), typeof(bool), typeof(MissingBadge),
                                    new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(MissingBadge),
                                    new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Red  = Frozen(new SolidColorBrush(Color.FromRgb(0xD8, 0x2B, 0x2B)));
    private static readonly Pen   Rim  = Frozen(new Pen(Brushes.White, 1.5));
    private static readonly Pen   Cross = Frozen(new Pen(Brushes.White, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    public MissingBadge()
    {
        IsHitTestVisible = false;
        ToolTip          = null;
    }

    public bool IsMissing
    {
        get => (bool)GetValue(IsMissingProperty);
        set => SetValue(IsMissingProperty, value);
    }

    /// <summary>The badge's size in device-independent pixels.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return IsMissing ? new Size(Size, Size) : new Size(0, 0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!IsMissing || Size <= 0) return;

        dc.PushTransform(new ScaleTransform(Size / 24, Size / 24));
        dc.DrawEllipse(Red, Rim, new Point(12, 12), 11, 11);
        dc.DrawLine(Cross, new Point(7.5, 7.5), new Point(16.5, 16.5));
        dc.DrawLine(Cross, new Point(16.5, 7.5), new Point(7.5, 16.5));
        dc.Pop();
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
