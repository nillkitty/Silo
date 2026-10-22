using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Silo.Storage;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     Colors and the choices kept with them: a character can be marked with one of sixteen colors (from its context menu, or the swatches under
///     the details), the mark shows wherever the character does (the list, the collection, the details) and the list can be filtered by it. The marks
///     belong to the character, not to a collection, and are kept in the Silo.
/// </summary>
public sealed partial class UnicodeExplorerView
{
    private const string ColorsKey     = "unicodeexplorer:colors";

    private CharacterColorMap _colors = new();
    private readonly List<Border> _swatches = [];
    private readonly TextBlock    _colorName = UiKit.Text(string.Empty, muted: true);

    // ---- keeping them --------------------------------------------------------------------------------------------

    private void LoadColors()
    {
        try
        {
            var stored = Stored.At<Dictionary<int, int>>(ColorsKey);
            _colors = stored.IsAvailable ? CharacterColorMap.From(stored.Get()) : new CharacterColorMap();
        }
        catch (Exception)
        {
            _colors = new CharacterColorMap(); // unreadable: start without
        }

        _view.Refresh();
        if (_tray.Count > 0) RefreshTray();
        UpdateColorStrip();
    }

    private void SaveColors()
    {
        try
        {
            var stored = Stored.At<Dictionary<int, int>>(ColorsKey);
            if (!stored.IsAvailable) return;

            if (_colors.Count == 0) stored.Delete();
            else stored.Set(_colors.ToDictionary());
        }
        catch (Exception ex)
        {
            Say("The colors could not be kept in the Silo: " + ex.Message);
        }
    }

    // ---- showing them --------------------------------------------------------------------------------------------

    private Color? ColorOfCodePoint(int codePoint) => _colors.ColorOf(codePoint) is { } c ? UiKit.ToColor(c) : null;

    private static Rectangle Swatch(CharacterColor color, double size)
    {
        return new Rectangle { Width = size, Height = size, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(UiKit.ToColor(color)) };
    }

    /// <summary>The color filter: null for none, -1 any mark, -2 no mark, else a palette index.</summary>
    private int? ColorFilter => _filter.ColorFilter;

    // ---- marking -------------------------------------------------------------------------------------------------

    /// <summary>
    ///     The characters a color chosen in the details applies to: all the selected ones when several are selected where the shown one was picked
    ///     (the collection or the results), else the one shown.
    /// </summary>
    private IReadOnlyList<int> ColorTargets()
    {
        // picked in the collection: every selected collection character (repeats in the collection count once)
        if (_inspectedFromTray && _traySel.Count > 1)
            return _traySel.OrderBy(i => i).Where(i => i < _tray.Count).Select(i => _tray[i].CodePoint.Value).Distinct().ToArray();

        if (Inspected is not { } shown) return [];

        bool oneOfSeveral = _sel.Count > 1 && _selectedIndex >= 0 && _selectedIndex < _results.Length && _results[_selectedIndex] == shown.Value && _sel.Contains(_selectedIndex);
        return oneOfSeveral ? SelectedCodePoints() : [shown.Value];
    }

    private void MarkCodePoints(IReadOnlyList<int> codePoints, int? index)
    {
        if (codePoints.Count == 0) return;

        if (_colors.Set(codePoints, index))
        {
            SaveColors();
            _view.Refresh();
            if (_tray.Count > 0) RefreshTray();
            if (ColorFilter is not null) Schedule(immediate: true);
        }

        UpdateColorStrip();
        string what = codePoints.Count == 1 ? new CodePoint(codePoints[0]).Hex : $"{codePoints.Count:N0} characters";
        Say(index is null ? $"Took the color off {what}." : $"Marked {what} {CharacterColors.At(index)!.Name.ToLowerInvariant()}.");
    }

    /// <summary>A "Color" submenu: the palette, and none.</summary>
    private MenuItem ColorMenu(Func<IReadOnlyList<int>> targets)
    {
        var root = new MenuItem { Header = "Color" };
        foreach (var c in CharacterColors.Palette)
        {
            int index = c.Index;
            var item  = new MenuItem { Header = c.Name, Icon = Swatch(c, 12) };
            item.Click += (_, _) => MarkCodePoints(targets(), index);
            root.Items.Add(item);
        }

        root.Items.Add(new Separator());
        root.Items.Add(UiKit.Item("No color", () => MarkCodePoints(targets(), null)));
        return root;
    }

    /// <summary>The swatches under the details: click one to mark the character, the cross to take the mark off.</summary>
    private UIElement BuildColorStrip()
    {
        var strip = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var label = UiKit.Text("Color", muted: true);
        label.Margin = new Thickness(0, 0, 6, 0);
        strip.Children.Add(label);

        foreach (var c in CharacterColors.Palette)
        {
            int index  = c.Index;
            var swatch = new Border
                         {
                             Width = 16, Height = 16, Margin = new Thickness(0, 0, 3, 0), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
                             Background = new SolidColorBrush(UiKit.ToColor(c)), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = c.Name + " (click again to take the color off)"
                         };
            swatch.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
            swatch.MouseLeftButtonUp += (_, _) =>
                                        {
                                            var targets = ColorTargets();
                                            if (targets.Count == 0) return;

                                            // clicking the color the characters already all have takes it off
                                            bool same = targets.All(t => _colors.Get(t) == index);
                                            MarkCodePoints(targets, same ? null : index);
                                        };
            _swatches.Add(swatch);
            strip.Children.Add(swatch);
        }

        var none = new TextBlock { Text = "✕", Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Take the color off", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) }
            .Themed(TextBlock.ForegroundProperty, "Theme.TextMuted");
        none.MouseLeftButtonUp += (_, _) => MarkCodePoints(ColorTargets(), null);
        strip.Children.Add(none);
        strip.Children.Add(_colorName);
        return strip;
    }

    /// <summary>Rings the swatch of the color the shown character has.</summary>
    private void UpdateColorStrip()
    {
        int? mine = Inspected is { } c ? _colors.Get(c.Value) : null;
        for (int i = 0; i < _swatches.Count; i++)
        {
            bool on = mine == i;
            _swatches[i].BorderThickness = new Thickness(on ? 2 : 1);
            _swatches[i].SetResourceReference(Border.BorderBrushProperty, on ? "Theme.Text" : "Theme.Border");
        }

        _colorName.Text = CharacterColors.At(mine)?.Name ?? string.Empty;
        ApplyInspectedColor(mine);
    }

    /// <summary>Shows the mark color on the big character in the way the color mode says.</summary>
    private void ApplyInspectedColor(int? mine)
    {
        var color = CharacterColors.At(mine) is { } c ? UiKit.ToColor(c) : (Color?)null;

        if (color is { } glyph && _settings.ColorMode == ColorMode.Glyph) _bigGlyph.Foreground = new SolidColorBrush(glyph);
        else _bigGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");

        if (color is { } back && _settings.ColorMode == ColorMode.Background) _bigGlyphBox.Background = UiKit.TintBrush(back);
        else _bigGlyphBox.SetResourceReference(Border.BackgroundProperty, "Theme.Surface");

        if (color is { } bar && _settings.ColorMode == ColorMode.Accent) _bigGlyphBox.BorderBrush = new SolidColorBrush(bar);
        else _bigGlyphBox.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        _bigGlyphBox.BorderThickness = new Thickness(color is not null && _settings.ColorMode == ColorMode.Accent ? 3 : 1);
    }
}
