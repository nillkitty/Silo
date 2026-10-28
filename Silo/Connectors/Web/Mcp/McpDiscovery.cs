using System.Runtime.CompilerServices;
using System.Text.Json;
using Telefrag.DI;

namespace Silo.Connectors.Web.Mcp;

/// <summary>
///     Represents a configured / discovered MCP Server
/// </summary>
public sealed record McpServer : IDestination
{
    public          List<string>               Args    { get; init; } = [];
    public required string                     Command { get; init; }
    public          Dictionary<string, string> Env     { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string Name { get; }
    public Uri    Uri  { get; set; }

    public McpServer(string name)
    {
        Name = name.Required();
        Uri  = new Uri("mcp:" + Name.UrlEncode());
    }
}

[Singleton(typeof(IDiscovery<McpServer>))]
public class McpDiscovery : IDiscovery<McpServer>
{
    public string        DisplayName => "Discover Configured MCP Servers";
    public DiscoveryType Type        => DiscoveryType.Quick;

    public async IAsyncEnumerable<Discovered<McpServer>> DiscoverAsync([EnumeratorCancellation] CancellationToken cancel = default)
    {
        var seenConfigurations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (clientName, filePath, _) in McpClientConfigs.Locations())
        {
            cancel.ThrowIfCancellationRequested();

            if (!global::System.IO.File.Exists(filePath) || !seenConfigurations.Add(Path.GetFullPath(filePath)))
                continue;

            JsonDocument doc;
            try
            {
                await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancel).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                continue;
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("mcpServers", out var serversElement) &&
                    !doc.RootElement.TryGetProperty("servers",    out serversElement))
                    continue;

                if (serversElement.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var serverProp in serversElement.EnumerateObject())
                {
                    cancel.ThrowIfCancellationRequested();

                    var serverObj = serverProp.Value;
                    if (serverObj.ValueKind != JsonValueKind.Object)
                        continue;

                    // Stdio command definition
                    if (!serverObj.TryGetProperty("command", out var cmdProp) || cmdProp.ValueKind != JsonValueKind.String)
                        continue;

                    string? command = cmdProp.GetString();
                    if (string.IsNullOrWhiteSpace(command))
                        continue;

                    // Resolve server name: check explicit config properties first, fallback to property key
                    string serverName = serverProp.Name;
                    if ((serverObj.TryGetProperty("name",  out var nameProp) || serverObj.TryGetProperty("displayName", out nameProp) ||
                         serverObj.TryGetProperty("title", out nameProp)) && nameProp.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(nameProp.GetString()))
                        serverName = nameProp.GetString()!;

                    var args = new List<string>();
                    if (serverObj.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Array)
                        foreach (var arg in argsProp.EnumerateArray())
                            if (arg.ValueKind == JsonValueKind.String)
                                args.Add(arg.GetString()!);

                    var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (serverObj.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
                        foreach (var envVar in envProp.EnumerateObject())
                            if (envVar.Value.ValueKind == JsonValueKind.String)
                                env[envVar.Name] = envVar.Value.GetString()!;

                    var server = new McpServer(serverName)
                                 {
                                     Command = command,
                                     Args    = args,
                                     Env     = env
                                 };

                    yield return new Discovered<McpServer>(this, new Uri(filePath), server);
                }
            }
        }
    }
}