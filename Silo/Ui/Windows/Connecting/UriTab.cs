using System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;
using Silo.Connectors;
using Silo.Settings;

namespace Silo.Ui.Windows.Connecting;

/// <summary>
///     The URI tab: type (or pick from the list of ones used before) a URI. Connect is available when some connector takes
///     it: a private scheme of Silo's own (<c>reg:</c>, <c>hkcu:</c>...) or a standard one a connector has claimed
///     (<c>file:</c>, <c>https:</c>) -- see <see cref="UriRules" />. A Windows path is accepted as a <c>file:</c> URI.
/// </summary>
public sealed class UriTab : ConnectTab
{
    private readonly ConnectionHistory           _history;
    private readonly IReadOnlyList<IUriConnector> _connectors;
    private readonly ComboBox                    _box;

    public UriTab(IReadOnlyList<IUriConnector> connectors, ConnectionHistory history)
    {
        _connectors = connectors;
        _history    = history;

        _box = new ComboBox { IsEditable = true, ItemsSource = history.Uris, Padding = new Thickness(3), MinWidth = 320 };
        _box.AddHandler(Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Update()));

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "URI to connect to", Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(_box);
        panel.Children.Add(new TextBlock
                           {
                               Text = "Examples:\n" +
                                      "    reg:///hkey_current_user/software          (a registry key)\n" +
                                      "    C:\\Data    \\\\server\\share    file:///C:/Data          (folders and files)\n" +
                                      "    https://example.com/data          (a web address)\n\n" +
                                      "Silo's own schemes (such as reg: and hkcu:) and standard ones that a connector handles are accepted. " +
                                      "URIs you have connected with are in the drop-down.",
                               Margin       = new Thickness(0, 10, 0, 0),
                               Opacity      = 0.65,
                               TextWrapping = TextWrapping.Wrap
                           });
        View = panel;

        Update();
    }

    public override string Header => "URI";

    public override UIElement View { get; }

    public override void FocusInput()
    {
        _box.Focus();
    }

    public override void Connected(ConnectionTarget target)
    {
        _history.RecordUri(_box.Text);
    }

    private void Update()
    {
        string text  = _box.Text;
        var    check = UriRules.Check(text, _connectors);

        if (string.IsNullOrWhiteSpace(text))
        {
            Set(null, "Enter a URI to connect to.");
            return;
        }

        if (!check.IsValid || check.Uri is not { } uri || check.Connector is not { } connector)
        {
            Set(null, check.Message, true);
            return;
        }

        string name = UriRules.NameFor(uri);
        Set(new ConnectionTarget(name, uri, cancel => ConnectionCreation.CreateAsync(connector, new ConnectionRequest(uri) { Connector = connector, DisplayName = name }, cancel)),
            check.Message);
    }
}
