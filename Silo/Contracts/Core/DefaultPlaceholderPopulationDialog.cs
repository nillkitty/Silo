using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Silo.DbModel;
using Silo.Ui.Controls;
using Telefrag.DI;

namespace Silo.Contracts.Core;

/// <summary>
///     The stock <see cref="IPlaceholderPopulationDialog" />: a wizard with a step rail on the left (one step per
///     placeholder, each showing what has been chosen so far), and on the right the components that can fill the current
///     placeholder as a grid of cards, with the chosen component's settings underneath. A placeholder for a list
///     (<c>Transform List</c>) adds a component per click and shows the ordered list, which can be rearranged, next to the
///     settings of the selected entry.
/// </summary>
/// <remarks>
///     The window is built per call, like <see cref="DefaultNewSiloDialog" />, so each use starts clean. Components are
///     created with <see cref="ComponentFactory" />; settings are drawn by <see cref="ModelControl{TModel}" />, the same
///     generated property editor the rest of Silo uses. A value the template already supplied
///     (<see cref="Placeholder.PlaceholderContent" />, if it is a component of the right type) starts out chosen.
/// </remarks>
[Singleton(typeof(IPlaceholderPopulationDialog))]
public class DefaultPlaceholderPopulationDialog : IPlaceholderPopulationDialog
{
    /// <summary>
    ///     Builds a component from its type. Replace it to build components some other way; the default calls the public
    ///     constructor with the fewest parameters, filling them from the app's DI container.
    /// </summary>
    public Func<Type, object?>? ComponentFactory { get; set; }

    private readonly List<PlaceholderRequest>                 _requests = [];
    private readonly Dictionary<Placeholder, object>           _values   = new();

    /// <summary>What the person chose, keyed by placeholder; filled by <see cref="ShowDialog" /> when they press Finish. Placeholders left empty (optional ones, or ones that name no type) are absent.</summary>
    public IDictionary<Placeholder, object> SelectedValues => _values;

    /// <summary>Queues a placeholder for the next <see cref="ShowDialog" />. The first one added after a showing starts a new batch.</summary>
    public void AddPlaceholder(Placeholder p, SiloTemplate source, bool isRequired)
    {
        ArgumentNullException.ThrowIfNull(p);

        if (_requests.Count == 0) _values.Clear();
        _requests.Add(new PlaceholderRequest(p, source, isRequired));
    }

    /// <summary>
    ///     Shows the wizard for the placeholders added since the last showing. Returns true if the person finished, false if
    ///     they cancelled; with nothing added it returns true without showing anything. The queue is emptied either way, so
    ///     the same instance can be used again (it is registered as a singleton).
    /// </summary>
    public bool? ShowDialog()
    {
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess()) return dispatcher.Invoke(() => ShowDialog());

        var requests = _requests.ToList();
        _requests.Clear();
        _values.Clear();

        var slots = PlaceholderComponents.Order(requests);
        if (slots.Count == 0) return true;

        var names = slots.Select(x => x.Source?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct().ToList();
        var title = names.Count == 0 ? null : string.Join(" + ", names);

        var wizard = new Wizard(slots, title, App.Require<IReflectionProvider>(), ComponentFactory ?? DefaultFactory);
        if (!wizard.Run(_values)) return false;

        return true;
    }

    /// <summary>The default <see cref="ComponentFactory" />.</summary>
    public static object? DefaultFactory(Type type)
    {
        var container = App.Instance?.Components;

        foreach (var ctor in type.GetConstructors().OrderBy(c => c.GetParameters().Length))
        {
            var parameters = ctor.GetParameters();
            var args       = new object?[parameters.Length];
            var usable     = true;

            for (var i = 0; i < parameters.Length; i++)
            {
                args[i] = ResolveService(container, parameters[i].ParameterType);
                if (args[i] is not null) continue;

                if (parameters[i].HasDefaultValue) args[i] = parameters[i].DefaultValue;
                else
                {
                    usable = false;
                    break;
                }
            }

            if (usable) return ctor.Invoke(args);
        }

        return null;
    }

    private static object? ResolveService(Container? container, Type service)
    {
        if (container is null || !(service.IsInterface || service.IsClass)) return null;

        try
        {
            var resolve = typeof(Container).GetMethods()
                                           .FirstOrDefault(m => m.Name == "Resolve" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            return resolve?.MakeGenericMethod(service).Invoke(container, null);
        }
        catch
        {
            return null; // not registered, or not resolvable as a service: the constructor is just not usable
        }
    }

