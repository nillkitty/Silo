using System.Windows.Controls;
using Silo.Connectors;

namespace Silo.Ui.Windows.Connecting;

/// <summary>A button next to a <see cref="UriFieldSetup" />'s box that fills it in (a folder or file browser, for instance).</summary>
/// <param name="Caption">The button's text.</param>
/// <param name="Pick">Asks the person for something; returns the text for the box, or null if they cancelled. Gets the owning window.</param>
public sealed record UriPicker(string Caption, Func<Window?, string?> Pick);

/// <summary>
///     The setup page of a connector that only needs a location: a box for it (with optional browse buttons) and a line of
///     examples. It is what a connector's <see cref="IConnectorUi" /> returns when a box is all it needs, and what the
///     Connectors tab falls back to for any connector that takes URIs but has no page of its own.
/// </summary>
public sealed class UriFieldSetup : IConnectorSetup
{
    private readonly IConnector             _connector;
    private readonly Func<string?, UriCheck> _check;
    private readonly TextBox                _box = new();
    private          UriCheck               _current = null!;

    /// <param name="connector">The connector the connection goes to.</param>
    /// <param name="check">Decides whether the text is something the connector will take (see <see cref="UriRules" />).</param>
    /// <param name="prompt">What the box is for ("Folder or file").</param>
    /// <param name="example">Examples of what to type.</param>
    /// <param name="initial">Text to start with.</param>
    /// <param name="pickers">Buttons that fill the box in.</param>
    public UriFieldSetup(IConnector connector, Func<string?, UriCheck> check, string prompt, string example, string initial = "",
                         IReadOnlyList<UriPicker>? pickers = null)
    {
        _connector = connector;
        _check     = check;

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 4) });

        var row = new DockPanel { LastChildFill = true };
        foreach (var picker in pickers ?? [])
        {
            var p = picker;
            var button = new Button { Content = p.Caption, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            DockPanel.SetDock(button, Dock.Right);
            button.Click += (_, _) =>
                            {
                                if (p.Pick(Window.GetWindow(panel)) is { Length: > 0 } text)
                                {
                                    _box.Text = text;
                                    _box.Focus();
                                }
                            };
            row.Children.Add(button);
        }

        _box.Padding = new Thickness(3);
        row.Children.Add(_box);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = example, Margin = new Thickness(0, 6, 0, 0), Opacity = 0.65, TextWrapping = TextWrapping.Wrap });

        View = panel;
        _box.TextChanged += (_, _) => Update();
        _box.Text        =  initial;
        Update();
    }

    public UIElement View { get; }

    public bool IsValid => _current.IsValid;

    public string? Problem => string.IsNullOrWhiteSpace(_box.Text) || _current.IsValid ? null : _current.Message;

    public string SuggestedName => _current.Uri is { } uri ? UriRules.NameFor(uri) : string.Empty;

    public event EventHandler? StateChanged;

    public ConnectionRequest BuildRequest()
    {
        var uri = _current.Uri ?? throw new InvalidOperationException("The setup is not valid: there is no URI to connect to.");
        return new ConnectionRequest(uri) { Connector = _connector, DisplayName = SuggestedName };
    }

    private void Update()
    {
        _current = _check(_box.Text);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
