using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Silo.Labs.Web;

namespace Silo.Labs.Parser;

/// <summary>Small helpers for the code-built views of the Parser Lab (themed bits, drag and drop, a one-line prompt).</summary>
internal static class ParserUi
{
    public static readonly Brush Green = Frozen(0x3F, 0xA9, 0x5B);
    public static readonly Brush Red   = Frozen(0xE5, 0x48, 0x4D);
    public static readonly Brush Gray  = Frozen(0x8A, 0x8F, 0x98);
    public static readonly Brush Blue  = Frozen(0x3B, 0x82, 0xD6);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static T Themed<T>(this T control, string? background = "Theme.Background", string? foreground = "Theme.Text") where T : Control
    {
        if (background is not null) control.SetResourceReference(Control.BackgroundProperty, background);
        if (foreground is not null) control.SetResourceReference(Control.ForegroundProperty, foreground);
        return control;
    }

    public static TextBlock Label(string text, bool muted = false, bool bold = false, bool mono = false)
    {
        var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        if (muted) block.SetResourceReference(TextBlock.ForegroundProperty, "Theme.TextMuted");
        else block.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text");
        if (bold) block.FontWeight = FontWeights.SemiBold;
        if (mono) block.FontFamily = WebUi.Mono;
        return block;
    }

    /// <summary>A small rounded box around <paramref name="content" />.</summary>
    public static Border Box(UIElement content, bool selected = false, string background = "Theme.Surface", Thickness? padding = null)
    {
        var border = new Border
                     {
                         Child           = content,
                         BorderThickness = new Thickness(selected ? 2 : 1),
                         CornerRadius    = new CornerRadius(4),
                         Padding         = padding ?? new Thickness(4, 2, 4, 2),
                         Margin          = new Thickness(2)
                     };
        border.SetResourceReference(Border.BorderBrushProperty, selected ? "Theme.Accent" : "Theme.Border");
        border.SetResourceReference(Border.BackgroundProperty, background);
        return border;
    }

    public static Button Small(string text, Action click, string? tip = null)
    {
        var button = new Button { Content = text, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(2, 0, 2, 0), ToolTip = tip, MinWidth = 22 };
        button.Click += (_, _) => click();
        return button;
    }

    public static ComboBox Combo(IEnumerable<string> items, string? selected, Action<string> changed, bool editable = false, double width = 140)
    {
        var combo = new ComboBox { IsEditable = editable, MinWidth = width, Margin = new Thickness(2, 0, 2, 0) };
        foreach (string item in items) combo.Items.Add(item);
        if (selected is not null && !combo.Items.Contains(selected) && editable) combo.Items.Add(selected);
        combo.SelectedItem = selected;
        if (editable) combo.Text = selected ?? string.Empty;

        if (editable)
        {
            combo.LostKeyboardFocus += (_, _) => changed(combo.Text);
            combo.KeyDown += (_, e) =>
                             {
                                 if (e.Key == Key.Enter) changed(combo.Text);
                             };
            combo.SelectionChanged += (_, _) =>
                                      {
                                          if (combo.SelectedItem is string s) changed(s);
                                      };
        }
        else
        {
            combo.SelectionChanged += (_, _) =>
                                      {
                                          if (combo.SelectedItem is string s && !string.Equals(s, selected, StringComparison.Ordinal)) changed(s);
                                      };
        }

        return combo;
    }

    /// <summary>A text box that reports its text when it is left or Enter is pressed (and only if it changed).</summary>
    public static TextBox Field(string? text, Action<string> commit, double width = 140, string? tip = null)
    {
        var box = new TextBox { Text = text ?? string.Empty, MinWidth = width, Margin = new Thickness(2, 0, 2, 0), ToolTip = tip, VerticalContentAlignment = VerticalAlignment.Center };
        string before = box.Text;
        void Commit()
        {
            if (box.Text == before) return;

            before = box.Text;
            commit(box.Text);
        }

        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
                       {
                           if (e.Key == Key.Enter) Commit();
                       };
        return box;
    }

    // ---- drag and drop ---------------------------------------------------------------------------

    /// <summary>Lets <paramref name="source" /> be dragged, carrying what <paramref name="item" /> says.</summary>
    public static void EnableDrag(UIElement source, Func<DragItem?> item)
    {
        Point? start = null;
        source.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(null);
        source.PreviewMouseMove += (_, e) =>
                                   {
                                       if (e.LeftButton != MouseButtonState.Pressed || start is not { } from) return;
                                       if (!Moved(from, e.GetPosition(null))) return;

                                       start = null;
                                       if (item() is { } it) Start(source, it);
                                   };
    }

