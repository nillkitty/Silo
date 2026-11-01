using System.IO;
using System.Text;
using Microsoft.Win32;
using Silo.Commanding;
using Silo.Shell;
using Silo.Ui.Windows;

namespace Silo.Labs.Shell;

/// <summary>One Shell Lab window: the package being designed, the entry that is selected, what is wrong with the package, and whether it is installed.</summary>
public sealed partial class ShellLabSession : LabSession<ShellPackage>, ILabLive
{
    private static readonly LabSaved<ShellPackage> Saved = new("shell");

    private readonly Debouncer          _notify;
    private readonly List<LabInspector> _inspectors;
    private          ShellPackage       _pkg = new();
    private          int[]?             _path;
    private          IReadOnlyList<ShellProblem> _problems = [];
    private          bool               _dirty;
    private          bool               _started;

    public ShellLabSession(ShellLab lab) : base(lab)
    {
        _notify     = new Debouncer(TimeSpan.FromMilliseconds(120), Rebuilt);
        _inspectors = [new ShellJsonInspector(this), new ShellRegistryInspector(this), new ShellInstalledInspector(this)];
        SelectionChanged += (_, _) => RaiseActionsChanged();
    }

    internal static LabSaved<ShellPackage> Kept => Saved;

    public ShellPackage Package => _pkg;

    /// <summary>Where the selected entry is: its position, and inside a submenu the position of the verb in it.</summary>
    public int[]? SelectedPath => _path;

    public ShellEntry? Selected => Find(_pkg, _path);

    public IReadOnlyList<ShellProblem> Problems => _problems;

    public bool IsDirty => _dirty;

    public event EventHandler? Changed;

    public event EventHandler? SelectionChanged;

    /// <summary>A different package was opened: the boxes show it afresh.</summary>
    public event EventHandler? Loaded;

    public override ShellPackage? CurrentValue => _pkg.Clone();

    public override string ValueText => string.IsNullOrWhiteSpace(_pkg.Name) ? "(new package)" : "shell:" + _pkg.Name;

    public override string Title => $"{LabOf.Name} - {(string.IsNullOrWhiteSpace(_pkg.Name) ? "new package" : _pkg.Name)}{(_dirty ? " *" : string.Empty)}";

    public override IReadOnlyList<LabInspector> Inspectors => _inspectors;

    public string Json => ShellJson.ToJson(_pkg);

    public string RegText
    {
        get
        {
            if (_problems.Any(p => p.Severity == ShellSeverity.Error))
                return "; The package has errors (see Problems), so the values it would write cannot be listed yet.\r\n" +
                       string.Join("\r\n", _problems.Where(p => p.Severity == ShellSeverity.Error).Select(p => "; " + p.Where + ": " + p.Text));

            return ShellCompiler.ToRegText(ShellCompiler.Compile(_pkg));
        }
    }

    public void Say(string text) => Status = text;

    // ---- finding entries -----------------------------------------------------------------------

    public static ShellEntry? Find(ShellPackage pkg, int[]? path)
    {
        if (path is not { Length: > 0 } || path[0] < 0 || path[0] >= pkg.Entries.Count) return null;

        var e = pkg.Entries[path[0]];
        if (path.Length == 1) return e;

        return path[1] >= 0 && path[1] < e.Children.Count ? e.Children[path[1]] : null;
    }

    private List<ShellEntry>? ListOf(int[] path) => path.Length == 1 ? _pkg.Entries : path[0] < _pkg.Entries.Count ? _pkg.Entries[path[0]].Children : null;

    // ---- opening -------------------------------------------------------------------------------

    protected override Task OpenValueAsync(ShellPackage value)
    {
        Replace(value.Clone(), false);
        return Task.CompletedTask;
    }

    protected override Task OpenDefaultAsync()
    {
        if (!_started) Replace(new ShellPackage { Name = string.Empty }, false);
        return Task.CompletedTask;
    }

    protected override void OnAttached()
    {
        if (!_started) _ = OpenDefaultAsync();
    }

    protected override void OnDispose() => _notify.Cancel();

