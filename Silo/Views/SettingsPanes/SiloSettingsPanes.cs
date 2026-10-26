using System.Net.NetworkInformation;
using System.Windows.Controls;
using Newtonsoft.Json;
using Silo.Commanding;
using Silo.Hotkeys;
using Silo.Model;
using Silo.Settings;
using Silo.Providers.Format;
using Dialogs = Silo.Ui.Dialogs;
using HotkeyDialog = Silo.Tools.UnicodeExplorer.HotkeyDialog;
using KeyCaptureBox = Silo.Tools.UnicodeExplorer.KeyCaptureBox;
using PromptDialog = Silo.Ui.Windows.PromptDialog;

namespace Silo.Views.SettingsPanes;

// Settings ▸ Silo Settings: kept in the Silo itself (SiloSettings), shared by everyone who opens it.

/// <summary>Settings ▸ Silo Settings ▸ Assemblies.</summary>
public sealed class AssembliesPane() : SiloSettingsPane("Assemblies", SettingsAudience.Any)
{
    private static string Filter => Formats.Of<AssemblyFormat>().Filter(true);

    protected override void BuildFor(OpenSilo silo, Panel body)
    {
        var s = SiloSettings.Assemblies.Load();

        body.Children.Add(Heading("Required Assemblies", 0));
        body.Children.Add(Note("This Silo requires the following assemblies and cannot load without them being present:"));
        body.Children.Add(AssemblyList(s.Required, "Required"));

        body.Children.Add(Heading("Auxiliary Assemblies"));
        body.Children.Add(Note("These assemblies are used by items in this Silo and are only required to display or edit those items:"));
        body.Children.Add(AssemblyList(s.Auxiliary, "Auxiliary"));

        body.Children.Add(Heading("Extension Assemblies"));
        body.Children.Add(Note("Add tools, data sources, connectors, labs and other resources from these assemblies:"));
        var extensions = AssemblyList(s.Extension, "Extension");
        var remove     = MakeButton("Remove", () => Remove(extensions));
        remove.IsEnabled = false;
        extensions.SelectionChanged += (_, _) => remove.IsEnabled = extensions.SelectedItem is not null;
        body.Children.Add(WithButtons(extensions, MakeButton("Add…", Add), remove));
        body.Children.Add(Note("Right-click an assembly to move it to another list."));
    }

    /// <summary>A list of assembly paths; its right-click menu moves the selected one to another list.</summary>
    private ListBox AssemblyList(List<string> paths, string listName)
    {
        var list = MakeList();
        foreach (string path in paths)
        {
            bool missing = !File.Exists(path);
            list.Items.Add(new ListBoxItem
                           {
                               Content   = System.IO.Path.GetFileName(path) + (missing ? "  (missing)" : string.Empty),
                               ToolTip   = path,
                               Tag       = path,
                               FontStyle = missing ? FontStyles.Italic : FontStyles.Normal
                           });
        }

        var menu = new ContextMenu();
        foreach (string target in new[] { "Required", "Auxiliary", "Extension" }.Where(t => t != listName))
        {
            var item = new MenuItem { Header = $"Move to {target}" };
            item.Click += (_, _) =>
                          {
                              if (list.SelectedItem is ListBoxItem { Tag: string path }) Move(path, target);
                          };
            menu.Items.Add(item);
        }

        var forget = new MenuItem { Header = "Remove from this Silo" };
        forget.Click += (_, _) => Remove(list);
        menu.Items.Add(new Separator());
        menu.Items.Add(forget);
        menu.Opened += (_, _) =>
                       {
                           foreach (var i in menu.Items.OfType<MenuItem>()) i.IsEnabled = list.SelectedItem is not null;
                       };
        list.ContextMenu = menu;
        return list;
    }

    private static List<string> ListNamed(AssembliesSettings s, string name) => name switch
                                                                                {
                                                                                    "Required"  => s.Required,
                                                                                    "Auxiliary" => s.Auxiliary,
                                                                                    _           => s.Extension
                                                                                };

    private void Add()
    {
        if (Dialogs.OpenFiles("Add extension assemblies", Filter) is not { Count: > 0 } files) return;

        SiloSettings.Assemblies.Update(s =>
                                       {
                                           foreach (string f in files)
                                               if (!s.Required.Concat(s.Auxiliary).Concat(s.Extension).Contains(f, StringComparer.OrdinalIgnoreCase))
                                                   s.Extension.Add(f);
                                       });
        Rebuild();
    }

