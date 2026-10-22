using System.Windows.Controls;
using System.Windows.Input;
using Silo.Unicode;

namespace Silo.Tools.UnicodeExplorer;

/// <summary>
///     The Generate tab: type some text and see it written in every <see cref="TextStyles" /> style (<c>hello</c> as <c>🅗🅔🅛🅛🅞</c>, bold, fullwidth, upside
///     down...); copy a result, add its characters to the collection, or look them up in the list.
/// </summary>
public sealed partial class UnicodeExplorerView
{
    private readonly TextBox _genInput = new() { Text = "Hello World", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "The text to write in the styles below" };
    private readonly StackPanel _genList = new() { Margin = new Thickness(2) };
    private readonly List<(TextStyle Style, TextBlock Output)> _genRows = [];

    private UIElement BuildGenerate()
    {
        var root = new DockPanel();

        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(UiKit.Btn("From collection", UseCollectionAsInput, "Use the characters of the collection (or the selected ones) as the text"));
        buttons.Children.Add(UiKit.Btn("Paste", PasteIntoGenerate, "Use the text on the clipboard"));
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        var label = UiKit.Text("Text", muted: true);
        label.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(label, Dock.Left);
        bar.Children.Add(label);
        bar.Children.Add(_genInput);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var frame = new Border { BorderThickness = new Thickness(1) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Theme.Border");
        frame.SetResourceReference(Border.BackgroundProperty, "Theme.BackgroundAlt");
        frame.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _genList };
        root.Children.Add(frame);

        string? group = null;
        foreach (var style in TextStyles.All)
        {
            if (style.Group != group)
            {
                group = style.Group;
                var heading = UiKit.Text(group, size: 12, weight: FontWeights.SemiBold);
                heading.Margin = new Thickness(2, 8, 0, 2);
                _genList.Children.Add(heading);
            }

            _genList.Children.Add(GenerateRow(style));
        }

        _genInput.TextChanged += (_, _) => RefreshGenerate();
        RefreshGenerate();
        return root;
    }

    private UIElement GenerateRow(TextStyle style)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = UiKit.Text(style.Name, muted: true);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip      = style.Name;
        name.Margin       = new Thickness(4, 0, 6, 0);

        var output = new TextBlock { FontFamily = UiKit.GlyphFont, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand }
            .Themed(TextBlock.ForegroundProperty, "Theme.Text");
        output.ToolTip = "Click to copy";
        output.MouseLeftButtonUp += (_, _) => CopyGenerated(style, output.Text);
        Grid.SetColumn(output, 1);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        actions.Children.Add(UiKit.Btn("Copy", () => CopyGenerated(style, output.Text), "Copy the text"));
        actions.Children.Add(UiKit.Btn("Add", () => AddTextToTray(output.Text, style.Name), "Add every character of it to the collection"));
        actions.Children.Add(UiKit.Btn("Look up", () => LookUp(output.Text), "List its characters in the browser above"));
        Grid.SetColumn(actions, 2);

        grid.Children.Add(name);
        grid.Children.Add(output);
        grid.Children.Add(actions);
        _genRows.Add((style, output));
        return grid;
    }

    private void RefreshGenerate()
    {
        string text = _genInput.Text;
        foreach (var (style, output) in _genRows)
        {
            try
            {
                output.Text = style.Apply(text);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                output.Text = "(" + ex.Message + ")";
            }
        }
    }

    private void CopyGenerated(TextStyle style, string text)
    {
        if (text.Length == 0)
        {
            Say("Type some text first.");
            return;
        }

        Copy(text, style.Name);
    }

    private void UseCollectionAsInput()
    {
        var items = TrayTarget();
        if (items.Count == 0)
        {
            Say("The collection is empty.");
            return;
        }

        _genInput.Text = string.Concat(items.Select(c => c.CodePoint.Text));
    }

    private void PasteIntoGenerate()
    {
        try
        {
            if (Clipboard.ContainsText()) _genInput.Text = Clipboard.GetText().Replace("\r", string.Empty).Replace("\n", " ");
            else Say("There is no text on the clipboard.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Say("The clipboard is busy.");
        }
    }

    /// <summary>Adds every character of a text to the collection, in order, repeats included (as pasting does).</summary>
    private void AddTextToTray(string text, string from)
    {
        if (text.Length == 0)
        {
            Say("Type some text first.");
            return;
        }

        var items = CodePoint.Enumerate(text).Select(c => new CollectedCharacter(c)).ToList();
        if (!HasTrayRoom(items.Count)) return;

        _tray.AddRange(items);
        RefreshTray();
        SaveTray();
        Say($"Added {items.Count:N0} character{(items.Count == 1 ? "" : "s")} ({from}).");
    }
}