    public void Replace(ShellPackage pkg, bool dirty)
    {
        _started = true;
        _pkg     = pkg;
        _dirty   = dirty;
        _path    = pkg.Entries.Count > 0 ? [0] : null;
        Rebuilt();
        Loaded?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    private void Rebuilt()
    {
        _notify.Cancel();
        _problems = ShellCheck.Validate(_pkg);
        int errors = _problems.Count(p => p.Severity == ShellSeverity.Error);
        Status = $"{_pkg.Entries.Count} entr{(_pkg.Entries.Count == 1 ? "y" : "ies")}; " + (_problems.Count == 0 ? "nothing wrong" : $"{errors} error(s), {_problems.Count - errors} warning(s)");
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseProblemsChanged();
        RaiseActionsChanged();
    }

    // ---- editing -------------------------------------------------------------------------------

    private void Touched(bool now)
    {
        _dirty = true;
        RaiseValueChanged();
        if (now) Rebuilt();
        else _notify.Poke();
    }

    public void EditPackage(Action<ShellPackage> change)
    {
        change(_pkg);
        Touched(true);
    }

    /// <summary>Changes the selected entry; the package is looked at again a moment after the last change, so typing goes on without a stop at each letter.</summary>
    public void EditEntry(Action<ShellEntry> change, bool now = false)
    {
        if (Selected is not { } e) return;

        change(e);
        Touched(now);
    }

    public void Select(int[]? path)
    {
        _path = path is not null && Find(_pkg, path) is not null ? path : null;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Structure(int[]? path)
    {
        _path = path is not null && Find(_pkg, path) is not null ? path : null;
        Touched(true);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private string NewName(string stem)
    {
        var used = _pkg.Walk().Select(w => w.Entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 1;; n++)
            if (!used.Contains(stem + n))
                return stem + n;
    }

    /// <summary>Adds an entry after the selected one; a verb goes inside the selected submenu.</summary>
    public void AddEntry(ShellEntryKind kind)
    {
        var entry = ShellTemplates.NewEntry(kind, NewName(kind switch
                                                        {
                                                            ShellEntryKind.Protocol => "scheme",
                                                            ShellEntryKind.FileType => "type",
                                                            ShellEntryKind.SendTo   => "Item",
                                                            _                       => "Entry"
                                                        }));
        if (kind == ShellEntryKind.Verb && _path is { Length: > 0 } && Selected is { Kind: ShellEntryKind.Submenu } sub && _path.Length == 1)
        {
            entry.Target = null;
            sub.Children.Add(entry);
            Structure([_path[0], sub.Children.Count - 1]);
            return;
        }

        if (kind == ShellEntryKind.Verb && _path is { Length: 2 })
        {
            entry.Target = null;
            var parent = _pkg.Entries[_path[0]];
            parent.Children.Insert(_path[1] + 1, entry);
            Structure([_path[0], _path[1] + 1]);
            return;
        }

        int at = _path is { Length: > 0 } ? _path[0] + 1 : _pkg.Entries.Count;
        _pkg.Entries.Insert(at, entry);
        Structure([at]);
    }

    public void Delete()
    {
        if (_path is null || ListOf(_path) is not { } list) return;

        int i = _path[^1];
        list.RemoveAt(i);
        var next = list.Count == 0 ? (_path.Length > 1 ? [_path[0]] : null) : _path.Take(_path.Length - 1).Append(Math.Min(i, list.Count - 1)).ToArray();
        Structure(next);
    }

    public void Move(int delta)
    {
        if (_path is null || ListOf(_path) is not { } list) return;

        int i = _path[^1], j = i + delta;
        if (j < 0 || j >= list.Count) return;

        (list[i], list[j]) = (list[j], list[i]);
        Structure(_path.Take(_path.Length - 1).Append(j).ToArray());
    }

    public void Duplicate()
    {
        if (_path is null || Selected is not { } e || ListOf(_path) is not { } list) return;

        var copy = e.Clone();
        copy.Name = NewName(string.IsNullOrWhiteSpace(e.Name) ? "Entry" : e.Name.TrimEnd("0123456789".ToCharArray()));
        int at = _path[^1] + 1;
        list.Insert(at, copy);
        Structure(_path.Take(_path.Length - 1).Append(at).ToArray());
    }

    /// <summary>Takes a JSON text as the package (the Lab's source of truth). Returns what is wrong with it, or null.</summary>
    public string? ApplyJson(string text)
    {
        if (!ShellJson.FromJson(text, out var pkg, out var error)) return error;

        _pkg  = pkg;
        _path = _path is not null && Find(_pkg, _path) is not null ? _path : pkg.Entries.Count > 0 ? [0] : null;
        Touched(true);
        Loaded?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    // ---- saved, files ----------------------------------------------------------------------------

    private bool LetGo(string what)
    {
        return !_dirty || LabUi.Confirm($"This package has changes that are not kept in the Silo. {what} anyway?", "Shell Lab");
    }

    public void OpenSaved(string name)
    {
        if (Saved.Load(name) is not { } pkg) { Status = $"There is no package called {name} in the Silo."; return; }

        if (!LetGo("Open another")) return;

        if (string.IsNullOrWhiteSpace(pkg.Name)) pkg.Name = name;
        Replace(pkg, false);
        RememberRecent(name);
    }

    protected override bool RecentOnOpen => false;

    public override bool LibraryOpensWithWindow => true;

    public override Task OpenRecentAsync(string text)
    {
        OpenSaved(text);
        return Task.CompletedTask;
    }

    public override IReadOnlyList<LabLibrarySection> LibrarySections(string filter)
    {
        bool Wanted(string text) => filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var kept = Saved.Names().Where(Wanted)
                        .Select(name => new LabLibraryEntry(name, "Package", ShellInstaller.Find(name) is not null ? "installed" : null, "Puzzle", () => OpenSaved(name), name == _pkg.Name,
                                                            ToolTip: "Double-click to open", Delete: () => Saved.Delete(name)))
                        .ToList();
        var templates = ShellTemplates.All.Where(t => Wanted(t.Title))
                                      .Select(t => new LabLibraryEntry(t.Title, "Template", null, "TbNew", () => StartFrom(t.Make()), false, true, "Start a new package from this"))
                                      .ToList();
        return [new("In the Silo", "Database5", kept, Note: Saved.IsAvailable ? null : "No Silo is open"), new("Templates", "TbNew", templates, true)];
    }

    protected override IEnumerable<LabProblem> Validate()
    {
        foreach (var p in _problems)
        {
            var path = p.Path;
            yield return new LabProblem(p.Severity == ShellSeverity.Error ? LabProblemSeverity.Error : LabProblemSeverity.Warning, "Shell package", p.Text,
                                        path.Length > 0 ? $"Entry {p.Where}" : null, path.Length > 0 ? () => Select(path) : null);
        }
    }

    public void StartFrom(ShellPackage pkg)
    {
        if (!LetGo("Start another")) return;

        Replace(pkg, true);
    }

    private void NewPackage()
    {
        if (!LetGo("Start another")) return;

        Replace(new ShellPackage(), false);
    }

    private void SavePackage()
    {
        string? name = _pkg.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = LabUi.Ask("Save package", "What is the package called? (It is also the name of the record it leaves when installed.)", "MyPackage");
            if (string.IsNullOrWhiteSpace(name)) return;

            _pkg.Name = name.Trim();
        }

        try
        {
            Saved.Save(_pkg.Name, _pkg);
            _dirty = false;
            Status = $"Kept {_pkg.Name} in the Silo";
            Changed?.Invoke(this, EventArgs.Empty);
            RaiseValueChanged();
            RaiseActionsChanged();
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
    }

    private void DeleteSaved()
    {
        if (string.IsNullOrWhiteSpace(_pkg.Name)) return;

        Status = Saved.Delete(_pkg.Name) ? $"Removed {_pkg.Name} from the Silo" : $"{_pkg.Name} was not kept";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void PasteJson()
    {
        string? text = null;
        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // the clipboard is in use by another program
        }

        if (string.IsNullOrWhiteSpace(text)) { Status = "There is no text on the clipboard."; return; }

        if (!LetGo("Replace it with the JSON on the clipboard")) return;

        Status = ApplyJson(text) is { } error ? "That is not a Shell package: " + error : "The package is now the JSON from the clipboard.";
    }

    private void OpenFile()
    {
        var dialog = new OpenFileDialog { Title = "Open a Shell package", Filter = "Shell package (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true || !LetGo("Open another")) return;

        try
        {
            if (!ShellJson.FromJson(File.ReadAllText(dialog.FileName), out var pkg, out var error)) { Status = "That is not a Shell package: " + error; return; }

            if (string.IsNullOrWhiteSpace(pkg.Name)) pkg.Name = Path.GetFileNameWithoutExtension(dialog.FileName);
            Replace(pkg, true);
        }
        catch (IOException ex)
        {
            Status = ex.Message;
        }
    }

    private void SaveFile(string title, string name, string filter, string text)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = name, Filter = filter };
        if (dialog.ShowDialog() != true) return;

        File.WriteAllText(dialog.FileName, text, new UTF8Encoding(true));
        Status = $"Saved {Path.GetFileName(dialog.FileName)}";
    }

    private string FileStem()
    {
        string name = string.IsNullOrWhiteSpace(_pkg.Name) ? "ShellPackage" : _pkg.Name;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    // ---- installing --------------------------------------------------------------------------------

    public InstalledShellPackage? InstalledNow => string.IsNullOrWhiteSpace(_pkg.Name) ? null : ShellInstaller.Find(_pkg.Name);

    private void InstallPackage()
    {
        if (_problems.Any(p => p.Severity == ShellSeverity.Error)) { Status = "Mend the errors first (see Problems)."; return; }

        int writes = ShellCompiler.Compile(_pkg).Count;
        if (!LabUi.Confirm($"Install {_pkg.Name} {_pkg.Version} for {(_pkg.Scope == ShellScope.AllUsers ? "all users" : "the current user")}?\n\nIt makes {writes} registry and file changes, which are recorded so the package can be uninstalled." +
                           (InstalledNow is not null ? "\n\nThe installed version is uninstalled first." : string.Empty), "Install Shell package"))
            return;

        try
        {
            var done = ShellInstaller.Install(_pkg.Clone(), "Shell Lab");
            Status = $"Installed {done.Name}: {done.Writes.Count} writes recorded";
        }
        catch (ShellPackageException ex)
        {
            Status = ex.Message.Split('\n')[0];
            PromptDialog.Show("Install Shell package", ex.Message, ["OK"]);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        RaiseActionsChanged();
    }

    private void UninstallPackage()
    {
        if (InstalledNow is null) return;

        if (!LabUi.Confirm($"Uninstall {_pkg.Name}? Everything it wrote is taken away, and what it replaced is put back.", "Uninstall Shell package")) return;

        try
        {
            var result = ShellInstaller.Uninstall(_pkg.Name);
            Status = result.Complete ? $"Uninstalled {_pkg.Name}: {result.Removed} writes undone" + (result.Left.Count > 0 ? $", {result.Left.Count} key(s) left" : string.Empty)
                         : $"{_pkg.Name} is partly uninstalled: {result.Failed.Count} write(s) could not be undone";
        }
        catch (ShellPackageException ex)
        {
            Status = ex.Message;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        RaiseActionsChanged();
    }

    // ---- actions -------------------------------------------------------------------------------------

    public override IEnumerable<ProvidedCommand> GlobalActions
    {
        get
        {
            yield return ProvidedCommand.From("new-package", "_New Package", NewPackage, "TbNew", null, "Start with an empty package");
            yield return ProvidedCommand.From("open-file", "_Open Package File...", OpenFile, "TbOpen", null, "Open a Shell package from a JSON file");
            yield return ProvidedCommand.From("paste-json", "_Paste Package JSON", PasteJson, "TbPaste", null, "Take the JSON on the clipboard as the package");
            yield return ProvidedCommand.From("manage", "_Manage Packages...", () => ShellPackagesWindow.ShowFor(Host?.Window), "Puzzle", null, "The Shell packages installed on this system");
        }
    }

    public override IEnumerable<ProvidedCommand> ContextActions
    {
        get
        {
            yield return ProvidedCommand.From("add-verb", "Add _Verb", () => AddEntry(ShellEntryKind.Verb), "TbNew", null, "Add a right-click action (inside the selected submenu if one is selected)");
            yield return ProvidedCommand.From("add-submenu", "Add _Submenu", () => AddEntry(ShellEntryKind.Submenu), null, null, "Add a cascading submenu to the right-click menu");
            yield return ProvidedCommand.From("add-protocol", "Add _Protocol", () => AddEntry(ShellEntryKind.Protocol), null, null, "Add a protocol handler");
            yield return ProvidedCommand.From("add-filetype", "Add File _Type", () => AddEntry(ShellEntryKind.FileType), null, null, "Add a file type (ProgID) and its extensions");
            yield return ProvidedCommand.From("duplicate", "D_uplicate", Duplicate, "TbCopy", () => Selected is not null, "Copy the selected entry after itself");
            yield return ProvidedCommand.From("up", "Move _Up", () => Move(-1), null, () => Selected is not null, "Move the entry up") with { Gesture = "Alt+Up" };
            yield return ProvidedCommand.From("down", "Move _Down", () => Move(1), null, () => Selected is not null, "Move the entry down") with { Gesture = "Alt+Down" };
            yield return ProvidedCommand.From("delete-entry", "_Delete Entry", Delete, "TbDelete", () => Selected is not null, "Take the entry out of the package");
        }
    }

    public override IEnumerable<ProvidedCommand> ValueActions
    {
        get
        {
            yield return ProvidedCommand.From("save", "_Save Package", SavePackage, "TbSave", () => Saved.IsAvailable, "Keep the package in the Silo") with { Gesture = "Ctrl+S" };
            yield return ProvidedCommand.From("delete", "Delete Saved Pac_kage", DeleteSaved, "TbDelete", () => Saved.IsAvailable && !string.IsNullOrWhiteSpace(_pkg.Name) && Saved.Exists(_pkg.Name), "Remove the package from the Silo");
            yield return ProvidedCommand.From("install", "_Install", InstallPackage, "Plug", () => _problems.All(p => p.Severity != ShellSeverity.Error), "Install the package on this system");
            yield return ProvidedCommand.From("uninstall", "_Uninstall", UninstallPackage, "TbDelete", () => InstalledNow is not null, "Take the installed package away");
            yield return ProvidedCommand.From("export-json", "Export _JSON...", () => SaveFile("Save the package", FileStem() + ".json", "Shell package (*.json)|*.json", Json), "TbExport", null, "Save the package as a file other computers can install from");
            yield return ProvidedCommand.From("export-reg", "Export _.reg...", () => SaveFile("Save the registry values", FileStem() + ".reg", "Registration entries (*.reg)|*.reg", RegText), "TbExport", () => _problems.All(p => p.Severity != ShellSeverity.Error), "Save the registry values the package writes as a .reg file (no record is kept for uninstalling)");
        }
    }

    // ---- terminal ------------------------------------------------------------------------------------

    private static Task<string?> Done(string? text) => Task.FromResult(text);

    public override IReadOnlyList<LabTerminalCommand> TerminalCommands =>
    [
        new("tree", "tree                    the package as an outline, each entry with its address (2.1 is the first verb of the second entry)", _ => Done(Outline())),
        new("select", "select <address>        select an entry", call => Done(SelectCommand(call.Rest))),
        new("check", "check                   what is wrong with the package", _ => Done(CheckCommand())),
        new("json", "json                    the package as JSON", _ => Done(Json)),
        new("reg", "reg                     the registry values the package writes", _ => Done(RegText)),
        new("save", "save [name]             keep the package in the Silo", call => Done(SaveCommand(call.Rest))),
        new("list", "list                    the packages kept in the Silo", _ => Done(Saved.IsAvailable ? string.Join('\n', Saved.Names()) : "No Silo is open.")),
        new("open", "open <name>             open a package kept in the Silo", call => Done(OpenCommand(call.Rest))),
        new("installed", "installed               the Shell packages installed on this system", _ => Done(InstalledList())),
        new("install", "install                 install the package", _ => Done(Do(InstallPackage))),
        new("uninstall", "uninstall               uninstall the package", _ => Done(Do(UninstallPackage)))
    ];

    private string Do(Action action)
    {
        action();
        return Status;
    }

    private string Outline()
    {
        if (_pkg.Entries.Count == 0) return "The package is empty.";

        var sb = new StringBuilder();
        for (int i = 0; i < _pkg.Entries.Count; i++)
        {
            var e = _pkg.Entries[i];
            sb.Append(i + 1).Append("  ").Append(e.Kind).Append(' ').Append(e.Display).Append(e.Enabled ? string.Empty : " (off)").Append('\n');
            for (int c = 0; c < e.Children.Count; c++) sb.Append("   ").Append(i + 1).Append('.').Append(c + 1).Append("  ").Append(e.Children[c].Display).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private string SelectCommand(string rest)
    {
        var path = rest.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => int.TryParse(p, out int n) ? n - 1 : -1).ToArray();
        if (path.Length is 0 or > 2 || Find(_pkg, path) is null) throw new InvalidOperationException("There is no entry at that address. Type tree to see them.");

        Select(path);
        return "Selected " + Selected!.Display;
    }

    private string CheckCommand() => _problems.Count == 0 ? "Nothing is wrong with the package." : string.Join('\n', _problems.Select(p => $"{(p.Severity == ShellSeverity.Error ? "error  " : "warning")} {p.Where,-8} {p.Text}"));

    private string SaveCommand(string rest)
    {
        if (!string.IsNullOrWhiteSpace(rest)) _pkg.Name = rest.Trim();
        if (string.IsNullOrWhiteSpace(_pkg.Name)) throw new InvalidOperationException("Give it a name: save <name>.");

        Saved.Save(_pkg.Name, _pkg);
        _dirty = false;
        RaiseValueChanged();
        return $"Kept {_pkg.Name} in the Silo.";
    }

    private string OpenCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) throw new InvalidOperationException("Open which? Type list to see them.");

        OpenSaved(rest.Trim());
        return Status;
    }

    private static string InstalledList()
    {
        var list = ShellInstaller.List();
        return list.Count == 0 ? "No Shell packages are installed." : string.Join('\n', list.Select(p => $"{p.Name,-24} {p.Version,-8} {p.InstalledUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {p.Status}"));
    }

    public override UIElement BuildMainView() => new ShellView(this);
}
