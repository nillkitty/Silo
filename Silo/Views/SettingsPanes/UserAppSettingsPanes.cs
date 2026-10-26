using System.Windows.Controls;
using System.Windows.Media;
using Silo.Platform;
using Silo.Settings;
using AppSettings = Silo.Settings.AppSettings;
using UserSettings = Silo.Settings.UserSettings;
using PromptDialog = Silo.Ui.Windows.PromptDialog;

namespace Silo.Views.SettingsPanes;

// Settings ▸ User Settings (the person's own, in %APPDATA%\Silo) and Settings ▸ App Settings (this computer's).

/// <summary>Settings ▸ User Settings ▸ Authoring Settings (authors).</summary>
public sealed class AuthoringPane() : SettingsPane("Authoring Settings", SettingsAudience.Author)
{
    protected override bool FollowsActiveSilo => false;

    private static void Set(Action<AuthoringSettings> change) => UserSettings.Authoring.Update(change);

    protected override void Build(Panel body)
    {
        var s = UserSettings.Authoring.Value;

        body.Children.Add(Heading("Authoring Tooltips", 0));
        var tooltips = Check("Providers, Interfaces, Data Sources   (Ctrl+Shift)", s.Tooltips, v => Set(x => x.Tooltips = v), 12);
        body.Children.Add(tooltips);
        var aspects = Aspects("Tooltip Aspects", AuthoringSettings.TooltipAspects, s, _ => Pink);
        DependOn(tooltips, aspects);
        body.Children.Add(aspects);

        body.Children.Add(Heading("Authoring Menus"));
        var menu = Check("Authoring Context Menu   (Ctrl+Right Click)", s.ContextMenu, v => Set(x => x.ContextMenu = v), 12,
                         "The pink design menu. Off: Ctrl+right-click shows it only at Developer level with the developer context menu on.");
        body.Children.Add(menu);
        var pieces = Aspects("Menu Pieces", AuthoringSettings.MenuPieces, s, _ => Pink);
        DependOn(menu, pieces);
        body.Children.Add(pieces);
    }

    private static Border Aspects(string title, IReadOnlyList<string> names, AuthoringSettings s, Func<string, Brush> glyph)
    {
        var list = new StackPanel();
        foreach (string name in names)
        {
            string n = name;
            list.Children.Add(GlyphCheck("♥", glyph(n), n, s.Has(n), v => Set(x => x.Set(n, v))));
        }

        return Box(title, SettingsAudience.Author, list, 44);
    }
}

/// <summary>Settings ▸ User Settings ▸ Developer Settings (developers).</summary>
public sealed class DeveloperPane() : SettingsPane("Developer Settings", SettingsAudience.Developer)
{
    protected override bool FollowsActiveSilo => false;

    private static void Set(Action<DeveloperSettings> change) => UserSettings.Developer.Update(change);

    protected override void Build(Panel body)
    {
        var s = UserSettings.Developer.Value;

        body.Children.Add(Heading("Developer Tooltips", 0));
        var tooltips = Check("Developer Tooltips   (Alt+Ctrl)", s.Tooltips, v => Set(x => x.Tooltips = v), 12);
        body.Children.Add(tooltips);
        var aspects = Aspects("Dev Tooltip Aspects", DeveloperSettings.TooltipAspects, s,
                              n => n switch
                                   {
                                       "Errors"       => Red,
                                       "Log Messages" => Orange,
                                       _              => Purple
                                   });
        DependOn(tooltips, aspects);
        body.Children.Add(aspects);

        body.Children.Add(Heading("Developer Menus"));
        var menu = Check("Developer Context Menu   (Ctrl+Right Click, Developer level)", s.ContextMenu, v => Set(x => x.ContextMenu = v), 12);
        body.Children.Add(menu);
        var pieces = Aspects("Dev Menu Pieces", DeveloperSettings.MenuPieces, s, _ => Purple);
        DependOn(menu, pieces);
        body.Children.Add(pieces);
    }

    private static Border Aspects(string title, IReadOnlyList<string> names, DeveloperSettings s, Func<string, Brush> glyph)
    {
        var list = new StackPanel();
        foreach (string name in names)
        {
            string n = name;
            list.Children.Add(GlyphCheck("■", glyph(n), n, s.Has(n), v => Set(x => x.Set(n, v))));
        }

        return Box(title, SettingsAudience.Developer, list, 12);
    }
}

/// <summary>Settings ▸ App Settings ▸ Auto-Update.</summary>
public sealed class AutoUpdatePane() : SettingsPane("Auto-Update", SettingsAudience.Any)
{
    private const string NoSource = "No update source is configured yet, so there is nothing to update from.";

    protected override bool FollowsActiveSilo => false;

