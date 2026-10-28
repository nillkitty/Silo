using System.Windows.Controls;
using Silo.Tools;
using Telefrag.DI;

namespace Silo.Connectors.Web.Mcp;

/// <summary>
///     Tools > MCP Server: turns Silo's own MCP server on and off, says where it is, and lists the AI tools found on this
///     computer with a button to add Silo's server to each. The first time the server is turned on it offers to add itself
///     to the ones that do not have it.
/// </summary>
[Singleton(typeof(ITool))]
public sealed class McpServerTool : ToolBase
{
    public McpServerTool() : base("mcp-server")
    {
        DisplayName = "MCP Server";
        GroupName   = "Connections";
        Icon        = IconRef.FromKey("Server");
    }

    protected override UIElement OnBuildContent()
    {
        return new McpServerView();
    }
}

internal sealed class McpServerView : UserControl
{
    private readonly StackPanel _clients = new();
    private readonly TextBox    _endpoint = new() { IsReadOnly = true, Margin = new Thickness(0, 4, 0, 8) };
    private readonly TextBlock  _status = new() { FontWeight = FontWeights.SemiBold };
    private readonly Button     _toggle = new() { Padding = new Thickness(12, 3, 12, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };

    public McpServerView()
    {
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(_status);
        root.Children.Add(_endpoint);
        root.Children.Add(new TextBlock
                          {
                              TextWrapping = TextWrapping.Wrap,
                              Text = "Lets an AI assistant look at the Silos you have open and what is in their connections (read only). " +
                                     "It listens on this computer only and wants a secret token that the assistants below are given through Silo itself."
                          });
        root.Children.Add(_toggle);
        root.Children.Add(new TextBlock { Text = "AI tools on this computer", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
        root.Children.Add(_clients);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };

        _toggle.Click += (_, _) => Toggle();
        McpHost.Current.StateChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Refresh));
        Refresh();
    }

    private void Toggle()
    {
        var host = McpHost.Current;
        if (host.IsRunning)
        {
            host.Stop();
            return;
        }

        if (host.Start()) OfferLinks();
    }

    /// <summary>The first time the server is on: asks whether to add it to the AI tools found that do not list it. Asked once for each tool.</summary>
    private void OfferLinks()
    {
        var settings = McpServerSettings.Load();
        var open = McpLlmLinks.Detect().Where(l => !l.IsLinked && !settings.Offered.Contains(l.Client)).ToList();
        if (open.Count == 0) return;

        settings.Offered.AddRange(open.Select(l => l.Client));
        settings.Save();

        string names = string.Join(Environment.NewLine, open.Select(l => "  " + l.Client));
        var answer = MessageBox.Show(Window.GetWindow(this), $"These AI tools are installed here. Add Silo's MCP server to them?{Environment.NewLine}{Environment.NewLine}{names}" +
                                                             $"{Environment.NewLine}{Environment.NewLine}Their configuration files are changed (the first version of each is kept next to it as .silo-backup).",
                                     "MCP Server", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            foreach (var link in open)
                Link(link);

        Refresh();
    }

    private void Link(LlmClientLink link)
    {
        try
        {
            McpLlmLinks.Link(link);
            App.Status($"Added Silo's MCP server to {link.Client}. Restart it to use it.");
        }
        catch (Exception ex)
        {
            App.Alert(ex, $"Silo's MCP server could not be added to {link.Client}.");
        }
    }

    private void Refresh()
    {
        var host = McpHost.Current;
        _status.Text      = host.IsRunning ? "Running" : host.LastError ?? "Off";
        _endpoint.Text    = host.IsRunning ? host.Endpoint.ToString() : string.Empty;
        _endpoint.Visibility = host.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        _toggle.Content   = host.IsRunning ? "Turn Off" : "Turn On";

        _clients.Children.Clear();
        var found = McpLlmLinks.Detect();
        if (found.Count == 0) _clients.Children.Add(new TextBlock { Text = "None found (Claude Desktop, Claude Code, Cursor, VS Code, Windsurf)." });

        foreach (var link in found)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            if (link.IsLinked)
            {
                var added = new TextBlock { Text = "Added", Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(added, Dock.Right);
                row.Children.Add(added);
            }
            else
            {
                var add = new Button { Content = "Add Silo", Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(add, Dock.Right);
                add.Click += (_, _) =>
                             {
                                 Link(link);
                                 Refresh();
                             };
                row.Children.Add(add);
            }

            row.Children.Add(new TextBlock { Text = link.Client, VerticalAlignment = VerticalAlignment.Center });
            _clients.Children.Add(row);
        }
    }
}
