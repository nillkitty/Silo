using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using Serilog;
using Silo.Labs;
using Silo.Labs.Shell;
using Silo.Ui.Windows;

namespace Silo.Shell;

/// <summary>
///     The Tools &gt; Shell Packages window: the Shell packages installed on this system (from the Silo's record in the registry), uninstalling
///     them, and installing one from a JSON file or from the Silo.
/// </summary>
public sealed class ShellPackagesWindow : Window
{
    private static ShellPackagesWindow? _open;

    private readonly ListView  _list = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Button    _uninstall = Make("_Uninstall");
    private readonly Button    _details = Make("_Details...");
    private readonly Button    _edit = Make("Open in Shell _Lab");

    private InstalledShellPackage? Selected => _list.SelectedItem as InstalledShellPackage;

    /// <summary>Shows the window (the one that is open is brought forward).</summary>
    public static void ShowFor(Window? owner)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }

        _open = new ShellPackagesWindow();
        if (owner is { IsVisible: true }) _open.Owner = owner;
        else _open.WindowStartupLocation              = WindowStartupLocation.CenterScreen;

        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    private static Button Make(string text) => new() { Content = text, Margin = new Thickness(0, 0, 0, 6), MinWidth = 150 };

    private void Reload()
    {
        try
        {
            _list.ItemsSource = ShellInstaller.List();
            _status.Text      = ((IReadOnlyList<InstalledShellPackage>)_list.ItemsSource).Count == 0 ? "No Shell packages are installed." : string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing the installed Shell packages failed");
            _status.Text = "Could not read the record of installed packages: " + ex.Message;
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _uninstall.IsEnabled = _details.IsEnabled = Selected is not null;
        _edit.IsEnabled      = Selected?.Package is not null;
    }

    // ---- install ---------------------------------------------------------------------------------

    private void InstallFromFile()
    {
        var dialog = new OpenFileDialog { Title = "Install a Shell package", Filter = "Shell package (*.json)|*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            if (!ShellJson.FromJson(File.ReadAllText(dialog.FileName), out var package, out var error))
            {
                _status.Text = "That is not a Shell package: " + error;
                return;
            }

            if (string.IsNullOrWhiteSpace(package.Name)) package.Name = Path.GetFileNameWithoutExtension(dialog.FileName);
            Install(package, dialog.FileName);
        }
        catch (IOException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void InstallFromSilo(FrameworkElement anchor)
    {
        var kept = ShellLabSession.Kept;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (string name in kept.Names())
        {
            string n    = name;
            var    item = new MenuItem { Header = n.Replace("_", "__") };
            item.Click += (_, _) =>
                          {
                              if (kept.Load(n) is { } package)
                              {
                                  if (string.IsNullOrWhiteSpace(package.Name)) package.Name = n;
                                  Install(package, "Silo: " + n);
                              }
                          };
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = kept.IsAvailable ? "(no packages kept in the Silo)" : "(no Silo is open)", IsEnabled = false });
        menu.IsOpen = true;
    }

    private void Install(ShellPackage package, string source)
    {
        var problems = ShellCheck.Validate(package);
        if (problems.Any(p => p.Severity == ShellSeverity.Error))
        {
            PromptDialog.Show("Install Shell package", "The package cannot be installed:\n" + string.Join('\n', problems.Where(p => p.Severity == ShellSeverity.Error).Select(p => "  " + p.Text)), ["OK"]);
            return;
        }

        int writes = ShellCompiler.Compile(package).Count;
        string summary = $"Install {package.Name} {package.Version} for {(package.Scope == ShellScope.AllUsers ? "all users" : "the current user")}?\n\n" +
                         (string.IsNullOrWhiteSpace(package.Description) ? string.Empty : package.Description + "\n\n") +
                         $"{package.Entries.Count(e => e.Enabled)} entries, {writes} registry and file changes, recorded so the package can be uninstalled." +
                         (problems.Count > 0 ? "\n\nWarnings:\n" + string.Join('\n', problems.Select(p => "  " + p.Text)) : string.Empty) +
                         (ShellInstaller.Find(package.Name) is not null ? "\n\nThe installed version is uninstalled first." : string.Empty);
        if (PromptDialog.Show("Install Shell package", summary, ["Install", "Cancel"]).Button != 0) return;

        try
        {
            var done = ShellInstaller.Install(package, source);
            _status.Text = $"Installed {done.Name}: {done.Writes.Count} writes recorded.";
        }
        catch (ShellPackageException ex)
        {
            _status.Text = ex.Message;
        }

        Reload();
    }

    // ---- uninstall, details ----------------------------------------------------------------------

    private void UninstallSelected()
    {
        if (Selected is not { } p) return;

        if (PromptDialog.Show("Uninstall Shell package", $"Uninstall {p.Name}?\n\nEverything it wrote ({p.Writes.Count} changes) is taken away, and what it replaced is put back.", ["Uninstall", "Cancel"]).Button != 0) return;

        try
        {
            var result = ShellInstaller.Uninstall(p.Name);
            _status.Text = result.Complete
                               ? $"Uninstalled {p.Name}: {result.Removed} changes undone." + (result.Left.Count > 0 ? "\nLeft in place:\n  " + string.Join("\n  ", result.Left) : string.Empty)
                               : $"{p.Name} is only partly uninstalled; these could not be undone (it can be tried again):\n  " + string.Join("\n  ", result.Failed);
        }
        catch (ShellPackageException ex)
        {
            _status.Text = ex.Message;
        }

        Reload();
    }

    private void ShowDetails()
    {
        if (Selected is not { } p) return;

        string text = $"{p.Name} {p.Version}\n{p.Status}; installed {p.InstalledUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} for {(p.Scope == ShellScope.AllUsers ? "all users" : "the current user")}" +
                      (string.IsNullOrWhiteSpace(p.Source) ? string.Empty : $"\nfrom {p.Source}") + "\n\nWritten:\n" + string.Join("\n", p.Writes.Select(w => "  " + w));
        var box = new TextBox { Text = text, IsReadOnly = true, FontFamily = LabUi.Mono, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(8) };
        box.Themed();
        new Window { Title = "Shell package " + p.Name, Width = 820, Height = 480, Owner = this, Content = box, ShowInTaskbar = false }.Also(w =>
                                                                                                                                              {
                                                                                                                                                  w.SetResourceReference(BackgroundProperty, "Theme.Background");
                                                                                                                                                  w.Show();
                                                                                                                                              });
    }

    private void OpenInLab()
    {
        if (Selected?.Package is { } package) LabHost.Open(LabHost.Labs.First(l => l.Id == "shell"), package);
    }

    // ---- layout ----------------------------------------------------------------------------------

    private ShellPackagesWindow()
    {
        Title                 = "Shell Packages";
        Width                 = 820;
        Height                = 480;
        MinWidth              = 600;
        MinHeight             = 320;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Theme.Background");
        SetResourceReference(ForegroundProperty, "Theme.Text");

        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Name", Width = 190, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(InstalledShellPackage.Name)) });
        grid.Columns.Add(new GridViewColumn { Header = "Version", Width = 70, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(InstalledShellPackage.Version)) });
        grid.Columns.Add(new GridViewColumn { Header = "For", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(InstalledShellPackage.Scope)) });
        grid.Columns.Add(new GridViewColumn { Header = "Installed", Width = 140, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(InstalledShellPackage.InstalledUtc)) { StringFormat = "yyyy-MM-dd HH:mm" } });
        grid.Columns.Add(new GridViewColumn { Header = "Status", Width = 100, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(InstalledShellPackage.Status)) });
        _list.View = grid;
        _list.SetResourceReference(BackgroundProperty, "Theme.Surface");
        _list.SetResourceReference(ForegroundProperty, "Theme.Text");
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, _) => ShowDetails();

        var install = Make("Install from _File...");
        install.Click += (_, _) => InstallFromFile();
        var fromSilo = Make("Install from _Silo...");
        fromSilo.Click += (_, _) => InstallFromSilo(fromSilo);
        var refresh = Make("_Refresh");
        refresh.Click += (_, _) => Reload();
        var newPackage = Make("_New in Shell Lab");
        newPackage.Click += (_, _) => LabHost.Open(LabHost.Labs.First(l => l.Id == "shell"));
        var close = Make("Close");
        close.IsCancel =  true;
        close.Click    += (_, _) => Close();
        _uninstall.Click += (_, _) => UninstallSelected();
        _details.Click   += (_, _) => ShowDetails();
        _edit.Click      += (_, _) => OpenInLab();

        var side = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        foreach (var b in new[] { install, fromSilo, _uninstall, _details, _edit, newPackage, refresh }) side.Children.Add(b);
        DockPanel.SetDock(side, Dock.Right);

        var root = new DockPanel { Margin = new Thickness(14) };
        var intro = new TextBlock { Text = "Shell packages set up Windows shell extensions: protocol handlers, file types, right-click actions, Send To and New items. What a package writes is recorded, so uninstalling it takes away exactly that.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);
        DockPanel.SetDock(close, Dock.Bottom);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.MinWidth            = 80;
        root.Children.Add(close);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(side);
        root.Children.Add(_list);
        Content = root;

        Reload();
    }
}
