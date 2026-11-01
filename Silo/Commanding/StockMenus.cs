using System.Diagnostics;
using System.Windows.Controls;
using Serilog;
using Silo.Design;
using Silo.Drawio;
using Silo.Labs;
using Silo.Settings;
using Silo.Shell;
using Silo.TextEditing;
using Silo.Tools;
using Silo.Toolbar;
using Silo.Ui;
using Silo.Ui.Windows;
using Silo.Converters;
using Silo.Model;

namespace Silo.Commanding;

/// <summary>
///     The menu commands that are the app's own and are not on the toolbar catalog, and the lists the menus fill in as they open (recent Silos,
///     diagrams, Labs, tools). Each names its group (<see cref="MenuGroups" />), the way a command a component adds does.
/// </summary>
public static class StockMenus
{
    private static OpenSilo? Active => App.Instance?.ActiveSilo;

    private static Window? MainWindow => Application.Current?.MainWindow;

    static StockMenus()
    {
        MenuCommands.Add(MenuGroups.ToolsMgmt, ToolsManagement);
        MenuCommands.AddEntries(MenuGroups.DocMru, RecentSilosEntries);
        MenuCommands.AddEntries(MenuGroups.DiagramList, DiagramEntries);
        MenuCommands.AddEntries(MenuGroups.LabsList, LabEntries);
        MenuCommands.AddEntries(MenuGroups.ToolsList, ToolEntries);
    }

    /// <summary>The commands that never change.</summary>
    public static IReadOnlyList<ProvidedCommand> Commands { get; } = Build();

    private static ProvidedCommand Late(string id, string header, Func<System.Windows.Input.ICommand?> resolve, object? parameter, string? tip, string group)
    {
        return new ProvidedCommand(id, header, new LateBoundCommand(resolve), parameter, null, tip, null, CommandPlacement.Menu, MenuGroup: group);
    }

    private static List<ProvidedCommand> Build()
    {
        var list = new List<ProvidedCommand>();

        // ---- File: the text editor (TextEditing/TextEditors.cs)
        var languages = new (string Header, TextLanguage Language)?[]
                        {
                            ("_Plain Text", TextLanguage.PlainText), ("_JSON", TextLanguage.Json), ("_XML", TextLanguage.Xml), ("_YAML", TextLanguage.Yaml),
                            ("_C# Script", TextLanguage.CSharp), ("_F# Script", TextLanguage.FSharp), ("Po_werShell Script", TextLanguage.PowerShell), ("P_ython Script", TextLanguage.Python),
                            ("_Batch Script", TextLanguage.Batch)
                        };
        var newText = languages.Select(l => ProvidedCommand.From("text.new." + l!.Value.Language, l.Value.Header, () => TextEditors.OpenNew(l.Value.Language))).ToList();
        list.Add(ProvidedCommand.Submenu("text.new", "New _Text Document", newText) with { MenuGroup = MenuGroups.DocMgmt });
        list.Add(ProvidedCommand.From("text.open", "Open Te_xt File...", TextEditors.OpenFileDialog, null, null,
                                      "Open a file in the text editor (JSON, XML and YAML are coloured, checked and can be formatted)", null, CommandPlacement.Menu, MenuGroups.DocMgmt));

        // ---- Silo: Add Data and Add Address Space (the parameter picks which)
        ProvidedCommand Table(string key, string header, string tip) => Late("silo.adddata." + key, header, () => Active?.Commands.NewTable, key, tip, MenuGroups.AddDataCmds);
        var tabular = ProvidedCommand.Submenu("silo.adddata.tabular", "_Tabular",
                                              [
                                                  Table("rich", "_Rich Text", "A table whose every column is rich text (bold, italic, code, links)"),
                                                  Table("plain", "_Plain Text", "A table whose every column is literal text"),
                                                  Table("typed", "T_yped Columns", "Starts with a text, a number and a date column; change each column's type as needed"),
                                                  Table("clipboard", "From _Clipboard...", "A table made of the table or text on the clipboard (Excel, a web page, CSV...)")
                                              ]);
        list.Add(ProvidedCommand.Submenu("silo.adddata", "Add _Data", [tabular]) with { MenuGroup = MenuGroups.AddDataCmds });

        ProvidedCommand Space(string key, string header, string? tip) => Late("silo.addspace." + key, header, () => Active?.Commands.AddAddressSpace, key, tip, MenuGroups.AddDataCmds);
        list.Add(ProvidedCommand.Submenu("silo.addspace", "Add _Address Space",
                                         [
                                             Space("4", "_IPv4", "0.0.0.0/0 and everything below it: registries, reserved blocks, reverse DNS, WHOIS"),
                                             Space("6", "IPv_6", "::/0 and everything below it: registries, reserved blocks, reverse DNS, WHOIS"),
                                             Space("both", "IPv4 and IPv6 _Both", null),
                                             Space("dns", "_Domain Names", "The root zone, top-level domains and the names you go to, with their records (A, MX, NS, TXT, DNSSEC...)")
                                         ]) with { MenuGroup = MenuGroups.AddDataCmds });

        // ---- Diagram
        list.Add(ProvidedCommand.From("diagram.new", "_New Diagram...", () => DiagramActions.New(Active), "TbNew", () => Active is not null, "Make an empty diagram in the Silo and open it",
                                      null, CommandPlacement.Menu, MenuGroups.NewDiagramCmds));
        return list;
    }