    private void Move(string path, string target)
    {
        SiloSettings.Assemblies.Update(s =>
                                       {
                                           foreach (var l in new[] { s.Required, s.Auxiliary, s.Extension })
                                               l.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
                                           ListNamed(s, target).Add(path);
                                       });
        RebuildLater();
    }

    private void Remove(ListBox list)
    {
        if (list.SelectedItem is not ListBoxItem { Tag: string path }) return;

        SiloSettings.Assemblies.Update(s =>
                                       {
                                           foreach (var l in new[] { s.Required, s.Auxiliary, s.Extension })
                                               l.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
                                       });
        RebuildLater();
    }
}

/// <summary>Settings ▸ Silo Settings ▸ Branding (authors).</summary>
public sealed class BrandingPane() : SiloSettingsPane("Branding", SettingsAudience.Author)
{
    private static string JsonFilter => $"Branding ({Formats.Of<JsonFormat>().Pattern()})|{Formats.Of<JsonFormat>().Pattern()}|{Formats.AllFiles}";

    public override double PreferredWidth => 620;

    protected override void BuildFor(OpenSilo silo, Panel body)
    {
        var s = SiloSettings.Branding.Load();

        body.Children.Add(Note("The following branding customizations are available:"));

        var list = PairList(160, "Customization", "Value", 200);
        foreach (string key in BrandingSettings.Keys)
            list.Items.Add(new SettingsPair(key, s.Values.TryGetValue(key, out string? v) ? v : "(default)", key));
        list.MouseDoubleClick += (_, _) => Configure(list);

        var box = Box("Customizations", SettingsAudience.Developer, list);
        list.Margin = new Thickness(0);
        box.MinWidth = 380;

        body.Children.Add(WithButtons(box,
                                      MakeButton("Configure…", () => Configure(list), toolTip: "Set the selected customization (or double-click it)."),
                                      MakeButton("Load from…", Load, toolTip: "Replace the customizations with those in a file."),
                                      MakeButton("Save Current…", Save, toolTip: "Write these customizations to a file."),
                                      MakeButton("Reset", () => Reset(list), toolTip: "Give the selected customization (or, with none selected, all of them) the app's own value.")));
    }

    private void Configure(ListView list)
    {
        if (list.SelectedItem is not SettingsPair { Tag: string key })
        {
            App.Alert("Choose a customization first.", "Branding");
            return;
        }

        var    current = SiloSettings.Branding.Load().Values;
        string initial = current.TryGetValue(key, out string? v) ? v : string.Empty;
        if (PromptDialog.Ask("Branding", $"The value of {key} in this Silo:", "Set", initial) is not { } value) return;

        SiloSettings.Branding.Update(s => s.Values[key] = value);
        Rebuild();
    }

    private void Reset(ListView list)
    {
        if (list.SelectedItem is SettingsPair { Tag: string key })
            SiloSettings.Branding.Update(s => s.Values.Remove(key));
        else if (MessageBox.Show("Give every customization the app's own value?", "Branding", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            SiloSettings.Branding.Update(s => s.Values.Clear());
        else
            return;

        Rebuild();
    }

    private void Load()
    {
        if (Dialogs.OpenFile("Load branding", JsonFilter) is not { } path) return;

        var loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path)) ??
                     throw new InvalidDataException("The file holds no customizations.");
        var known = loaded.Where(p => BrandingSettings.Keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        if (known.Count == 0) throw new InvalidDataException("The file holds none of the customizations this Silo knows.");

        SiloSettings.Branding.Update(s =>
                                     {
                                         s.Values.Clear();
                                         foreach (var (k, v) in known) s.Values[k] = v;
                                     });
        Rebuild();
    }

    private static void Save()
    {
        if (Dialogs.SaveFile("Save branding", JsonFilter, Formats.Of<JsonFormat>().FileName("branding")) is not { } path) return;
        File.WriteAllText(path, JsonConvert.SerializeObject(SiloSettings.Branding.Load().Values, Formatting.Indented));
    }
}

/// <summary>Settings ▸ Silo Settings ▸ Security/Scripting (developers).</summary>
public sealed class SecurityPane() : SiloSettingsPane("Security/Scripting", SettingsAudience.Developer)
{
    public override double PreferredWidth => 620;

    private static void Set(Action<SecuritySettings> change) => SiloSettings.Security.Update(change);

