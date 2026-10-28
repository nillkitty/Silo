using System.Text.Json;
using System.Text.Json.Nodes;
using Silo.Contracts.ConnectorServices;
using Silo.Providers;
using Silo.Ui.Windows.Connecting;
using Silo.ViewModel;
using Telefrag.DI;

namespace Silo.Connectors.Web.Mcp;

/// <summary>
///     Connects to MCP servers: the ones the AI tools on this computer are configured with (<c>mcp:name</c>, started as a
///     program and spoken to on its standard input and output; see <see cref="McpDiscovery" />) and ones at a web address
///     (<c>mcp+https://host/path</c>, streamable HTTP). What a server offers -- tools, resources and prompts -- is shown in
///     the Explorer.
/// </summary>
[Singleton(typeof(IConnector))]
public class McpConnector : ConnectorBase<McpItem, McpState>, IUriConnector, IAsyncConnector, IDestinationSource
{
    public const string HttpScheme  = "mcp+http";
    public const string HttpsScheme = "mcp+https";
    public const string StdioScheme = "mcp";

    public string DisplayName => "MCP";

    public string? Description => "MCP servers: the tools, resources and prompts they offer to AI assistants";

    public async Task<IConnection> CreateConnectionAsync(ConnectionRequest crq, CancellationToken cancel)
    {
        crq.Required();
        if (crq.Destination is McpServer known) return new McpConnection(this, known.Uri, known, null, crq);

        var uri = crq.RequestedUri ?? throw new DestinationRequiredException("The MCP connector requires a destination URI");
        if (TryGetWebAddress(uri, out var web)) return new McpConnection(this, uri, null, web, crq);

        if (!uri.Scheme.Equals(StdioScheme, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"The MCP connector cannot handle URI scheme '{uri.Scheme}'");

        var server = await FindServerAsync(uri, cancel) ??
                     throw new FileNotFoundException($"No MCP server for '{Uri.UnescapeDataString(uri.AbsolutePath)}' is configured in any AI tool on this computer.");
        return new McpConnection(this, uri, server, null, crq);
    }

    public IEnumerable<DestinationEntry> GetRegisteredDestinations()
    {
        // the discovery is quick (it reads a few files), but it is asynchronous: wait for it off this thread
        var found = Task.Run(async () =>
                             {
                                 var list = new List<McpServer>();
                                 foreach (var discovery in App.ResolveAll<IDiscovery<McpServer>>())
                                     await foreach (var item in discovery.DiscoverAsync(CancellationToken.None))
                                         list.Add(item.Component);
                                 return list;
                             }).GetAwaiter().GetResult();

        return found.DistinctBy(s => s.Uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new DestinationEntry(s.Name, s.Uri, $"{s.Command} {string.Join(' ', s.Args)}".Trim()));
    }

    public UriSchemeScope HandlesScheme(string scheme)
    {
        return scheme?.ToLowerInvariant() switch
               {
                   StdioScheme or HttpScheme or HttpsScheme => UriSchemeScope.Private,
                   _                                        => UriSchemeScope.Unknown
               };
    }

    public string? Validate(Uri uri)
    {
        if (TryGetWebAddress(uri, out var web))
            return web.Host.Length > 0 ? null : "An MCP server address needs a host name, such as https://example.com/mcp.";

        return uri.Scheme.Equals(StdioScheme, StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Trim('/').Length > 0
                   ? null
                   : "Name one of the configured MCP servers (mcp:name), or give a web address (mcp+https://example.com/mcp).";
    }

    protected override IConnection OnCreateConnection(ConnectionRequest crq)
    {
        // the dialog and the restore take the asynchronous way in; this is for code that has no async context
        return Task.Run(() => CreateConnectionAsync(crq, CancellationToken.None)).GetAwaiter().GetResult();
    }

    protected override IConnectorSetup OnCreateSetup(ConnectorSetupContext context)
    {
        return new UriFieldSetup(this, text => UriRules.Check(text, [this], UriRules.AnyScope), "MCP server",
                                 "Examples:  mcp:filesystem    mcp+https://example.com/mcp");
    }

    protected override McpState? OnGetStateOther(object context)
    {
        return context switch
               {
                   McpConnection c => c.State as McpState,
                   McpItem i       => i.Connection.State as McpState,
                   _               => null
               };
    }

    private static bool IsHttp(Uri uri)
    {
        return uri.Scheme.Equals(HttpScheme, StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals(HttpsScheme, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The web address a URI stands for: <c>mcp+https://host/path</c> and <c>mcp:https://host/path</c> (what the dialog makes of a
    ///     pasted address) are <c>https://host/path</c>; a plain http(s) address is itself.
    /// </summary>
    private static bool TryGetWebAddress(Uri uri, out Uri web)
    {
        string text = uri.OriginalString.Trim();
        if (text.StartsWith("mcp+", StringComparison.OrdinalIgnoreCase) || text.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase))
            text = text[4..];

        if (Uri.TryCreate(text, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            web = parsed;
            return true;
        }

        web = uri;
        return false;
    }

    private static async Task<McpServer?> FindServerAsync(Uri wanted, CancellationToken cancel)
    {
        foreach (var discovery in App.ResolveAll<IDiscovery<McpServer>>())
            await foreach (var item in discovery.DiscoverAsync(cancel))
                if (string.Equals(item.Component.Uri.AbsoluteUri, wanted.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                    return item.Component;

        return null;
    }
}

/// <summary>What the connection to an MCP server is doing.</summary>
public abstract record McpState(bool IsConnected, bool IsFailed, string DisplayName) : IDisplayName, IConnectionState
{
    public bool IsDisposed => false;

    /// <summary>The key of the state's icon (see <c>Resources/NavIcons.xaml</c>), from its <see cref="IconAttribute" />.</summary>
    public string IconKey => IconAttribute.KeyOf(GetType()) ?? "Plug";

    [Icon("StateDisconnected")]
    public record Disconnected() : McpState(false, false, nameof(Disconnected));

    [Icon("StateConnecting")]
    public record Connecting(Uri Target) : McpState(false, false, $"Connecting to {Target}");

    [Icon("StateConnected")]
    public record Connected(string Server) : McpState(true, false, $"Connected to {Server}");

    [Icon("StateFailed")]
    public record Failed(Exception Exception) : McpState(false, true, $"Failed: {Exception.Message}");
}

/// <summary>An MCP server as the Explorer shows it: the connection is its root, then <c>Tools</c>, <c>Resources</c> and <c>Prompts</c>, each with what the server lists.</summary>
public class McpConnection : WebConnection, INodeDetails, ICommandProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Uri?          _webAddress;
    private          McpSession?   _session;

    public McpConnection(McpConnector connector, Uri target, McpServer? server, Uri? webAddress, ConnectionRequest? crq)
        : base(connector, target, crq)
    {
        Server      = server;
        _webAddress = webAddress;
        DisplayText = crq?.DisplayName;
        State       = new McpState.Disconnected();
    }

    /// <summary>The program this connection starts; null for a server at a web address.</summary>
    public McpServer? Server { get; }

    /// <summary>The session with the server while connected.</summary>
    public McpSession? Session => _session;

    public IconRef? Icon => IconRef.FromKey("Server");

    public string Name => DisplayText is { Length: > 0 } text ? text : Server?.Name ?? _webAddress?.Host ?? "MCP server";

    public string TypeName => "MCP Server";

    Uri INodeDetails.Uri => Destination!.Uri;

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        yield return ("Type", "MCP server");
        yield return ("Transport", Server is not null ? "Standard input and output" : "Streamable HTTP");
        yield return ("Address", Destination!.Uri.ToString());
        if (Server is not null) yield return ("Command", $"{Server.Command} {string.Join(' ', Server.Args)}".Trim());
        yield return ("State", (State as McpState)?.DisplayName ?? "Not connected");

        if (_session?.Server is { } info)
        {
            yield return ("Server", $"{info.ServerInfo.Name} {info.ServerInfo.Version}");
            yield return ("Protocol", info.ProtocolVersion);
            yield return ("Offers", string.Join(", ", Capabilities(info)));
            if (!string.IsNullOrWhiteSpace(info.Instructions)) yield return ("Instructions", info.Instructions);
        }

        if (State is McpState.Failed failed) yield return ("Last error", failed.Exception.Message);
    }

    public IEnumerable<ProvidedCommand> GetCommands()
    {
        yield return ProvidedCommand.From("mcp.connect", "_Connect", ConnectFromUi, "Plug", () => _session is null, "Starts the server and asks it what it offers", "Connection");
        yield return ProvidedCommand.From("mcp.disconnect", "_Disconnect", DisconnectFromUi, "Power", () => _session is not null, "Ends the conversation with the server", "Connection");
    }

    private static IEnumerable<string> Capabilities(InitializeResult info)
    {
        if (info.Capabilities.Tools is not null) yield return "tools";
        if (info.Capabilities.Resources is not null) yield return "resources";
        if (info.Capabilities.Prompts is not null) yield return "prompts";
        if (info.Capabilities.Logging is not null) yield return "logging";
    }

    public override async Task<bool> Connect(CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            if (_session is not null) return true;

            State = new McpState.Connecting(Destination!.Uri);
            McpTransport transport = Server is not null
                                         ? new McpStdioTransport(Server)
                                         : new McpHttpTransport(Data ?? throw new InvalidOperationException("No HTTP client."), _webAddress!);
            var session = new McpSession(transport);
            try
            {
                await session.StartAsync(cancel);
            }
            catch (Exception ex)
            {
                await session.DisposeAsync();
                State = new McpState.Failed(ex);
                throw;
            }

            session.Closed += OnSessionClosed;
            _session       =  session;
            State          =  new McpState.Connected(session.Server!.ServerInfo.Name);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnSessionClosed(Exception? why)
    {
        _session = null;
        State    = why is null ? new McpState.Disconnected() : new McpState.Failed(why);
    }

    protected override async Task<bool> OnDisconnect()
    {
        await _gate.WaitAsync();
        try
        {
            if (_session is not { } session) return false;

            _session = null;
            State    = new McpState.Disconnected();
            await session.DisposeAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Connects (unless the connection is being restored in the background, which waits until the server is looked at) and puts the server in the Explorer.</summary>
    protected override async Task OnInitAsync(ConnectContext context)
    {
        bool       restoring = context.Reason == ConnectReason.BackgroundRestore;
        Exception? failure   = null;

        if (!restoring)
            try
            {
                await Connect();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

        if (Node is null)
        {
            Node = new McpNode(this, LoadGroupsAsync, restoring || failure is not null ? 0 : 1);
            context.NavigationManager.AddNode(Node, ConnectionRequest?.ParentNode);
        }

        if (failure is not null) global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // ---- what the Explorer shows -------------------------------------------------------------------

    private Uri ChildUri(string path)
    {
        string root = Destination!.Uri.AbsoluteUri;
        int    hash = root.IndexOf('#');
        return new Uri((hash < 0 ? root : root[..hash]) + "#" + path);
    }

    private async Task<IReadOnlyList<SiloNode>> LoadGroupsAsync(CancellationToken cancel)
    {
        await Connect(cancel);
        var caps = _session!.Server!.Capabilities;

        var groups = new List<SiloNode>();
        if (caps.Tools is not null)
            groups.Add(Group("Tools", "tools", async ct => (await _session!.ListToolsAsync(ct)).Select(ToolItem)));
        if (caps.Resources is not null)
            groups.Add(Group("Resources", "resources", async ct => (await _session!.ListResourcesAsync(ct)).Select(ResourceItem)));
        if (caps.Prompts is not null)
            groups.Add(Group("Prompts", "prompts", async ct => (await _session!.ListPromptsAsync(ct)).Select(PromptItem)));
        return groups;
    }

    private McpNode Group(string title, string path, Func<CancellationToken, Task<IEnumerable<McpItem>>> list)
    {
        var item = new McpItem(this, title, "MCP " + title, ChildUri(path), "Folder17", [("Contains", $"What the server lists as {path}")])
                   {
                       Loader = async ct => (await list(ct)).Select(i => (SiloNode)new McpNode(i, i.Loader, 0)).ToList()
                   };
        return new McpNode(item, item.Loader, 0);
    }

    private McpItem ToolItem(Tool tool)
    {
        var details = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(tool.Description)) details.Add(("Description", tool.Description));

        if (tool.InputSchema["properties"] is JsonObject props)
        {
            var required = (tool.InputSchema["required"] as JsonArray)?.Select(r => r?.GetValue<string>()).ToHashSet() ?? [];
            foreach (var (key, schema) in props)
                details.Add(("Argument " + key,
                             $"{schema?["type"]?.ToJsonString().Trim('"') ?? "any"}{(required.Contains(key) ? ", required" : string.Empty)}" +
                             (schema?["description"] is { } d ? $" - {d.GetValue<string>()}" : string.Empty)));
        }

        details.Add(("Input schema", tool.InputSchema.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        return new McpItem(this, tool.Name, "MCP Tool", ChildUri("tools/" + Uri.EscapeDataString(tool.Name)), "Plug", details);
    }

    private McpItem ResourceItem(Resource resource)
    {
        var details = new List<(string, string)> { ("Resource URI", resource.Uri) };
        if (!string.IsNullOrWhiteSpace(resource.Description)) details.Add(("Description", resource.Description));
        if (resource.MimeType is { Length: > 0 }) details.Add(("Content type", resource.MimeType));
        if (resource.Size is { } size) details.Add(("Size", $"{size:N0} bytes"));
        return new McpItem(this, resource.Name, "MCP Resource", ChildUri("resources/" + Uri.EscapeDataString(resource.Uri)), "Document", details);
    }

    private McpItem PromptItem(Prompt prompt)
    {
        var details = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(prompt.Description)) details.Add(("Description", prompt.Description));
        foreach (var arg in prompt.Arguments ?? [])
            details.Add(("Argument " + arg.Name, (arg.Required ? "required" : "optional") + (arg.Description is { } d ? " - " + d : string.Empty)));
        return new McpItem(this, prompt.Name, "MCP Prompt", ChildUri("prompts/" + Uri.EscapeDataString(prompt.Name)), "Document", details);
    }

    private async void ConnectFromUi()
    {
        try
        {
            await Connect();
            if (Node is { ChildLoader: not null, ChildrenLoaded: true } node) await node.ReloadChildrenAsync();
        }
        catch (OperationCanceledException)
        {
            // the person cancelled
        }
        catch (Exception ex)
        {
            App.Status($"Could not connect to {Name}: {ex.Message}");
        }
    }

    private async void DisconnectFromUi()
    {
        try
        {
            await Disconnect();
        }
        catch (Exception ex)
        {
            App.Status($"Could not disconnect from {Name}: {ex.Message}");
        }
    }
}

/// <summary>A tool, resource, prompt or group of them on an MCP server: what the Explorer's node for it describes.</summary>
public sealed class McpItem(McpConnection connection, string name, string typeName, Uri uri, string iconKey, IReadOnlyList<(string Name, string Value)> details)
    : INodeDetails
{
    public McpConnection Connection { get; } = connection;

    /// <summary>Reads what is below the item when it is expanded; null for a leaf.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<SiloNode>>>? Loader { get; init; }

    public IconRef? Icon     { get; } = IconRef.FromKey(iconKey);
    public string   Name     { get; } = name;
    public string   TypeName { get; } = typeName;
    public Uri      Uri      { get; } = uri;

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        return details;
    }
}

public record McpNode : SiloNode
{
    public McpNode(INodeDetails model, Func<CancellationToken, Task<IReadOnlyList<SiloNode>>>? loader, int? expand = null)
        : base(model, model.Name, model.Uri, model.Icon, expand)
    {
        TypeName    = model.TypeName;
        ChildLoader = loader;
    }
}
