using System.Windows.Media;
using Silo.Design;

namespace Silo.Explorer;

/// <summary>
///     The small badge drawn in the lower-right corner of an icon to say which database the thing is kept in:
///     Z a wireframe globe (blue), Y a cloud (green), W a globe with continents (the theme's text colour, turned differently for
///     each world), X a warning triangle (red-orange, "temporary"), U stars (purple). Each is drawn on a 24 x 24 grid and
///     scaled to <see cref="Size" />, with a round backing in the theme's background so it reads over any icon.
/// </summary>
public sealed class RealmBadge : FrameworkElement
{
    public static readonly DependencyProperty RealmProperty =
        DependencyProperty.Register(nameof(Realm), typeof(Realm), typeof(RealmBadge),
                                    new FrameworkPropertyMetadata(Realm.None, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnRealmChanged));

    public static readonly DependencyProperty WorldKeyProperty =
        DependencyProperty.Register(nameof(WorldKey), typeof(string), typeof(RealmBadge),
                                    new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnRealmChanged));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(RealmBadge),
                                    new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>What the badge stands behind: the theme's background (set from <c>Theme.Background</c>).</summary>
    public static readonly DependencyProperty BackdropProperty =
        DependencyProperty.Register(nameof(Backdrop), typeof(Brush), typeof(RealmBadge),
                                    new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The colour of the world badge: the theme's text colour (set from <c>Theme.Text</c>).</summary>
    public static readonly DependencyProperty InkProperty =
        DependencyProperty.Register(nameof(Ink), typeof(Brush), typeof(RealmBadge),
                                    new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush BlueBrush   = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xDE)));
    private static readonly Brush GreenBrush  = Frozen(new SolidColorBrush(Color.FromRgb(0x3F, 0xA3, 0x4D)));
    private static readonly Brush OrangeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x5C, 0x1F)));
    private static readonly Brush PurpleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0x4B, 0xD1)));

    private static readonly Geometry Cloud = Frozen(new GeometryGroup
                                                    {
                                                        Children =
                                                        [
                                                            new EllipseGeometry(new Point(8.2, 14.6), 4.3, 4.3),
                                                            new EllipseGeometry(new Point(12.6, 10.4), 5.3, 5.3),
                                                            new EllipseGeometry(new Point(17.2, 14), 4.3, 4.3),
                                                            new RectangleGeometry(new Rect(8.2, 14, 9, 4.9))
                                                        ]
                                                    });

    private static readonly Geometry Warning = Frozen(Geometry.Parse("M12,2.8 L22.2,20.4 L1.8,20.4 Z"));

    // the land of the world badge: three strips of continents on a band as wide as the globe is round (24), repeated so it can be turned
    private static readonly Geometry Land = Frozen(Geometry.Parse(
        "M3,6 L7,5 L8.5,8 L6.5,11 L8,14 L7,18 L5,16 L5.5,12 L3,9 Z " +
        "M12,5 L16,5.5 L17,8 L15.5,10 L17,14 L15,18 L13,16 L12.5,12 L11,9 Z " +
        "M19,6 L23,7 L22.5,11 L20,10 Z M20,15 L22.5,15 L22,17.5 L20,17 Z"));

    private static readonly Geometry Stars = Frozen(new GeometryGroup
                                                    {
                                                        Children =
                                                        [
                                                            Star(10.5, 13, 8, 2.2), Star(18.8, 6.4, 4.2, 1.3), Star(18, 18.2, 3.2, 1),
                                                            new EllipseGeometry(new Point(4.8, 5.4), 1.1, 1.1)
                                                        ]
                                                    });

    public RealmBadge()
    {
        IsHitTestVisible = false;
        SetResourceReference(BackdropProperty, "Theme.Background");
        SetResourceReference(InkProperty,      "Theme.Text");
    }

    public Realm Realm
    {
        get => (Realm)GetValue(RealmProperty);
        set => SetValue(RealmProperty, value);
    }

    /// <summary>For a world badge: the key of the world; it decides how the globe is turned.</summary>
    public string? WorldKey
    {
        get => (string?)GetValue(WorldKeyProperty);
        set => SetValue(WorldKeyProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Brush? Backdrop
    {
        get => (Brush?)GetValue(BackdropProperty);
        set => SetValue(BackdropProperty, value);
    }

    public Brush? Ink
    {
        get => (Brush?)GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    /// <summary>
    ///     How far the globe of a world is turned, in grid units (0 to 24) east-west and in tilt (-1.5 to 1.5) north-south. Stable: the
    ///     same key always gives the same turn, on every machine and run (a plain string hash is not, so this is FNV-1a). A world
    ///     without a key is not turned.
    /// </summary>
    public static (double Turn, double Tilt) RotationFor(string? worldKey)
    {
        if (string.IsNullOrEmpty(worldKey)) return (0, 0);

        uint hash = 2166136261;
        foreach (char c in worldKey.ToLowerInvariant())
        {
            hash ^= c;
            hash *= 16777619;
        }

        double turn = hash % 24;
        double tilt = ((int)((hash >> 8) % 7) - 3) * 0.5; // -1.5 .. 1.5
        return (turn, tilt);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return Realm == Realm.None ? new Size(0, 0) : new Size(Size, Size);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var realm = Realm;
        if (realm == Realm.None || Size <= 0) return;

        dc.PushTransform(new ScaleTransform(Size / 24, Size / 24));

        // a round backing, so the badge stands out from the icon under it
        dc.PushOpacity(0.92);
        dc.DrawEllipse(Backdrop ?? Brushes.White, null, new Point(12, 12), 12, 12);
        dc.Pop();

        switch (realm)
        {
            case Realm.Global:
                DrawWireGlobe(dc);
                break;
            case Realm.Meta:
                dc.DrawGeometry(GreenBrush, null, Cloud);
                break;
            case Realm.World:
                DrawWorld(dc);
                break;
            case Realm.Scratch:
                DrawWarning(dc);
                break;
            case Realm.Universal:
                dc.DrawGeometry(PurpleBrush, null, Stars);
                break;
        }

        dc.Pop();
    }

    private static void OnRealmChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var badge = (RealmBadge)d;
        badge.ToolTip = badge.Realm == Realm.None ? null : Realms.Describe(badge.Realm, badge.WorldKey);
    }

    private static void DrawWireGlobe(DrawingContext dc)
    {
        var pen = new Pen(BlueBrush, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var c   = new Point(12, 12);

        dc.DrawEllipse(null, pen, c, 9.2, 9.2);
        dc.DrawEllipse(null, pen, c, 4.2, 9.2);
        dc.DrawLine(pen, new Point(2.8, 12),   new Point(21.2, 12));
        dc.DrawLine(pen, new Point(4.4, 7.4),  new Point(19.6, 7.4));
        dc.DrawLine(pen, new Point(4.4, 16.6), new Point(19.6, 16.6));
    }

    private void DrawWorld(DrawingContext dc)
    {
        var ink = Ink ?? Brushes.Black;
        var (turn, tilt) = RotationFor(WorldKey);

        dc.DrawEllipse(null, new Pen(ink, 1.5), new Point(12, 12), 9.2, 9.2);

        // the land, turned: the band slides east and wraps round, and is clipped to the globe
        dc.PushClip(new EllipseGeometry(new Point(12, 12), 8.6, 8.6));
        foreach (double shift in new[] { turn - 24, turn })
        {
            dc.PushTransform(new TranslateTransform(shift, tilt));
            dc.DrawGeometry(ink, null, Land);
            dc.Pop();
        }

        dc.Pop();
    }

    private static void DrawWarning(DrawingContext dc)
    {
        var pen = new Pen(OrangeBrush, 1.6) { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(OrangeBrush, pen, Warning);

        // the exclamation mark
        dc.DrawRoundedRectangle(Brushes.White, null, new Rect(10.9, 8.4, 2.2, 6.4), 1, 1);
        dc.DrawEllipse(Brushes.White, null, new Point(12, 17.5), 1.25, 1.25);
    }

    /// <summary>A four-pointed star (eight points, alternately far and near).</summary>
    private static Geometry Star(double cx, double cy, double outer, double inner)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < 8; i++)
            {
                double angle  = Math.PI / 4 * i - Math.PI / 2;
                double radius = i % 2 == 0 ? outer : inner;
                var    point  = new Point(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
                if (i == 0) ctx.BeginFigure(point, true, true);
                else ctx.LineTo(point, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