    // =====================================================================================================
    //  the wizard
    // =====================================================================================================

    private sealed class Entry(ComponentInfo info, object instance)
    {
        public ComponentInfo Info     { get; } = info;
        public object        Instance { get; } = instance;
    }

    private sealed class Step(PlaceholderSlot slot)
    {
        public PlaceholderSlot       Slot       { get; } = slot;
        public List<ComponentInfo>   Components { get; set; } = [];
        public Exception?            LoadError  { get; set; }
        public Entry?                Chosen     { get; set; }
        public ObservableCollection<Entry> Items { get; } = [];
        public string                Filter     { get; set; } = "";
        public bool                  Visited    { get; set; }

        /// <summary>
        ///     A required placeholder needs a component (a list, at least one); an optional one can be left empty; one that
        ///     names no type cannot be picked here and is left as it is.
        /// </summary>
        public bool IsComplete => !Slot.IsPickable || !Slot.IsRequired || (Slot.IsList ? Items.Count > 0 : Chosen is not null);

        public string Summary
        {
            get
            {
                if (!Slot.IsPickable) return "Nothing to choose";
                if (!Slot.IsList) return Chosen?.Info.Name ?? (Slot.IsRequired ? "Not chosen yet" : "Optional - skipped");
                return Items.Count switch
                       {
                           0 => Slot.IsRequired ? "Add at least one" : "Optional - nothing added",
                           1 => Items[0].Info.Name,
                           _ => Items.Count + " items: " + string.Join(", ", Items.Select(i => i.Info.Name))
                       };
            }
        }
    }

    private sealed class Wizard
    {
        // palette
        private static readonly Color NavyColor   = Rgb(0x0A0F2E);
        private static readonly Color NavyMid     = Rgb(0x1E2A78);
        private static readonly Brush Accent      = Solid(0x5B6CFF);
        private static readonly Brush AccentDeep  = Solid(0x2F3FD0);
        private static readonly Brush AccentSoft  = Solid(0xE9ECFF);
        private static readonly Brush Surface     = Solid(0xF4F5FB);
        private static readonly Brush CardBorder  = Solid(0xDDE0F0);
        private static readonly Brush TextBrush   = Solid(0x1B1F3B);
        private static readonly Brush MutedBrush  = Solid(0x6A6F90);
        private static readonly Brush GoodBrush   = Solid(0x2EB872);
        private static readonly Brush BadBrush    = Solid(0xC0392B);
        private static readonly Brush WhiteBrush  = Solid(0xFFFFFF);
        private static readonly FontFamily Glyphs = new("Segoe MDL2 Assets");

        private static readonly (uint From, uint To)[] BadgeColors =
        [
            (0x5B6CFF, 0x8E54E9), (0x00B09B, 0x2E7D9A), (0xF2994A, 0xEB5757), (0x56CCF2, 0x2D9CDB), (0x9B51E0, 0xE056A8),
            (0x27AE60, 0x7ED957)
        ];

        private static readonly Lazy<ControlTemplate> ButtonTemplate = new(() => (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "<Border x:Name='Root' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' " +
            "BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='8' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>" +
            "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' RecognizesAccessKey='True'/></Border>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Root' Property='Opacity' Value='0.88'/></Trigger>" +
            "<Trigger Property='IsPressed' Value='True'><Setter TargetName='Root' Property='Opacity' Value='0.72'/></Trigger>" +
            "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='Root' Property='Opacity' Value='0.38'/></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>"));

        private readonly Button      _back;
        private readonly Button      _cancel;
        private readonly Button      _next;
        private readonly ProgressBar _progress = new() { Height = 4, BorderThickness = new Thickness(0), Foreground = Accent, Background = AccentSoft };
        private readonly StackPanel  _rail     = new() { Margin = new Thickness(10, 4, 10, 10) };
        private readonly Border      _host     = new() { ClipToBounds = true };
        private readonly TextBlock   _stepOf   = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Accent };
        private readonly TextBlock   _heading  = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = TextBrush, Margin = new Thickness(0, 2, 0, 0) };
        private readonly List<Step>  _steps;
        private readonly Window      _window;
        private readonly Func<Type, object?> _factory;

        private int  _index = -1;
        private bool _finished;

        // the current page
        private WrapPanel? _cards;
        private Border?    _config;
        private ListBox?   _list;
        private TextBlock? _notice;
        private Entry?     _selected;