    protected override void BuildFor(OpenSilo silo, Panel body)
    {
        var s = SiloSettings.Security.Load();

        body.Children.Add(Heading("Enabled Components", 0));

        // queries and scripts side by side, each over a box of its kinds
        var queries = Check("Queries", s.Queries, v => Set(x => x.Queries = v));
        var queryKinds = new StackPanel();
        queryKinds.Children.Add(Check("SQL", s.Sql, v => Set(x => x.Sql = v)));
        queryKinds.Children.Add(Check("XQL", s.Xql, v => Set(x => x.Xql = v)));
        DependOn(queries, queryKinds);

        var scripts = Check("Scripts", s.Scripts, v => Set(x => x.Scripts = v));
        var scriptKinds = new StackPanel();
        scriptKinds.Children.Add(Check("JS   JavaScript", s.JavaScript, v => Set(x => x.JavaScript = v)));
        scriptKinds.Children.Add(Check("FS   F# Script",  s.FSharp,     v => Set(x => x.FSharp     = v)));
        scriptKinds.Children.Add(Check("CS   C# Script",  s.CSharp,     v => Set(x => x.CSharp     = v)));
        scriptKinds.Children.Add(Check("PY   Python",     s.Python,     v => Set(x => x.Python     = v)));
        scriptKinds.Children.Add(Check("PS   PowerShell", s.PowerShell, v => Set(x => x.PowerShell = v)));
        DependOn(scripts, scriptKinds);

        var columns = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.Children.Add(Column(queries, queryKinds));
        var right = Column(scripts, scriptKinds);
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);
        body.Children.Add(columns);

        body.Children.Add(Check(Described("Conditional & Looping Expressions", "(if, switch, for, each, while, ...)"), s.ConditionalExpressions,
                                v => Set(x => x.ConditionalExpressions = v)));
        body.Children.Add(Check(Described("Logical Expressions", "(logical and, or, not, ...)"), s.LogicalExpressions,
                                v => Set(x => x.LogicalExpressions = v)));
        body.Children.Add(Check(Described("Value Expressions", "(avg, min, max, arithmetic operations, ...)"), s.ValueExpressions,
                                v => Set(x => x.ValueExpressions = v)));

        body.Children.Add(Heading("Added Security", 10));
        body.Children.Add(Check("Require assemblies be signed", s.RequireSignedAssemblies, v => Set(x => x.RequireSignedAssemblies = v)));
        body.Children.Add(Check("Require scripts be signed",    s.RequireSignedScripts,    v => Set(x => x.RequireSignedScripts    = v)));
        var confirm = Check("User confirmation required for all execution", s.ConfirmExecution, v => Set(x => x.ConfirmExecution = v));
        var preApprove = Check("Allow user to pre-approve subsequent execution of the same content", s.AllowPreApproval,
                               v => Set(x => x.AllowPreApproval = v), 20);
        DependOn(confirm, preApprove);
        body.Children.Add(confirm);
        body.Children.Add(preApprove);
        body.Children.Add(Check("Require universal packages be signed", s.RequireSignedPackages, v => Set(x => x.RequireSignedPackages = v)));
    }

    private static StackPanel Column(CheckBox master, Panel kinds)
    {
        var column = new StackPanel();
        column.Children.Add(master);
        kinds.Margin = new Thickness(8, 4, 8, 4);
        var frame = new Border { BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4), Margin = new Thickness(20, 4, 20, 10), Child = kinds };
        frame.SetResourceReference(Border.BorderBrushProperty, "Theme.BorderStrong");
        column.Children.Add(frame);
        return column;
    }

    private static TextBlock Described(string name, string description)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new System.Windows.Documents.Run("  " + description));
        return text;
    }
}

/// <summary>Settings ▸ Silo Settings ▸ Snapshots.</summary>
public sealed class SnapshotsPane() : SiloSettingsPane("Snapshots", SettingsAudience.Any)
{
    private const string NotYet = "Snapshots are not available yet: the Silo's storage cannot make copy-on-write snapshots.";

    protected override void BuildFor(OpenSilo silo, Panel body)
    {
        body.Children.Add(Heading("Silo Snapshots", 0));
        body.Children.Add(Note("A snapshot creates a (copy-on-write) point-in-time snapshot of all data in the Silo."));

        var list = MakeList();
        list.Items.Add(new ListBoxItem { Content = "No snapshots yet.", IsEnabled = false });
        body.Children.Add(WithButtons(list,
                                      MakeButton("Open Read-Only",   null, NotYet),
                                      MakeButton("Restore Snapshot", null, NotYet),
                                      MakeButton("Delete",           null, NotYet)));

        var take = MakeButton("Take snapshot", null, NotYet);
        take.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(take);
    }
}

