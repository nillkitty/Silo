using Silo.Shell;
using Telefrag.DI;

namespace Silo.Labs.Shell;

/// <summary>
///     Designs Shell packages: the protocol handlers, file types, right-click actions and submenus, icons, Send To and New items and program names
///     that Windows' shell is told about. The package is kept in the Silo as JSON; the Lab shows the menus it makes, the registry values it
///     writes, and installs it (and takes it away again).
/// </summary>
[Singleton(typeof(ILab))]
public class ShellLab : LabBase<ShellPackage>
{
    public override string Description => "Design a Shell package: protocol handlers, file types, right-click actions and submenus, icons, Send To and New items. See the menus it makes, " +
                                          "the registry values it writes, keep it in the Silo as JSON, and install it (or uninstall it again).";

    public override string IconKey => "Puzzle";

    public override string Id => "shell";

    public override string Name => "Shell Lab";

    public override int Order => 60;

    public override LabSession<ShellPackage> CreateSession() => new ShellLabSession(this);

    protected override bool TryConvert(object? candidate, out ShellPackage? value)
    {
        switch (candidate)
        {
            case ShellPackage package:
                value = package;
                return true;
            // asked of every node when a menu is built: only the shell:Name way of referring to a kept package is taken
            case string text when text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && text.Length > 6:
                value = ShellLabSession.Kept.Load(text[6..].Trim());
                return value is not null;
            default:
                value = null;
                return false;
        }
    }

    /// <summary>A text is the package's JSON, or the name of a kept package.</summary>
    public override bool TryParse(string text, out ShellPackage? value)
    {
        if (text.TrimStart().StartsWith('{') && ShellJson.FromJson(text, out var package, out _))
        {
            value = package;
            return true;
        }

        value = ShellLabSession.Kept.Load(text.Trim());
        return value is not null;
    }
}

/// <summary>Packages to start from.</summary>
internal static class ShellTemplates
{
    private const string App = @"C:\Program Files\MyApp\MyApp.exe";

    public static IReadOnlyList<(string Title, Func<ShellPackage> Make)> All { get; } =
    [
        ("Open with my app (all files)", () => new ShellPackage
                                               {
                                                   Name = "OpenWithMyApp", Description = "An Open with MyApp action on every file",
                                                   Entries = [new ShellEntry { Kind = ShellEntryKind.Verb, Target = "*", Name = "OpenWithMyApp", Label = "Open with My&App", Command = $"\"{App}\" \"%1\"", Icon = App + ",0" }]
                                               }),
        ("Protocol handler", () => new ShellPackage
                               {
                                   Name = "MyAppProtocol", Description = "myapp:// addresses open MyApp",
                                   Entries = [new ShellEntry { Kind = ShellEntryKind.Protocol, Name = "myapp", Description = "MyApp Protocol", Command = $"\"{App}\" \"%1\"", Icon = App + ",0" }]
                               }),
        ("File type with a New item", () => new ShellPackage
                                            {
                                                Name = "MyAppFiles", Description = ".myapp documents: a file type, double-click, and an item in the New menu",
                                                Entries =
                                                [
                                                    new ShellEntry { Kind = ShellEntryKind.FileType, Name = "MyApp.Document", Description = "MyApp Document", Command = $"\"{App}\" \"%1\"", Icon = App + ",1", Extensions = [".myapp"], MakeDefault = true },
                                                    new ShellEntry { Kind = ShellEntryKind.NewFile, Name = ".myapp", Label = "New MyApp Document", Content = "{}" }
                                                ]
                                            }),
        ("Folder background submenu", () => new ShellPackage
                                            {
                                                Name = "MyAppTools", Description = "A MyApp submenu when right-clicking the background of a folder",
                                                Entries =
                                                [
                                                    new ShellEntry
                                                    {
                                                        Kind = ShellEntryKind.Submenu, Target = @"Directory\Background", Name = "MyAppTools", Label = "My&App", Icon = App + ",0", Position = ShellPosition.Top,
                                                        Children =
                                                        [
                                                            new ShellEntry { Kind = ShellEntryKind.Verb, Name = "OpenHere", Label = "Open Here", Command = $"\"{App}\" \"%V\"" },
                                                            new ShellEntry { Kind = ShellEntryKind.Verb, Name = "Scan", Label = "Scan This Folder", Command = $"\"{App}\" --scan \"%V\"" }
                                                        ]
                                                    }
                                                ]
                                            }),
        ("Send To shortcut", () => new ShellPackage
                                  {
                                      Name = "MyAppSendTo", Description = "Send files to MyApp",
                                      Entries = [new ShellEntry { Kind = ShellEntryKind.SendTo, Name = "MyApp", Command = App }]
                                  })
    ];

    /// <summary>A new entry of a kind, with the fields that kind needs filled in as an example.</summary>
    public static ShellEntry NewEntry(ShellEntryKind kind, string name)
    {
        var e = new ShellEntry { Kind = kind, Name = name };
        switch (kind)
        {
            case ShellEntryKind.Protocol:
                e.Description = name + " Protocol";
                e.Command     = $"\"{App}\" \"%1\"";
                break;
            case ShellEntryKind.FileType:
                e.Name        = "MyApp." + name;
                e.Description = "MyApp file";
                e.Command     = $"\"{App}\" \"%1\"";
                break;
            case ShellEntryKind.Verb:
                e.Target  = "*";
                e.Label   = "New Action";
                e.Command = $"\"{App}\" \"%1\"";
                break;
            case ShellEntryKind.Submenu:
                e.Target = "*";
                e.Label  = "New Submenu";
                break;
            case ShellEntryKind.ClassIcon:
                e.Name   = string.Empty;
                e.Target = ".myapp";
                e.Icon   = App + ",0";
                break;
            case ShellEntryKind.DefaultVerb:
                e.Target = "*";
                break;
            case ShellEntryKind.SendTo:
                e.Command = App;
                break;
            case ShellEntryKind.NewFile:
                e.Name  = ".myapp";
                e.Label = "New File";
                break;
            case ShellEntryKind.AppPath:
                e.Name    = "myapp.exe";
                e.Command = App;
                break;
            case ShellEntryKind.Application:
                e.Name    = "myapp.exe";
                e.Label   = "MyApp";
                e.Command = $"\"{App}\" \"%1\"";
                break;
            case ShellEntryKind.Registry:
                e.Name = string.Empty;
                e.Key  = @"HKCU\Software\MyApp";
                break;
        }

        return e;
    }
}
