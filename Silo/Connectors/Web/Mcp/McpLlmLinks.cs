using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Silo.Connectors.Web.Mcp;

/// <summary>How an AI tool takes its list of MCP servers.</summary>
public enum McpClientKind
{
    /// <summary>A JSON file with a <c>mcpServers</c> (or <c>servers</c>) object.</summary>
    JsonFile,

    /// <summary>Claude Code, which has a command for it (<c>claude mcp add</c>) and a config file that is not to be edited by others.</summary>
    ClaudeCode,

    /// <summary>Found, but with a way of listing servers Silo does not write.</summary>
    ReadOnly
}

/// <summary>Where an AI tool keeps its MCP servers.</summary>
public sealed record McpClientLocation(string Client, string FilePath, McpClientKind Kind = McpClientKind.JsonFile);

/// <summary>The AI tools whose MCP configuration Silo knows (to find the servers they use, and to add its own).</summary>
public static class McpClientConfigs
{
    public static IReadOnlyList<McpClientLocation> Locations()
    {
        string appData     = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return
        [
            new("Claude Desktop", Path.Combine(appData, "Claude", "claude_desktop_config.json")),
            new("Claude Desktop (macOS)", Path.Combine(userProfile, "Library", "Application Support", "Claude", "claude_desktop_config.json")),
            new("Claude Code", Path.Combine(userProfile, ".claude.json"), McpClientKind.ClaudeCode),
            new("Cursor", Path.Combine(userProfile, ".cursor", "mcp.json")),
            new("Cursor (AppData)", Path.Combine(appData, "Cursor", "mcp.json")),
            new("VS Code", Path.Combine(appData, "Code", "User", "globalStorage", "github.copilot-chat", "mcp.json")),
            new("VS Code (macOS)", Path.Combine(userProfile, "Library", "Application Support", "Code", "User", "globalStorage", "github.copilot-chat", "mcp.json")),
            new("Windsurf", Path.Combine(userProfile, ".codeium", "windsurf", "mcp_config.json")),
            new("User MCP Profile", Path.Combine(userProfile, ".mcp", "config.json")),
            new("Zed Editor", Path.Combine(appData, "Zed", "settings.json"), McpClientKind.ReadOnly)
        ];
    }
}

/// <summary>An AI tool the person uses, and whether it already lists Silo's MCP server.</summary>
public sealed record LlmClientLink(McpClientLocation Location, bool IsLinked)
{
    public string Client => Location.Client;
}

/// <summary>Finding the AI tools on this computer and adding Silo's MCP server to their configuration.</summary>
public static class McpLlmLinks
{
    /// <summary>The name Silo's server has in the AI tools' configuration.</summary>
    public const string ServerName = "silo";

    /// <summary>The AI tools that are in use here (their configuration exists) and can be given Silo's server.</summary>
    public static IReadOnlyList<LlmClientLink> Detect()
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<LlmClientLink>();

        foreach (var location in McpClientConfigs.Locations())
        {
            if (location.Kind == McpClientKind.ReadOnly) continue;

            bool exists = global::System.IO.File.Exists(location.FilePath);
            bool usesIt = exists || (location.Kind == McpClientKind.ClaudeCode && McpProcess.Find("claude") is not null);
            if (!usesIt || !seen.Add(Path.GetFullPath(location.FilePath))) continue;

            result.Add(new LlmClientLink(location, exists && IsListed(location.FilePath)));
        }

        return result;
    }

    /// <summary>The command an AI tool runs to reach Silo's server: Silo's console launcher in its <c>mcp</c> mode, which relays to the running app.</summary>
    public static (string Command, string[] Args) BridgeCommand()
    {
        string self    = Environment.ProcessPath ?? "silo.exe";
        string console = Path.ChangeExtension(self, ".com");
        return global::System.IO.File.Exists(console) ? (console, ["mcp"]) : (self, ["--cli", "mcp"]);
    }

    public static void Link(LlmClientLink link)
    {
        var (command, args) = BridgeCommand();
        if (link.Location.Kind == McpClientKind.ClaudeCode) LinkClaudeCode(command, args);
        else LinkJsonFile(link.Location.FilePath, command, args);
    }

    private static bool IsListed(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc    = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (string key in new[] { "mcpServers", "servers" })
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var servers) &&
                    servers.ValueKind == JsonValueKind.Object && servers.TryGetProperty(ServerName, out _))
                    return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            /* unreadable: offered, and the attempt says why not */
        }

        return false;
    }

    private static void LinkJsonFile(string path, string command, string[] args)
    {
        JsonObject root;
        if (global::System.IO.File.Exists(path) && new FileInfo(path).Length > 0)
        {
            string text = global::System.IO.File.ReadAllText(path);
            root = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new InvalidDataException($"{path} is not a JSON object.");

            string backup = path + ".silo-backup";
            if (!global::System.IO.File.Exists(backup)) global::System.IO.File.WriteAllText(backup, text); // the file is rewritten whole: the first version is kept
        }
        else
        {
            root = new JsonObject();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        // VS Code calls the list "servers" and wants a type; the others call it "mcpServers"
        bool   vsCode = root["servers"] is JsonObject && root["mcpServers"] is null;
        string key    = vsCode ? "servers" : "mcpServers";
        var    list   = root[key] as JsonObject ?? (JsonObject)(root[key] = new JsonObject())!;

        var entry = new JsonObject { ["command"] = command, ["args"] = new JsonArray(args.Select(a => (JsonNode?)a).ToArray()) };
        if (vsCode) entry["type"] = "stdio";
        list[ServerName] = entry;

        string temp = path + ".silo-tmp";
        global::System.IO.File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        global::System.IO.File.Move(temp, path, true);
    }

    private static void LinkClaudeCode(string command, string[] args)
    {
        string claude = McpProcess.Find("claude") ?? throw new FileNotFoundException("The 'claude' command was not found on the PATH.");
        var    info   = McpProcess.CreateStartInfo(claude, ["mcp", "add", "--scope", "user", ServerName, "--", command, ..args]);
        info.RedirectStandardInput = false;
        info.StandardInputEncoding = null;

        using var process = Process.Start(info) ?? throw new InvalidOperationException("'claude' could not be started.");
        var       errors  = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(true);
            throw new TimeoutException("'claude mcp add' did not finish.");
        }

        if (process.ExitCode != 0) throw new InvalidOperationException($"'claude mcp add' failed: {errors.GetAwaiter().GetResult().Trim()}");
    }
}

/// <summary>What the MCP server remembers between runs: whether it is on, where, and the secret that lets a program use it. Kept in the user's local application data.</summary>
public sealed class McpServerSettings
{
    public const int DefaultPort = 47820;

    /// <summary>Whether the server starts with Silo.</summary>
    public bool Enabled { get; set; }

    /// <summary>The AI tools that were already asked about adding Silo's server.</summary>
    public List<string> Offered { get; set; } = [];

    public int Port { get; set; } = DefaultPort;

    /// <summary>Sent as a bearer token with every request: a web page in the browser can reach 127.0.0.1 too, a program that cannot read this file cannot use the server.</summary>
    public string Token { get; set; } = string.Empty;

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Silo", "mcp-server.json");

    public static McpServerSettings Load()
    {
        McpServerSettings? settings = null;
        try
        {
            if (global::System.IO.File.Exists(FilePath)) settings = JsonSerializer.Deserialize<McpServerSettings>(global::System.IO.File.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            /* start again with new settings */
        }

        settings ??= new McpServerSettings();
        if (settings.Token.Length == 0)
        {
            settings.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            settings.Save();
        }

        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        global::System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