/// <summary>Settings ▸ Silo Settings ▸ Backup/Restore.</summary>
public sealed class BackupPane() : SiloSettingsPane("Backup/Restore", SettingsAudience.Any)
{
    private const string None   = "(none)";
    private const string NotYet = "No backup provider is available yet.";

    protected override void BuildFor(OpenSilo silo, Panel body)
    {
        var s = SiloSettings.Backup.Load();

        body.Children.Add(Heading("Automatic Backup", 0));
        body.Children.Add(Note("Configure and schedule a backup provider to create automatic backups of this Silo."));
        body.Children.Add(WithButtons(Providers(s.AutomaticProvider, v => SiloSettings.Backup.Update(x => x.AutomaticProvider = v)),
                                      MakeButton("Configure…", null, NotYet),
                                      MakeButton("Schedule…",  null, NotYet)));

        body.Children.Add(Rule());

        body.Children.Add(Heading("Restore"));
        body.Children.Add(WithButtons(Providers(s.RestoreProvider, v => SiloSettings.Backup.Update(x => x.RestoreProvider = v)),
                                      MakeButton("Browse Backups…",  null, NotYet),
                                      MakeButton("View Backup Log", null, NotYet)));
    }

    private static ListBox Providers(string? chosen, Action<string?> set)
    {
        var list = MakeList(78);
        list.Items.Add(None);
        foreach (string p in BackupSettings.Providers) list.Items.Add(p);
        list.SelectedItem      =  chosen is not null && BackupSettings.Providers.Contains(chosen) ? chosen : None;
        list.SelectionChanged += (_, _) => set(list.SelectedItem as string is { } p && p != None ? p : null);
        return list;
    }
}

/// <summary>Settings ▸ Silo Settings ▸ Network &amp; Energy.</summary>
public sealed class NetworkPane() : SiloSettingsPane("Network & Energy", SettingsAudience.Any)
{
    private static void Set(Action<NetworkSettings> change) => SiloSettings.Network.Update(change);

    protected override void BuildFor(OpenSilo silo, Panel body)
    {
        var s = SiloSettings.Network.Load();

        body.Children.Add(Heading("Apply the following settings:", 0));
        var energy = Heading("Energy Saver Settings");
        energy.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Accent");
        energy.Margin = new Thickness(16, 4, 0, 4);
        body.Children.Add(energy);

        body.Children.Add(Check("Disable Data Sync on Battery",               s.DisableSyncOnBattery,      v => Set(x => x.DisableSyncOnBattery      = v), 16));
        body.Children.Add(Check("Disable Data Sync when no network is available", s.DisableSyncWithoutNetwork, v => Set(x => x.DisableSyncWithoutNetwork = v), 16));
        body.Children.Add(Check("Disable Auto-Backup on Battery",             s.DisableBackupOnBattery,    v => Set(x => x.DisableBackupOnBattery    = v), 16));

        var names = NetworkNames(s.SyncNetwork, s.BackupNetwork);
        body.Children.Add(OnlyOn("Only run Data Sync on network", s.OnlySyncOnNetwork, s.SyncNetwork, names,
                                 v => Set(x => x.OnlySyncOnNetwork = v), n => Set(x => x.SyncNetwork = n)));
        body.Children.Add(OnlyOn("Only run Auto-Backup on network", s.OnlyBackupOnNetwork, s.BackupNetwork, names,
                                 v => Set(x => x.OnlyBackupOnNetwork = v), n => Set(x => x.BackupNetwork = n)));
    }