    /// <summary>Lets the entries of a list be dragged; <paramref name="map" /> says what an entry carries (null: nothing).</summary>
    public static void EnableListDrag(ListBox list, Func<object, DragItem?> map)
    {
        Point?  start = null;
        object? under = null;
        list.PreviewMouseLeftButtonDown += (_, e) =>
                                           {
                                               start = e.GetPosition(null);
                                               under = (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext;
                                           };
        list.PreviewMouseMove += (_, e) =>
                                 {
                                     if (e.LeftButton != MouseButtonState.Pressed || start is not { } from || under is null) return;
                                     if (!Moved(from, e.GetPosition(null))) return;

                                     start = null;
                                     if (map(under) is { } it) Start(list, it);
                                 };
    }

    private static bool Moved(Point from, Point to)
    {
        return Math.Abs(to.X - from.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(to.Y - from.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    private static void Start(DependencyObject source, DragItem item)
    {
        var data = new DataObject();
        data.SetData(DragItem.Format, item.Encode());
        DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    /// <summary>The thing being dragged over or dropped on a Parser Lab drop target; null when it is something else.</summary>
    public static DragItem? ItemOf(DragEventArgs e)
    {
        return e.Data.GetDataPresent(DragItem.Format) ? DragItem.Decode(e.Data.GetData(DragItem.Format) as string) : null;
    }

    /// <summary>
    ///     Lets a piece of the canvas be dragged. Uses the bubbling events, so of boxes inside boxes the innermost one under the mouse is the one
    ///     dragged (the tunneling ones would start the outermost).
    /// </summary>
    public static void EnableNodeDrag(UIElement source, Func<DragItem?> item)
    {
        Point? start = null;
        // handledEventsToo: the box also selects itself on this event and marks it handled
        source.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) => start = e.GetPosition(null)), true);
        source.MouseLeftButtonUp += (_, _) => start = null;
        source.MouseMove += (_, e) =>
                            {
                                if (e.LeftButton != MouseButtonState.Pressed || start is not { } from) return;
                                if (!Moved(from, e.GetPosition(null))) return;

                                start     = null;
                                e.Handled = true;
                                if (item() is { } it) Start(source, it);
                            };
    }

    /// <summary>Makes <paramref name="target" /> take what is dropped on it.</summary>
    public static void EnableDrop(UIElement target, Action<DragItem> drop)
    {
        EnableDrop(target, (item, _) => drop(item));
    }

    /// <summary>
    ///     Makes <paramref name="target" /> take what is dropped on it. The second argument says whether Ctrl was down: a piece of the canvas that is
    ///     dropped is moved, or copied with Ctrl.
    /// </summary>
    public static void EnableDrop(UIElement target, Action<DragItem, bool> drop)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) =>
                           {
                               var item = ItemOf(e);
                               e.Effects = item is null                  ? DragDropEffects.None :
                                           item.Kind == "node" && !Copying(e) ? DragDropEffects.Move : DragDropEffects.Copy;
                               e.Handled = true;
                           };
        target.Drop += (_, e) =>
                       {
                           if (ItemOf(e) is { } item)
                           {
                               e.Handled = true;
                               drop(item, Copying(e));
                           }
                       };
    }

    private static bool Copying(DragEventArgs e) => (e.KeyStates & DragDropKeyStates.ControlKey) != 0;

    // ---- a one-line question ---------------------------------------------------------------------

    /// <summary>Asks for a line of text in a small window; null when the person cancelled (or left it empty).</summary>
    public static string? Ask(string title, string prompt, string initial = "")
    {
        return Silo.Ui.Windows.PromptDialog.Ask(title, prompt, "OK", initial);
    }

    /// <summary>Asks a yes/no question; true for yes.</summary>
    public static bool Confirm(string text, string title = "Parser Lab")
    {
        return Silo.Ui.Windows.PromptDialog.Show(title, text, ["Yes", "No"]).Button == 0;
    }

    /// <summary>The key of the icon for a node kind (the icons are in Resources/NavIcons.xaml).</summary>
    public static string NodeIcon(string kind)
    {
        return kind switch
               {
                   "token"     => "Token",
                   "literal"   => "Literal",
                   "chars"     => "CharSet",
                   "unicode"   => "Unicode",
                   "ref"       => "Rule",
                   "eof"       => "Token",
                   "seq"       => "Sequence",
                   "choice"    => "Choice",
                   "repeat"    => "Quantifier",
                   "not"       => "Not",
                   "try"       => "Wrap",
                   "between"   => "Wrap",
                   "delimited" => "Quantifier",
                   "convert"   => "Wrap",
                   "where"     => "Wrap",
                   "named"     => "Property",
                   _           => "Wrap"
               };
    }

    /// <summary>The key of the icon for a combinator of the catalog.</summary>
    public static string CombinatorIcon(string id)
    {
        return id switch
               {
                   "many" or "once" or "repeat" or "optional" or "one" or "many-delimited" or "once-delimited" => "Quantifier",
                   "not"                                                                                      => "Not",
                   "or"                                                                                       => "Choice",
                   "then" or "ignore-then"                                                                    => "Sequence",
                   "match" or "match-many"                                                                    => "CharSet",
                   "named"                                                                                    => "Property",
                   _                                                                                          => "Wrap"
               };
    }
}
