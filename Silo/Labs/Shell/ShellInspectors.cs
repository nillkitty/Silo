using System.Windows.Controls;
using System.Windows.Input;
using Silo.Shell;

namespace Silo.Labs.Shell;

/// <summary>The package as JSON, which is what the package is made from: change it here and press Apply (or Ctrl+Enter) to change the package.</summary>
internal sealed class ShellJsonInspector(ShellLabSession session) : LiveInspector(session)
{
    private TextBox?   _text;
    private TextBlock? _error;

    public override string Id => "json";

    public override string Title => "JSON";

    public override string IconKey => "Coding";

    protected override UIElement Create()
    {
        _text = LabUi.Memo(string.Empty, _ => { });
        _text.KeyDown += (_, e) =>
                         {
                             if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) Apply();
                         };
        _error            = LabUi.Label(string.Empty, wrap: true);
        _error.Foreground = LabUi.Red;
        _error.Margin     = new Thickness(8, 0, 8, 4);
        var bar = LabUi.Bar(LabUi.Button("Apply", Apply, "Make the package of this JSON (Ctrl+Enter)"),
                            LabUi.Button("Revert", () =>
                                                   {
                                                       _text.Text  = session.Json;
                                                       _error.Text = string.Empty;
                                                   }, "Go back to the JSON of the package as it is"),
                            LabUi.Button("Copy", () => LabUi.Copy(session.Json), "Put the package's JSON on the clipboard"));
        var head = new StackPanel();
        head.Children.Add(bar);
        head.Children.Add(_error);
        return LabUi.Layout(head, _text);
    }

    private void Apply()
    {
        if (_text is null || _error is null) return;

        _error.Text = session.ApplyJson(_text.Text) ?? string.Empty;
    }

    protected override void Refresh()
    {
        // what is typed here is not overwritten while the box has the keyboard
        if (_text is null || _text.IsKeyboardFocusWithin) return;

        string json = session.Json;
        if (_text.Text != json)
        {
            _text.Text = json;
            if (_error is not null) _error.Text = string.Empty;
        }
    }
}

/// <summary>What installing the package writes, as a .reg file.</summary>
internal sealed class ShellRegistryInspector(ShellLabSession session) : LiveInspector(session)
{
    private TextBox? _text;

    public override string Id => "registry";

    public override string Title => "Registry";

    public override string IconKey => "Coding";

    protected override UIElement Create()
    {
        _text = LabUi.Output();
        var bar = LabUi.Bar(LabUi.Button("Copy", () => LabUi.Copy(session.RegText), "Put the .reg text on the clipboard"),
                            LabUi.Label("The values installing the package writes. Installing also records them, so uninstalling can undo them; a .reg file cannot.", muted: true));
        return LabUi.Layout(bar, _text);
    }

    protected override void Refresh()
    {
        if (_text is null) return;

        try
        {
            _text.Text = session.RegText;
        }
        catch (Exception ex)
        {
            _text.Text = "; " + ex.Message;
        }
    }
}

/// <summary>Whether the package is installed on this system, when, and everything it wrote.</summary>
internal sealed class ShellInstalledInspector(ShellLabSession session) : LiveInspector(session)
{
    private TextBox?   _text;
    private TextBlock? _head;

    public override string Id => "installed";

    public override string Title => "Installed";

    public override string IconKey => "Puzzle";

    public override bool VisibleByDefault => false;

    protected override UIElement Create()
    {
        _text = LabUi.Output();
        _head = LabUi.Label(string.Empty, wrap: true);
        _head.Margin = new Thickness(8, 4, 8, 4);
        var bar = new StackPanel();
        bar.Children.Add(LabUi.Bar(LabUi.Button("Refresh", Refresh, "Look at the registry again"), LabUi.Button("Manage Packages...", () => ShellPackagesWindow.ShowFor(Window.GetWindow(_text)), "All the Shell packages installed on this system")));
        bar.Children.Add(_head);
        return LabUi.Layout(bar, _text);
    }

    protected override void Refresh()
    {
        if (_text is null || _head is null) return;

        var installed = session.InstalledNow;
        if (installed is null)
        {
            _head.Text = string.IsNullOrWhiteSpace(session.Package.Name) ? "Name the package to see whether it is installed." : $"{session.Package.Name} is not installed.";
            _text.Text = string.Empty;
            return;
        }

        _head.Text = $"{installed.Name} {installed.Version} - {installed.Status}, installed {installed.InstalledUtc.ToLocalTime():yyyy-MM-dd HH:mm} for {(installed.Scope == ShellScope.AllUsers ? "all users" : "the current user")}.";
        _text.Text = string.Join("\r\n", installed.Writes.Select(w => w.ToString()));
    }
}