    protected override void Build(Panel body)
    {
        var s = AppSettings.AutoUpdate.Value;

        body.Children.Add(Heading("App Updates", 0));

        var versions = new Grid { Margin = new Thickness(16, 0, 0, 4) };
        versions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        versions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        versions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        versions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        versions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Place(versions, Note("Current Version"), 0, 0);
        Place(versions, Note("Available"),       0, 1);
        Place(versions, ReadOnlyBox(typeof(App).Assembly.GetName().Version?.ToString() ?? "?"), 1, 0);
        Place(versions, ReadOnlyBox("—", NoSource), 1, 1);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var update  = MakeButton("Update", null, NoSource);
        update.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(update);
        buttons.Children.Add(MakeButton("Update on Close", null, NoSource));
        Place(versions, buttons, 2, 1);
        body.Children.Add(versions);

        body.Children.Add(Heading("Other Components", 10));
        var list = new StackPanel();
        Brush[] colours = [Red, Orange, Amber, Green, Blue, Purple, Pink];
        int i = 0;
        foreach (string component in AutoUpdateSettings.Components)
        {
            string c = component;
            list.Children.Add(GlyphCheck("■", colours[i++ % colours.Length], c, !s.NeverUpdate.Contains(c),
                                         v => AppSettings.AutoUpdate.Update(x =>
                                                                             {
                                                                                 x.NeverUpdate.Remove(c);
                                                                                 if (!v) x.NeverUpdate.Add(c);
                                                                             })));
        }

        var frame = new Border { BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 4, 6, 4), Child = list };
        frame.SetResourceReference(Border.BorderBrushProperty, "Theme.BorderStrong");

        var never = MakeButton("Never update", () =>
                                               {
                                                   AppSettings.AutoUpdate.Update(x => x.NeverUpdate = [.. AutoUpdateSettings.Components]);
                                                   Rebuild();
                                               }, toolTip: "Clear every box: nothing updates on its own.");
        never.Margin = new Thickness(0, 30, 0, 0);
        body.Children.Add(WithButtons(frame, MakeButton("Update all", null, NoSource), MakeButton("Update", null, NoSource), never));
        body.Children.Add(Note("Ticked kinds of component are kept up to date on their own."));
    }

    private static TextBox ReadOnlyBox(string text, string? toolTip = null)
    {
        return new TextBox { Text = text, IsReadOnly = true, Width = 130, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 16, 0), ToolTip = toolTip };
    }

    private static void Place(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}

/// <summary>Settings ▸ App Settings ▸ Registered Handlers: the URI schemes whose links Windows opens with Silo.</summary>
public sealed class RegisteredHandlersPane() : SettingsPane("Registered Handlers", SettingsAudience.Any)
{
    protected override bool FollowsActiveSilo => false;

    protected override void Build(Panel body)
    {
        body.Children.Add(Heading("Global Handlers owned by Silo", 0));
        body.Children.Add(Note("Links of these schemes open in Silo when they are enabled (for you, on this computer)."));

        var list = PairList(120, "Scheme", "Opened by", 110);
        foreach (string scheme in AppSettings.Handlers.Value.All)
        {
            var (owner, command) = SchemeRegistration.OwnerOf(scheme);
            string by = owner switch
                        {
                            SchemeOwner.Silo  => "Silo",
                            SchemeOwner.Other => "another program: " + command,
                            _                 => "(nothing)"
                        };
            list.Items.Add(new SettingsPair(scheme + ":", by, scheme));
        }

        body.Children.Add(WithButtons(list,
                                      MakeButton("Disable", () => Disable(list), toolTip: "Stop Silo opening links of the selected scheme."),
                                      MakeButton("Enable",  () => Enable(list),  toolTip: "Open links of the selected scheme with Silo."),
                                      MakeButton("Add…",    Add)));
    }

    private static string? Selected(ListView list) => (list.SelectedItem as SettingsPair)?.Tag as string;

    private void Enable(ListView list)
    {
        if (Selected(list) is not { } scheme) return;

        var (owner, command) = SchemeRegistration.OwnerOf(scheme);
        if (owner == SchemeOwner.Other &&
            MessageBox.Show($"{scheme}: links are opened by\n\n{command}\n\nOpen them with Silo instead (for you)? Disabling it again gives them back.",
                            "Registered Handlers", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        SchemeRegistration.Register(scheme);
        Rebuild();
    }

    private void Disable(ListView list)
    {
        if (Selected(list) is not { } scheme) return;

        if (!SchemeRegistration.Unregister(scheme))
            App.Alert($"Silo is not registered (for you) as what opens {scheme}: links, so there is nothing to disable.", "Registered Handlers");
        Rebuild();
    }

    private void Add()
    {
        if (PromptDialog.Ask("Add handler", "The URI scheme (the part before the colon, such as sql):", "Add") is not { } text) return;

        string scheme = text.Trim().TrimEnd(':').ToLowerInvariant();
        if (!SchemeRegistration.IsValidScheme(scheme))
        {
            App.Alert($"\"{text}\" is not a URI scheme: start with a letter, then use letters, digits, +, - or .", "Add handler");
            return;
        }

        AppSettings.Handlers.Update(s =>
                                    {
                                        if (!s.All.Contains(scheme, StringComparer.OrdinalIgnoreCase)) s.Added.Add(scheme);
                                    });
        Rebuild();
    }
}