        public Wizard(List<PlaceholderSlot> slots, string? title, IReflectionProvider reflection, Func<Type, object?> factory)
        {
            _factory = factory;
            _steps   = slots.Select(slot => Load(slot, reflection)).ToList();

            _back   = MakeButton("Back", false);
            _next   = MakeButton("Next  ›", true);
            _cancel = MakeButton("Cancel", false);
            _cancel.IsCancel = true;

            _back.Click   += (_, _) => Go(_index - 1);
            _next.Click   += (_, _) => Advance();
            _cancel.Click += (_, _) => _window!.DialogResult = false;

            _window = new Window
                      {
                          Title                 = string.IsNullOrWhiteSpace(title) ? "Fill in the blanks" : "Set up " + title.Trim(),
                          Width                 = 980,
                          Height                = 660,
                          MinWidth              = 820,
                          MinHeight             = 560,
                          ShowInTaskbar         = false,
                          ResizeMode            = ResizeMode.CanResizeWithGrip,
                          WindowStartupLocation = WindowStartupLocation.CenterOwner,
                          Background            = Surface,
                          Owner                 = Application.Current?.MainWindow is { IsVisible: true } w ? w : null
                      };
            if (_window.Owner is null) _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _window.Content = BuildFrame(title);
            _window.Loaded += (_, _) => Go(0);
        }

        /// <summary>Shows the wizard. If the person finishes, their choices are added to <paramref name="values" /> and true is returned.</summary>
        public bool Run(Dictionary<Placeholder, object> values)
        {
            _window.ShowDialog();
            if (!_finished) return false;

            foreach (var step in _steps)
            {
                if (!step.Slot.IsPickable) continue;

                object? value = step.Slot.IsList
                                    ? step.Items.Count > 0 ? PlaceholderComponents.CreateList(step.Slot.Type!, step.Items.Select(i => i.Instance).ToList()) : null
                                    : step.Chosen?.Instance;
                if (value is not null) values[step.Slot.Placeholder] = value;
            }

            return true;
        }

        // ---- state ---------------------------------------------------------------------------------------

        private Step Load(PlaceholderSlot slot, IReflectionProvider reflection)
        {
            var step = new Step(slot);
            if (!slot.IsPickable) return step;

            try
            {
                step.Components = PlaceholderComponents.Find(slot.ElementType!, reflection);
            }
            catch (Exception ex)
            {
                step.LoadError = ex;
            }

            // what the template already supplied starts out chosen
            var given = slot.Placeholder.PlaceholderContent;
            if (slot.IsList)
            {
                if (given is System.Collections.IEnumerable many and not string)
                    foreach (var item in many)
                        if (item is not null && slot.ElementType!.IsInstanceOfType(item))
                            step.Items.Add(new Entry(InfoFor(step, item.GetType()), item));
            }
            else if (given is not null && slot.ElementType!.IsInstanceOfType(given))
            {
                step.Chosen = new Entry(InfoFor(step, given.GetType()), given);
            }

            return step;
        }

        private static ComponentInfo InfoFor(Step step, Type type)
        {
            return step.Components.FirstOrDefault(c => c.Type == type) ?? PlaceholderComponents.Describe(type) ??
                   new ComponentInfo(type, PlaceholderComponents.Humanize(type.Name), null, null, null);
        }

        private Step Current => _steps[_index];

        private void Go(int index)
        {
            if (index < 0 || index >= _steps.Count) return;
            if (index > _index && !_steps.Take(index).All(s => s.IsComplete)) return;

            var direction = index >= _index ? 1 : -1;
            _index = index;
            Current.Visited = true;
            _selected = Current.Items.FirstOrDefault();

            _stepOf.Text = $"STEP {_index + 1} OF {_steps.Count}" + (Current.Slot.Source?.Name is { Length: > 0 } template ? "  \u00B7  " + template.Trim().ToUpperInvariant() : "");
            _heading.Text = Current.Slot.Title;
            AnimateProgress(_index + 1);

            var page = BuildPage(Current);
            _host.Child = page;
            Slide(page, direction);

            RefreshRail();
            UpdateButtons();
        }

        private void Advance()
        {
            if (!Current.IsComplete) return;

            if (_index < _steps.Count - 1)
            {
                Go(_index + 1);
                return;
            }

            _finished            = true;
            _window.DialogResult = true;
        }