    private static Grid OnlyOn(string text, bool on, string network, IReadOnlyList<string> names, Action<bool> setOn, Action<string> setNetwork)
    {
        var check = Check(text, on, setOn);
        var combo = new ComboBox { IsEditable = true, Width = 170, Text = network, ToolTip = "The network (connection name); type one that is not listed." };
        foreach (string n in names) combo.Items.Add(n);
        combo.Text = network;

        void Save()
        {
            string value = (combo.SelectedItem as string ?? combo.Text ?? string.Empty).Trim();
            if (value != network)
            {
                network = value;
                setNetwork(value);
            }
        }

        combo.DropDownClosed     += (_, _) => Save();
        combo.LostKeyboardFocus  += (_, _) => Save();
        DependOn(check, combo);

        var grid = new Grid { Margin = new Thickness(16, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(check);
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);
        return grid;
    }

    /// <summary>The names of this computer's network connections that are up, and the ones already chosen.</summary>
    private static IReadOnlyList<string> NetworkNames(params string[] chosen)
    {
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    names.Add(nic.Name);
        }
        catch (NetworkInformationException)
        {
            // the list is only a convenience: a name can still be typed
        }

        foreach (string c in chosen)
            if (!string.IsNullOrWhiteSpace(c)) names.Add(c);
        return names.ToList();
    }
}

/// <summary>Settings ▸ Silo Settings ▸ Auditing &amp; Undo.</summary>
public sealed class AuditUndoPane() : SiloSettingsPane("Auditing & Undo", SettingsAudience.Any)
{
    protected override void BuildFor(OpenSilo silo, Panel body)
    {
        var s = SiloSettings.AuditUndo.Load();

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddHeader(grid, "Track for Undo/Redo", 1);
        AddHeader(grid, "Keep Audit Log",      2);

        int row = 1;
        foreach (string kind in AuditUndoSettings.Kinds)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = kind + " Actions", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 16, 2) };
            Grid.SetRow(label, row);
            grid.Children.Add(label);

            string k = kind;
            Place(grid, Check(string.Empty, s.IsUndone(k),  v => Change(x => x.SetUndone(k, v))),  row, 1);
            Place(grid, Check(string.Empty, s.IsAudited(k), v => Change(x => x.SetAudited(k, v))), row, 2);
            row++;
        }

        body.Children.Add(grid);
        body.Children.Add(Row("Undo max levels:",            NumberBox(s.UndoMaxLevels,    1, 1000, v => SiloSettings.AuditUndo.Update(x => x.UndoMaxLevels    = v))));
        body.Children.Add(Row("Undo max parallel universes:", NumberBox(s.UndoMaxUniverses, 1, 50,   v => SiloSettings.AuditUndo.Update(x => x.UndoMaxUniverses = v))));
    }

    // "All" and the others follow each other, so the boxes are made again after a change
    private void Change(Action<AuditUndoSettings> change)
    {
        SiloSettings.AuditUndo.Update(change);
        RebuildLater();
    }

    private static void AddHeader(Grid grid, string text, int column)
    {
        var header = new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Accent");
        Grid.SetColumn(header, column);
        grid.Children.Add(header);
    }

    private static void Place(Grid grid, CheckBox box, int row, int column)
    {
        box.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(box, row);
        Grid.SetColumn(box, column);
        grid.Children.Add(box);
    }
}

/// <summary>
///     Settings ▸ Silo Settings ▸ Keyboard: the Silo's global shortcut bindings (<see cref="GlobalShortcutPrefs" />) and the
///     keys of the app's own commands (<see cref="Shortcuts" />).
/// </summary>
public sealed class KeyboardPane : SiloSettingsPane
{
    private static GlobalShortcutPrefs Prefs => GlobalHotkeyService.Current.Prefs;

    private void OnPrefsChanged(object? sender, EventArgs e) => RebuildLater();

    public KeyboardPane() : base("Keyboard", SettingsAudience.Any)
    {
        Loaded   += (_, _) =>
                    {
                        Prefs.Changed -= OnPrefsChanged;
                        Prefs.Changed += OnPrefsChanged;
                    };
        Unloaded += (_, _) => Prefs.Changed -= OnPrefsChanged;
    }