    // ---- Tools

    private static IEnumerable<ProvidedCommand> ToolsManagement()
    {
        yield return ProvidedCommand.From("tools.shellpackages", "Shell _Packages...", () => ShellPackagesWindow.ShowFor(MainWindow), "Puzzle", null,
                                          "Shell packages installed on this system (protocol handlers, file types, right-click actions...): uninstall them or install one from a file",
                                          null, CommandPlacement.Menu, MenuGroups.ToolsMgmt);
        if (App.ShowToolsMenu)
            yield return ProvidedCommand.From("tools.manage", "Manage Tools...", () => ManageToolsWindow.ShowFor(MainWindow), null, null,
                                              "Turn tools on or off and put them in order (the Tools menu and the toolbar)", null, CommandPlacement.Menu, MenuGroups.ToolsMgmt);

        yield return ProvidedCommand.From("tools.break", "Break Debugger", Debugger.Break, null, () => Debugger.IsAttached, null, null, CommandPlacement.Menu, MenuGroups.ToolsMgmt);
    }

    private static IEnumerable<Control> ToolEntries(MenuContext ctx)
    {
        string? group = null;
        foreach (var tool in ToolbarCatalog.AllTools().Where(t => t.IsVisible))
        {
            if (group is not null && !string.Equals(group, tool.GroupName, StringComparison.Ordinal)) yield return new Separator();
            group = tool.GroupName;

            var t = tool;
            var item = new MenuItem
                       {
                           Header      = t.Header, // a tool's name carries its own access key ("_Battery Monitor")
                           IsCheckable = true,
                           IsChecked   = t.IsChecked,
                           IsEnabled   = t.IsEnabled,
                           Icon        = IconResolver.ElementFor(t.Icon)
                       };
            item.Click += (_, _) => t.Toggle();
            yield return item;
        }
    }

    // ---- File: recent Silos

    private static IEnumerable<Control> RecentSilosEntries(MenuContext ctx)
    {
        int n = 0;
        foreach (var recent in RecentSilos.Instance.Items.Take(8))
        {
            var r = recent;
            var item = new MenuItem { Header = $"_{++n} {r.Name.Replace("_", "__")}", ToolTip = r.Target };
            item.Click += (_, _) => _ = RecentOpen(r);
            yield return item;
        }
    }

    private static async Task RecentOpen(RecentSilo entry)
    {
        try
        {
            await OpenSiloCommand.OpenPathAsync(entry.Target);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening the recent Silo {Target} failed", entry.Target);
            App.Status($"Could not open {entry.Name}: {ex.Message}");
        }
    }

    // ---- Diagram: the Silo's diagrams

    private static IEnumerable<Control> DiagramEntries(MenuContext ctx)
    {
        var silo = Active;
        if (silo?.Data is not IDiagramStore store) yield break;

        IReadOnlyList<DbModel.Global.Diagram> diagrams;
        try
        {
            diagrams = store.ListDiagrams();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing the diagrams for the Diagram menu failed");
            yield break;
        }

        foreach (var d in diagrams)
        {
            int id   = d.Id;
            var item = new MenuItem { Header = d.Name.Replace("_", "__") };
            item.Click += (_, _) => DiagramActions.Open(silo, id);
            yield return item;
        }
    }

    // ---- Lab: the Labs, and the Lab windows that are open

    private static IEnumerable<Control> LabEntries(MenuContext ctx)
    {
        var labs = LabHost.Labs;
        foreach (var lab in labs)
        {
            var l    = lab;
            var item = new MenuItem { Header = "Open " + l.Name.Replace("_", "__"), ToolTip = l.Description };

            // always set, even to nothing: inside the menu bar an implicit style would otherwise read an icon off the command
            item.Icon  =  ToolbarIcons.Find(l.IconKey) is { } geometry ? new AppIcon { Icon = geometry, Size = 16 } : null;
            item.Click += (_, _) => LabHost.Open(l);
            yield return item;
        }

        if (labs.Count == 0) yield return new MenuItem { Header = "(no labs)", IsEnabled = false };

        var windows = LabHost.Windows;
        if (windows.Count > 0) yield return new Separator();
        foreach (var window in windows)
        {
            var w    = window;
            var item = new MenuItem { Header = "Window: " + LabUi.Shorten(w.Title.Replace("_", "__")), ToolTip = w.Title };
            item.Click += (_, _) =>
                          {
                              if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                              w.Activate();
                          };
            yield return item;
        }
    }
}
