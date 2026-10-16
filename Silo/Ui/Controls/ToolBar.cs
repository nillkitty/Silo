using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Silo.Toolbar;

namespace Silo.Ui;

// Fully-qualifies the base type (rather than `using System.Windows.Controls;` + a bare
// `ToolBar` base) because this class is itself named ToolBar in a different namespace -
// an unqualified reference here would be ambiguous with itself.

/// <summary>
///     The main window's toolbar (below the menu bar): buttons for the File / Edit / Help and Silo commands, the commands of
///     the node selected in the Explorer, the commands tools offer, a Tools drop-down for every other tool, and a Customize
///     button. What is shown, and in which order, is the person's <see cref="ToolbarLayout" /> (<see cref="ToolbarSettings" />);
///     it is rebuilt whenever that changes, and when the selected Explorer node (so its commands) changes.
/// </summary>
public class ToolBar : System.Windows.Controls.ToolBar
{
    private static Style? _buttonStyle;

    public ToolBar()
    {
        // an implicit style only applies to its exact target type; this class derives from ToolBar, so ask for the themed one
        SetResourceReference(StyleProperty, typeof(System.Windows.Controls.ToolBar));

        ContextMenu = BuildContextMenu();
        Loaded     += OnLoaded;
        Unloaded   += OnUnloaded;
    }

    private static Style ButtonStyle => _buttonStyle ??= (Style)XamlReader.Parse(ButtonStyleXaml);

    /// <summary>A flat button that takes its colours from the theme (the stock toolbar button would not follow Light/Dark).</summary>
    private const string ButtonStyleXaml = """
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
               TargetType="{x:Type Button}">
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="Foreground" Value="{DynamicResource Theme.Text}" />
            <Setter Property="Padding" Value="5,4" />
            <Setter Property="Margin" Value="1,0" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="{x:Type Button}">
                        <Border x:Name="Bd" Background="{TemplateBinding Background}" CornerRadius="3"
                                Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Theme.SurfaceHover}" />
                            </Trigger>
                            <Trigger Property="IsPressed" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Theme.SurfacePressed}" />
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Foreground" Value="{DynamicResource Theme.Disabled}" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """;