    protected override void BuildFor(OpenSilo silo, Panel body)
    {
        body.Children.Add(Heading("Global Bindings", 0));
        body.Children.Add(Note("Shortcut keys that type or copy text, in any application or only in Silo."));

        var bindings = PairList(110, "Shortcut", "Key and action", 150);
        foreach (var pref in Prefs.Shortcuts)
            bindings.Items.Add(new SettingsPair(pref.Name + (pref.Enabled ? string.Empty : "  (off)"),
                                                pref.IsBound ? $"{pref.Shortcut}   {pref.Action}" : "(not bound)", pref));
        bindings.MouseDoubleClick += (_, _) => ChangeBinding(bindings);
        body.Children.Add(WithButtons(bindings,
                                      MakeButton("Add…",   AddBinding),
                                      MakeButton("Change", () => ChangeBinding(bindings)),
                                      MakeButton("Remove", () => RemoveBinding(bindings))));

        body.Children.Add(Heading("App Commands", 6));
        var commands = PairList(130, "Command", "Key", 170);
        foreach (var command in Shortcuts.Commands)
        {
            var    key  = Shortcuts.KeyOf(command);
            string text = key.IsSet ? key.ToString() : "(none)";
            if (key != command.DefaultKey) text += $"   (default {command.DefaultKey})";
            commands.Items.Add(new SettingsPair(command.Name, text, command));
        }

        commands.MouseDoubleClick += (_, _) => ChangeCommand(commands);
        body.Children.Add(WithButtons(commands,
                                      MakeButton("Change",  () => ChangeCommand(commands)),
                                      MakeButton("Remove",  () => WithCommand(commands, c => Shortcuts.SetKey(c, ShortcutKey.None))),
                                      MakeButton("Default", () => WithCommand(commands, Shortcuts.ResetKey),
                                                 toolTip: "Give the selected command its default key back.")));
    }

    private static T? Selected<T>(ListView list) where T : class => (list.SelectedItem as SettingsPair)?.Tag as T;

    private void WithCommand(ListView list, Action<AppCommandKey> action)
    {
        if (Selected<AppCommandKey>(list) is not { } command)
        {
            None();
            return;
        }

        action(command);
        Rebuild();
    }

    private static void None()
    {
        App.Alert("Choose one in the list first.", "Keyboard");
    }

    private void AddBinding()
    {
        if (HotkeyDialog.Ask(Window.GetWindow(this), Prefs, ShortcutBinding.Unbound(string.Empty), StoredScope.Personal, null) is { } result)
            Prefs.Save(result.Binding, result.Storage);
    }

    private void ChangeBinding(ListView list)
    {
        if (Selected<IGlobalShortcutPref>(list) is not { } pref)
        {
            None();
            return;
        }

        if (HotkeyDialog.Ask(Window.GetWindow(this), Prefs, pref.Value, pref.Storage, pref.Name) is { } result)
            Prefs.Save(result.Binding, result.Storage, pref.Name);
    }

    private void RemoveBinding(ListView list)
    {
        if (Selected<IGlobalShortcutPref>(list) is not { } pref)
        {
            None();
            return;
        }

        if (MessageBox.Show($"Remove the shortcut \"{pref.Name}\"?", "Keyboard", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            Prefs.Remove(pref.Name);
    }

    private void ChangeCommand(ListView list)
    {
        if (Selected<AppCommandKey>(list) is not { } command)
        {
            None();
            return;
        }

        if (KeyPrompt.Ask(Window.GetWindow(this), command) is not { } key) return;

        if (Shortcuts.UsedBy(key, command) is { } other &&
            MessageBox.Show($"{key} is the key of \"{other.Name}\". Give it to \"{command.Name}\" and take it from \"{other.Name}\"?", "Keyboard",
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        if (Shortcuts.UsedBy(key, command) is { } taken) Shortcuts.SetKey(taken, ShortcutKey.None);
        Shortcuts.SetKey(command, key);
        Rebuild();
    }

    /// <summary>A small dialog that takes the key for an app command.</summary>
    private static class KeyPrompt
    {
        public static ShortcutKey? Ask(Window? owner, AppCommandKey command)
        {
            var box = new KeyCaptureBox { Value = Shortcuts.KeyOf(command), Margin = new Thickness(0, 6, 0, 10) };
            var ok  = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = $"Press the key for \"{command.Name}\" (default {command.DefaultKey}):" });
            panel.Children.Add(box);
            panel.Children.Add(buttons);

            var window = new Window
                         {
                             Title                 = "Change key",
                             Content               = panel,
                             SizeToContent         = SizeToContent.WidthAndHeight,
                             ResizeMode            = ResizeMode.NoResize,
                             ShowInTaskbar         = false,
                             Owner                 = owner,
                             WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
                         };
            ok.Click += (_, _) =>
                        {
                            if (!box.Value.IsSet)
                            {
                                MessageBox.Show(window, "Press a key first (or use Remove to take the command's key away).", "Change key");
                                return;
                            }

                            window.DialogResult = true;
                        };
            window.Loaded += (_, _) => box.Focus();

            bool done;
            using (GlobalHotkeyService.Current.Suspend())
            {
                done = window.ShowDialog() == true;
            }

            return done ? box.Value : null;
        }
    }
}