        private void UpdateButtons()
        {
            var last = _index == _steps.Count - 1;
            _next.Content   = last ? "Finish" : "Next  ›";
            _next.IsEnabled = Current.IsComplete;
            _back.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Changed()
        {
            RefreshRail();
            UpdateButtons();
        }

        // ---- frame: rail | header, page, footer ----------------------------------------------------------

        private UIElement BuildFrame(string? title)
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(262) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // left: the rail
            var railBox = new Grid { Background = new LinearGradientBrush(NavyColor, NavyMid, 90) };
            railBox.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            railBox.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var heading = new StackPanel { Margin = new Thickness(22, 24, 18, 18) };
            heading.Children.Add(new TextBlock { Text = "", FontFamily = Glyphs, FontSize = 22, Foreground = Accent, Margin = new Thickness(0, 0, 0, 8) });
            heading.Children.Add(new TextBlock { Text = "Fill in the blanks", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = WhiteBrush });
            heading.Children.Add(new TextBlock
                                 {
                                     Text         = string.IsNullOrWhiteSpace(title) ? "This template needs a few choices." : title!.Trim(),
                                     FontSize     = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                                     TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
                                 });
            Grid.SetRow(heading, 0);
            railBox.Children.Add(heading);

            var railScroll = new ScrollViewer { Content = _rail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            Grid.SetRow(railScroll, 1);
            railBox.Children.Add(railScroll);
            Grid.SetColumn(railBox, 0);
            root.Children.Add(railBox);

            // right: header / page / footer
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new Border { Background = WhiteBrush, BorderBrush = CardBorder, BorderThickness = new Thickness(0, 0, 0, 1) };
            var headerStack = new StackPanel();
            var texts       = new StackPanel { Margin = new Thickness(28, 18, 28, 14) };
            texts.Children.Add(_stepOf);
            texts.Children.Add(_heading);
            headerStack.Children.Add(texts);
            headerStack.Children.Add(_progress);
            header.Child = headerStack;
            Grid.SetRow(header, 0);
            right.Children.Add(header);

            Grid.SetRow(_host, 1);
            right.Children.Add(_host);

            var footer = new Border
                         {
                             Background = WhiteBrush, BorderBrush = CardBorder, BorderThickness = new Thickness(0, 1, 0, 0),
                             Padding    = new Thickness(28, 14, 28, 14)
                         };
            var buttons = new Grid();
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _back.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(_cancel, 0);
            Grid.SetColumn(_back,   2);
            Grid.SetColumn(_next,   3);
            buttons.Children.Add(_cancel);
            buttons.Children.Add(_back);
            buttons.Children.Add(_next);
            footer.Child = buttons;
            Grid.SetRow(footer, 2);
            right.Children.Add(footer);

            Grid.SetColumn(right, 1);
            root.Children.Add(right);
            return root;
        }

        // ---- the rail ------------------------------------------------------------------------------------

        private void RefreshRail()
        {
            _rail.Children.Clear();
            for (var i = 0; i < _steps.Count; i++) _rail.Children.Add(RailItem(i));
        }

        private UIElement RailItem(int i)
        {
            var step    = _steps[i];
            var current = i == _index;
            var done    = !current && step.Visited && step.IsComplete;

            var dot = new Border
                      {
                          Width = 30, Height = 30, CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Top,
                          Background = current ? Accent : done ? GoodBrush : Brushes.Transparent,
                          BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
                          BorderThickness = current || done ? new Thickness(0) : new Thickness(1.5)
                      };
            dot.Child = done
                            ? new TextBlock { Text = "", FontFamily = Glyphs, FontSize = 13, Foreground = WhiteBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                            : new TextBlock { Text = (i + 1).ToString(), FontWeight = FontWeights.SemiBold, Foreground = WhiteBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = step.Slot.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = WhiteBrush, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
                              {
                                  Text = step.Summary, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0),
                                  Foreground = new SolidColorBrush(done ? Color.FromRgb(0x9F, 0xE8, 0xC2) : Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF))
                              });

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(text, 1);
            row.Children.Add(dot);
            row.Children.Add(text);

            var normal = current ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Colors.Transparent;
            var item = new Border
                       {
                           Child = row, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 10, 10, 10), Margin = new Thickness(0, 2, 0, 2),
                           Background = new SolidColorBrush(normal), Cursor = Cursors.Hand
                       };
            var enabled = i <= _index || _steps.Take(i).All(s => s.IsComplete);
            item.Opacity = enabled ? 1 : 0.55;
            item.MouseEnter += (_, _) =>
                               {
                                   if (!current) item.Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
                               };
            item.MouseLeave += (_, _) => item.Background = new SolidColorBrush(normal);
            item.MouseLeftButtonUp += (_, _) => Go(i);
            return item;
        }

        // ---- a step's page -------------------------------------------------------------------------------

        private UIElement BuildPage(Step step)
        {
            var slot = step.Slot;

            _cards  = null;
            _config = null;
            _list   = null;

            var grid = new Grid { Margin = new Thickness(28, 18, 28, 12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 120 });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 150 });

            // what is being asked for
            var top = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            if (slot.Description is { } description)
                top.Children.Add(new TextBlock { Text = description, Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

            var needed = new StackPanel { Orientation = Orientation.Horizontal };
            needed.Children.Add(new TextBlock { Text = "Type needed:", FontStyle = FontStyles.Italic, Foreground = MutedBrush, VerticalAlignment = VerticalAlignment.Center });
            needed.Children.Add(new Border
                                {
                                    Background = AccentSoft, CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 2, 10, 3), Margin = new Thickness(10, 0, 0, 0),
                                    Child = new TextBlock { Text = slot.TypeLabel, FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = AccentDeep }
                                });
            needed.Children.Add(new Border
                                {
                                    CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 2, 10, 3), Margin = new Thickness(8, 0, 0, 0),
                                    Background   = slot.IsRequired ? Solid(0xFDECEA) : Surface, BorderBrush = slot.IsRequired ? Solid(0xF3B8B0) : CardBorder,
                                    BorderThickness = new Thickness(1),
                                    Child = new TextBlock { Text = slot.IsRequired ? "Required" : "Optional", FontSize = 12, Foreground = slot.IsRequired ? BadBrush : MutedBrush }
                                });
            top.Children.Add(needed);

            _notice = new TextBlock { Foreground = BadBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            top.Children.Add(_notice);
            Grid.SetRow(top, 0);
            grid.Children.Add(top);

            // available components
            var available = BuildAvailable(step);
            Grid.SetRow(available, 1);
            grid.Children.Add(available);

            if (!slot.IsPickable) return grid;

            var splitter = new GridSplitter
                           {
                               Height = 8, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
                               Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ResizeDirection = GridResizeDirection.Rows
                           };
            Grid.SetRow(splitter, 2);
            grid.Children.Add(splitter);

            var selection = slot.IsList ? BuildListArea(step) : BuildSingleArea(step);
            Grid.SetRow(selection, 3);
            grid.Children.Add(selection);
            return grid;
        }

        private UIElement BuildAvailable(Step step)
        {
            var slot = step.Slot;
            var box  = Elevated(new Grid());
            var body = (Grid)((Border)box).Child;
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var head = new Grid { Margin = new Thickness(16, 12, 16, 0) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock
                              {
                                  Text = slot.IsList ? "Available components  —  click to add" : "Available components:", FontWeight = FontWeights.SemiBold,
                                  Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center
                              });
            if (step.Components.Count > 8)
            {
                var filter = new TextBox { Width = 190, Padding = new Thickness(6, 3, 6, 3), Text = step.Filter, ToolTip = "Filter the components by name" };
                filter.TextChanged += (_, _) =>
                                      {
                                          step.Filter = filter.Text;
                                          RebuildCards(step);
                                      };
                Grid.SetColumn(filter, 1);
                head.Children.Add(filter);
            }

            Grid.SetRow(head, 0);
            body.Children.Add(head);

            if (!slot.IsPickable)
            {
                body.Children.Add(Message(1, "This placeholder does not say what type it stands for, so there is nothing to choose here. It is left empty."));
                return box;
            }

            if (step.LoadError is { } error)
            {
                body.Children.Add(Message(1, "The components could not be listed: " + error.Message, true));
                return box;
            }

            if (step.Components.Count == 0)
            {
                body.Children.Add(Message(1, $"No components of this type are available yet. Anything that implements {PlaceholderComponents.TypeLabel(slot.ElementType!)} and can be built by Silo will show up here."));
                return box;
            }

            _cards = new WrapPanel { Margin = new Thickness(8, 6, 8, 8) };
            var scroll = new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            Grid.SetRow(scroll, 1);
            body.Children.Add(scroll);
            RebuildCards(step);
            return box;
        }

        private void RebuildCards(Step step)
        {
            if (_cards is null) return;

            _cards.Children.Clear();
            var filter  = step.Filter.Trim();
            var visible = step.Components.Where(c => filter.Length == 0 || c.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                                                     (c.Description?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ?? false)).ToList();
            foreach (var info in visible) _cards.Children.Add(Card(step, info));

            if (visible.Count == 0)
                _cards.Children.Add(new TextBlock { Text = "Nothing matches that filter.", Foreground = MutedBrush, Margin = new Thickness(10) });
        }

        private UIElement Card(Step step, ComponentInfo info)
        {
            var selected = !step.Slot.IsList && step.Chosen?.Info.Type == info.Type;

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(IconFor(info, 44));
            stack.Children.Add(new TextBlock
                               {
                                   Text = info.Name, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, FontSize = 13,
                                   FontWeight = FontWeights.SemiBold, Foreground = TextBrush, Margin = new Thickness(8, 9, 8, 0)
                               });

            var inner = new Grid();
            inner.Children.Add(stack);
            if (selected)
                inner.Children.Add(new Border
                                   {
                                       Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Accent, HorizontalAlignment = HorizontalAlignment.Right,
                                       VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 7, 0),
                                       Child = new TextBlock { Text = "", FontFamily = Glyphs, FontSize = 11, Foreground = WhiteBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                                   });

            var scale = new ScaleTransform(1, 1);
            var card = new Border
                       {
                           Width = 152, Height = 126, Margin = new Thickness(7), CornerRadius = new CornerRadius(12), Background = WhiteBrush,
                           BorderBrush = selected ? Accent : CardBorder, BorderThickness = new Thickness(selected ? 2 : 1), Cursor = Cursors.Hand,
                           Child = inner, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale,
                           Effect = new DropShadowEffect
                                    {
                                        BlurRadius = selected ? 18 : 10, ShadowDepth = 2, Direction = 270, Opacity = selected ? 0.4 : 0.14,
                                        Color      = selected ? Rgb(0x5B6CFF) : Colors.Black
                                    },
                           ToolTip = string.IsNullOrWhiteSpace(info.Description) ? info.Type.FullName : info.Description + "\n" + info.Type.FullName
                       };
            card.MouseEnter += (_, _) => Pulse(scale, 1.05);
            card.MouseLeave += (_, _) => Pulse(scale, 1.0);
            card.MouseLeftButtonDown += (_, e) =>
                                        {
                                            if (step.Slot.IsList)
                                            {
                                                if (e.ClickCount == 1) AddToList(step, info);
                                            }
                                            else if (!step.Slot.IsRequired && step.Chosen?.Info.Type == info.Type && e.ClickCount == 1)
                                            {
                                                step.Chosen = null; // an optional choice is cleared by clicking it again
                                                RebuildCards(step);
                                                ShowConfig(null, "Settings");
                                                Changed();
                                            }
                                            else
                                            {
                                                Choose(step, info);
                                                if (e.ClickCount >= 2 && step.IsComplete) Advance(); // double click: choose and move on
                                            }
                                        };
            return card;
        }

        // ---- choosing --------------------------------------------------------------------------------------

        private Entry? Create(ComponentInfo info)
        {
            object? instance = null;
            try
            {
                instance = _factory(info.Type);
            }
            catch (Exception ex)
            {
                Notice($"'{info.Name}' could not be created: {(ex.InnerException ?? ex).Message}");
                return null;
            }

            if (instance is null)
            {
                Notice($"'{info.Name}' could not be created: no constructor could be filled in from what Silo has registered.");
                return null;
            }

            Notice(null);
            return new Entry(info, instance);
        }

        private void Notice(string? text)
        {
            if (_notice is null) return;

            _notice.Text       = text ?? "";
            _notice.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Choose(Step step, ComponentInfo info)
        {
            if (step.Chosen?.Info.Type == info.Type)
            {
                Notice(null);
                return;
            }

            if (Create(info) is not { } entry) return;

            step.Chosen = entry;
            RebuildCards(step);
            ShowConfig(entry, "Settings");
            Changed();
        }

        private void AddToList(Step step, ComponentInfo info)
        {
            if (Create(info) is not { } entry) return;

            step.Items.Add(entry);
            _selected = entry;
            RefreshList(step);
            Changed();
        }

        // ---- the settings area ----------------------------------------------------------------------------

        private UIElement BuildSingleArea(Step step)
        {
            var box = Elevated(new Grid());
            _config = new Border();
            ((Border)box).Child = _config;
            ShowConfig(step.Chosen, "Settings");
            return box;
        }

        private UIElement BuildListArea(Step step)
        {
            var box  = Elevated(new Grid());
            var grid = (Grid)((Border)box).Child;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(268) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new Grid { Margin = new Thickness(14, 12, 6, 12) };
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            left.Children.Add(new TextBlock { Text = "In order, top to bottom:", FontWeight = FontWeights.SemiBold, Foreground = TextBrush, Margin = new Thickness(0, 0, 0, 8) });

            _list = new ListBox { BorderBrush = CardBorder, Background = WhiteBrush, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            _list.SelectionChanged += (_, _) =>
                                      {
                                          if (_list.SelectedItem is ListBoxItem { Tag: Entry entry } && !ReferenceEquals(_selected, entry))
                                          {
                                              _selected = entry;
                                              ShowConfig(entry, "Settings");
                                          }
                                      };
            Grid.SetRow(_list, 1);
            left.Children.Add(_list);

            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            tools.Children.Add(ToolButton("", "Move up",   () => Move(step, -1)));
            tools.Children.Add(ToolButton("", "Move down", () => Move(step, 1)));
            tools.Children.Add(ToolButton("", "Remove",    () => Remove(step)));
            Grid.SetRow(tools, 2);
            left.Children.Add(tools);
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            _config = new Border { BorderBrush = CardBorder, BorderThickness = new Thickness(1, 0, 0, 0) };
            Grid.SetColumn(_config, 1);
            grid.Children.Add(_config);

            RefreshList(step);
            return box;
        }

        private void RefreshList(Step step)
        {
            if (_list is null) return;

            _list.Items.Clear();
            var n = 0;
            foreach (var entry in step.Items)
            {
                n++;
                var row = new Grid { Margin = new Thickness(2, 5, 2, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var number = new Border
                             {
                                 Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = AccentSoft, Margin = new Thickness(0, 0, 9, 0),
                                 Child = new TextBlock { Text = n.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = AccentDeep, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                             };
                var name = new TextBlock { Text = entry.Info.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(name, 1);
                row.Children.Add(number);
                row.Children.Add(name);
                _list.Items.Add(new ListBoxItem { Content = row, Tag = entry });
            }

            var match = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, _selected)) ?? _list.Items.OfType<ListBoxItem>().FirstOrDefault();
            _selected = (match?.Tag as Entry);
            if (match is not null) match.IsSelected = true;
            ShowConfig(_selected, "Settings");
        }

        private void Move(Step step, int by)
        {
            if (_selected is null) return;

            var from = step.Items.IndexOf(_selected);
            var to   = from + by;
            if (from < 0 || to < 0 || to >= step.Items.Count) return;

            step.Items.Move(from, to);
            RefreshList(step);
            Changed();
        }

        private void Remove(Step step)
        {
            if (_selected is null) return;

            var at = step.Items.IndexOf(_selected);
            if (at < 0) return;

            step.Items.RemoveAt(at);
            _selected = step.Items.Count == 0 ? null : step.Items[Math.Min(at, step.Items.Count - 1)];
            RefreshList(step);
            Changed();
        }

        private void ShowConfig(Entry? entry, string heading)
        {
            if (_config is null) return;

            if (entry is null)
            {
                _config.Child = new TextBlock
                                {
                                    Text = Current.Slot.IsList
                                               ? "Click a component above to add it to the list."
                                               : Current.Slot.IsRequired
                                                   ? "Choose a component above to set it up."
                                                   : "This one is optional: choose a component above to set it up, or just continue.",
                                    Foreground = MutedBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(24)
                                };
                return;
            }

            var grid = new Grid { Margin = new Thickness(18, 12, 18, 12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            title.Children.Add(IconFor(entry.Info, 26));
            var names = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = entry.Info.Name + "  —  " + heading.ToLowerInvariant(), FontWeight = FontWeights.SemiBold, Foreground = TextBrush });
            if (!string.IsNullOrWhiteSpace(entry.Info.Description))
                names.Children.Add(new TextBlock { Text = entry.Info.Description, FontSize = 12, Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis });
            title.Children.Add(names);
            grid.Children.Add(title);

            UIElement body;
            if (!PlaceholderComponents.IsConfigurable(entry.Instance.GetType()))
            {
                body = new TextBlock { Text = "There is nothing to configure for this component.", Foreground = MutedBrush, Margin = new Thickness(0, 6, 0, 0) };
            }
            else
            {
                try
                {
                    var editor = (UIElement)Activator.CreateInstance(typeof(ModelControl<>).MakeGenericType(entry.Instance.GetType()), entry.Instance)!;
                    body = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
                }
                catch (Exception ex)
                {
                    body = new TextBlock { Text = "The settings could not be shown: " + (ex.InnerException ?? ex).Message, Foreground = BadBrush, TextWrapping = TextWrapping.Wrap };
                }
            }

            Grid.SetRow(body, 1);
            grid.Children.Add(body);
            _config.Child = grid;
        }

        // ---- small pieces ------------------------------------------------------------------------------------

        private static UIElement Message(int row, string text, bool error = false)
        {
            var message = new TextBlock
                          {
                              Text = text, Foreground = error ? BadBrush : MutedBrush, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                              HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460, Margin = new Thickness(20)
                          };
            Grid.SetRow(message, row);
            return message;
        }

        /// <summary>A white rounded card; the returned <see cref="Border" />'s child is <paramref name="content" />.</summary>
        private static UIElement Elevated(UIElement content)
        {
            return new Border
                   {
                       Background = WhiteBrush, BorderBrush = CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = content,
                       Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 1, Direction = 270, Opacity = 0.08, Color = Colors.Black }
                   };
        }

        private Button ToolButton(string glyph, string tip, Action action)
        {
            var button = MakeButton(glyph, false);
            button.FontFamily = Glyphs;
            button.Padding    = new Thickness(10, 6, 10, 6);
            button.Margin     = new Thickness(0, 0, 6, 0);
            button.ToolTip    = tip;
            button.Click     += (_, _) => action();
            return button;
        }

        private static Button MakeButton(string text, bool primary)
        {
            return new Button
                   {
                       Content = text, Template = ButtonTemplate.Value, Padding = new Thickness(24, 9, 24, 9), FontSize = 13, FontWeight = FontWeights.SemiBold,
                       Background = primary ? Accent : WhiteBrush, Foreground = primary ? WhiteBrush : TextBrush, BorderBrush = primary ? Accent : CardBorder,
                       BorderThickness = new Thickness(1), Cursor = Cursors.Hand
                   };
        }

        private static UIElement IconFor(ComponentInfo info, double size)
        {
            if (info.Icon is { } uri)
                try
                {
                    return new Image { Source = new BitmapImage(uri), Width = size, Height = size, Stretch = Stretch.Uniform };
                }
                catch
                {
                    // a bad icon uri falls back to the glyph or letter
                }

            var colors = BadgeColors[(int)((uint)info.Name.Aggregate(7, (h, c) => unchecked(h * 31 + c)) % (uint)BadgeColors.Length)];
            var badge = new Border
                        {
                            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28),
                            Background = new LinearGradientBrush(Rgb(colors.From), Rgb(colors.To), 45)
                        };

            badge.Child = !string.IsNullOrEmpty(info.Glyph)
                              ? new TextBlock { Text = info.Glyph, FontFamily = Glyphs, FontSize = size * 0.5, Foreground = WhiteBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                              : new TextBlock
                                {
                                    Text = info.Name.Length > 0 ? info.Name[..1].ToUpperInvariant() : "?", FontSize = size * 0.5, FontWeight = FontWeights.Bold,
                                    Foreground = WhiteBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                                };
            return badge;
        }

        // ---- motion --------------------------------------------------------------------------------------------

        private static void Pulse(ScaleTransform transform, double to)
        {
            var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(130)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        private void AnimateProgress(double value)
        {
            _progress.Minimum = 0;
            _progress.Maximum = _steps.Count;
            _progress.BeginAnimation(RangeBase.ValueProperty,
                                     new DoubleAnimation(value, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        /// <summary>Brings a page in from the side the person is heading towards, fading it up.</summary>
        private static void Slide(UIElement page, int direction)
        {
            var move = new TranslateTransform(direction * 36, 0);
            page.RenderTransform = move;
            page.Opacity         = 0;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * 36, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }

        // ---- colours ---------------------------------------------------------------------------------------------

        private static Color Rgb(uint rgb)
        {
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        private static Brush Solid(uint rgb)
        {
            var brush = new SolidColorBrush(Rgb(rgb));
            brush.Freeze();
            return brush;
        }
    }
}