    // ---- life cycle ----------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ToolbarSettings.Changed += OnLayoutChanged;
        NodeCommandHub.Changed  += OnNodeChanged;
        if (App.Instance is { } app) app.ActiveSiloChanged += OnActiveSiloChanged;

        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ToolbarSettings.Changed -= OnLayoutChanged;
        NodeCommandHub.Changed  -= OnNodeChanged;
        if (App.Instance is { } app) app.ActiveSiloChanged -= OnActiveSiloChanged;
    }

    private void OnLayoutChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Rebuild);

    private void OnNodeChanged(object? sender, EventArgs e)
    {
        // only a toolbar that shows node commands has anything to redo
        if (ToolbarSettings.Layout.Slots.Any(s => s.Id == ToolbarLayout.NodeCommandsId))
            Dispatcher.InvokeAsync(Rebuild);
    }

    // Silo commands are looked up when used, so the buttons only need to re-ask whether they are enabled
    private void OnActiveSiloChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(CommandManager.InvalidateRequerySuggested);

    // ---- building ------------------------------------------------------------------------------

    /// <summary>Builds the buttons from the current layout.</summary>
    public void Rebuild()
    {
        var layout = ToolbarSettings.Layout;
        Visibility = layout.Visible ? Visibility.Visible : Visibility.Collapsed;

        Items.Clear();
        var placedTools = layout.Slots.Select(s => s.Id).Where(id => id.StartsWith(ToolbarCatalog.ToolCommandPrefix, StringComparison.Ordinal)).ToHashSet();

        foreach (var slot in layout.Slots)
        {
            if (slot.IsSeparator)
            {
                Items.Add(new Separator());
                continue;
            }

            var def = ToolbarCatalog.Find(slot.Id);
            if (def is null) continue; // a tool that is not there (any more): its slot stays in the layout, nothing is shown

            try
            {
                switch (def.Kind)
                {
                    case ToolbarItemKind.Command:
                        Items.Add(CommandButton(def.CreateCommand(), def.Parameter, slot.IconKey ?? def.IconKey, slot.Header ?? def.Header,
                                                TipOf(slot, def), slot.ShowText));
                        break;

                    case ToolbarItemKind.Customize:
                        Items.Add(CommandButton(new Silo.Commanding.DelegateCommand(_ => CustomizeToolbarWindow.ShowFor(Window.GetWindow(this))), null,
                                                slot.IconKey ?? def.IconKey, slot.Header ?? def.Header, TipOf(slot, def), slot.ShowText));
                        break;

                    case ToolbarItemKind.ToolsDropdown:
                        Items.Add(ToolsDropdown(slot, def));
                        break;

                    case ToolbarItemKind.ToolCommands:
                        foreach (var d in ToolbarCatalog.ToolCommands().Where(d => !placedTools.Contains(d.Id)))
                            Items.Add(CommandButton(d.CreateCommand(), d.Parameter, d.IconKey, d.Header, d.ToolTip, false));
                        break;

                    case ToolbarItemKind.NodeCommands:
                        AddNodeCommands();
                        break;
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Building the toolbar item {id} failed", slot.Id);
            }
        }

        TidySeparators();
    }

    private static string TipOf(ToolbarSlot slot, ToolbarItemDef def)
    {
        if (!string.IsNullOrWhiteSpace(slot.ToolTip)) return slot.ToolTip;
        if (!string.IsNullOrWhiteSpace(slot.Header)) return ToolbarText.Plain(slot.Header);
        return def.ToolTip ?? def.PlainHeader;
    }

    private void AddNodeCommands()
    {
        string? group = null;
        foreach (var c in NodeCommandHub.CommandsFor(NodeCommandHub.Current).Where(c => c.OnToolbar))
        {
            if (group is not null && group != c.Group) Items.Add(new Separator());
            group = c.Group;

            Items.Add(CommandButton(c.Command, c.Parameter, c.IconKey, c.Header, c.ToolTip ?? ToolbarText.Plain(c.Header), c.IconKey is null));
        }
    }

    /// <summary>Removes separators at the ends and doubled ones (a group that turned out empty leaves its separators behind).</summary>
    private void TidySeparators()
    {
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            bool bad = Items[i] is Separator && (i == 0 || i == Items.Count - 1 || Items[i - 1] is Separator);
            if (bad) Items.RemoveAt(i);
        }
    }

    private static Button CommandButton(ICommand? command, object? parameter, string? iconKey, string header, string? tip, bool showText)
    {
        var button = new Button
                     {
                         Style            = ButtonStyle,
                         Command          = command,
                         CommandParameter = parameter,
                         Content          = ButtonContent(iconKey, ToolbarText.Plain(header), showText, false),
                         ToolTip          = string.IsNullOrWhiteSpace(tip) ? ToolbarText.Plain(header) : tip
                     };
        ToolTipService.SetShowOnDisabled(button, true);
        return button;
    }

    private static object ButtonContent(string? iconKey, string label, bool showText, bool dropdown)
    {
        var icon = ToolbarIcons.Find(iconKey);
        if (icon is null) showText = true; // nothing else to tell the button apart

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null) panel.Children.Add(new AppIcon { Icon = icon, Size = 16 });
        if (showText)
            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = icon is null ? new Thickness() : new Thickness(5, 0, 0, 0) });
        if (dropdown)
            panel.Children.Add(new TextBlock { Text = "▾", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 1, 0, 0) });

        return panel;
    }

    // ---- Tools drop-down ---------------------------------------------------------------------------

    private Button ToolsDropdown(ToolbarSlot slot, ToolbarItemDef def)
    {
        var button = new Button
                     {
                         Style   = ButtonStyle,
                         Content = ButtonContent(slot.IconKey ?? def.IconKey, ToolbarText.Plain(slot.Header ?? def.Header), slot.ShowText, true),
                         ToolTip = TipOf(slot, def)
                     };
        button.Click += (_, _) => ShowToolsMenu(button);
        return button;
    }

    /// <summary>Opens a menu of the tools that have no buttons of their own; choosing one does what its Tools-menu entry does (open or close it).</summary>
    private static void ShowToolsMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };

        string? group = null;
        foreach (var tool in ToolbarCatalog.DropdownTools())
        {
            if (group is not null && !string.Equals(group, tool.GroupName, StringComparison.Ordinal)) menu.Items.Add(new Separator());
            group = tool.GroupName;

            var t    = tool;
            var item = new MenuItem
                       {
                           Header      = t.Header,
                           IsCheckable = true,
                           IsChecked   = t.IsChecked,
                           IsEnabled   = t.IsEnabled,
                           Icon        = t.Icon is { } image ? new Image { Source = image, Width = 16, Height = 16 } : null
                       };
            item.Click += (_, _) => t.Toggle();
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "(no tools)", IsEnabled = false });

        menu.IsOpen = true;
    }

    // ---- right-click menu --------------------------------------------------------------------------

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var customize = new MenuItem { Header = "_Customize Toolbar..." };
        customize.Click += (_, _) => CustomizeToolbarWindow.ShowFor(Window.GetWindow(this));
        menu.Items.Add(customize);

        var reset = new MenuItem { Header = "_Reset Toolbar" };
        reset.Click += (_, _) => ToolbarSettings.Reset();
        menu.Items.Add(reset);

        menu.Items.Add(new Separator());

        var hide = new MenuItem { Header = "_Hide Toolbar" };
        hide.Click += (_, _) => ToolbarSettings.Visible = false;
        menu.Items.Add(hide);

        return menu;
    }
}
