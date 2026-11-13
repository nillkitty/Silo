using System.Text.RegularExpressions;
using Serilog;
using Silo.Providers.Network;
using Silo.Storage;

namespace Silo.Servers;

/// <summary>One server set up in a Silo: which service, where it listens, its settings, and the certificate it serves TLS with.</summary>
public sealed class ServerConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>What the person calls it; the service's name until they change it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The <see cref="IServerService.Name" /> of the service it runs.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Whether Start all starts it.</summary>
    public bool Enabled { get; set; } = true;

    public List<ServerEndpoint> Endpoints { get; set; } = [];

    /// <summary>The certificate vault id of the keypair TLS endpoints present; null for none.</summary>
    public string? CertificateId { get; set; }

    /// <summary>The settings that were set, by <see cref="ServerSetting.Key" />.</summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ServerConfig Clone()
    {
        var copy = (ServerConfig)MemberwiseClone();
        copy.Endpoints = [..Endpoints];
        copy.Settings  = new Dictionary<string, string>(Settings, StringComparer.OrdinalIgnoreCase);
        return copy;
    }

    /// <summary>A new server of the service, listening where the service would.</summary>
    public static ServerConfig For(IServerService service)
    {
        var config = new ServerConfig { Name = service.DisplayName, Service = service.Name, Endpoints = [..service.DefaultEndpoints] };
        foreach (var s in service.Settings.Where(s => s.Default is not null)) config.Settings[s.Key] = s.Default!;
        return config;
    }
}

/// <summary>
///     Endpoints written as text, the way the Servers panel shows them: <c>UDP:53; TCP:53</c>, <c>TCP:25,587+starttls</c>,
///     <c>TCP:465+tls</c>, <c>127.0.0.1/TCP:2525</c>. Ports of one protocol, address and TLS mode are written together.
/// </summary>
public static partial class EndpointText
{
    [GeneratedRegex(@"^(?:(?<addr>\[[0-9a-fA-F:.]+\]|[0-9a-fA-F:.]+)/)?(?<proto>tcp|udp):(?<ports>\d+(?:\s*,\s*\d+)*)(?:\s*\+\s*(?<tls>tls|starttls))?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Entry();

    public static string Format(IEnumerable<ServerEndpoint> endpoints)
    {
        return string.Join("; ", endpoints.GroupBy(e => (e.Protocol, Address: e.Address ?? "", e.Tls))
                                          .Select(g =>
                                                  {
                                                      var (protocol, address, tls) = g.Key;
                                                      string addr  = address.Length == 0 ? "" : (address.Contains(':') ? $"[{address}]" : address) + "/";
                                                      string ports = string.Join(",", g.Select(e => e.Port));
                                                      string mode = tls switch
                                                                    {
                                                                        ServerTls.Implicit => "+tls",
                                                                        ServerTls.StartTls => "+starttls",
                                                                        _                  => ""
                                                                    };
                                                      return $"{addr}{protocol.ToString().ToUpperInvariant()}:{ports}{mode}";
                                                  }));
    }

    /// <summary>The endpoints the text describes.</summary>
    /// <exception cref="FormatException">Part of it is not an endpoint (the message says which, and why).</exception>
    public static List<ServerEndpoint> Parse(string text)
    {
        var list = new List<ServerEndpoint>();
        foreach (var raw in text.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Entry().Match(raw);
            if (!m.Success) throw new FormatException($"\"{raw}\" is not an endpoint; write it like TCP:25, UDP:53, TCP:465+tls or 127.0.0.1/TCP:587+starttls.");

            var protocol = m.Groups["proto"].Value.Equals("udp", StringComparison.OrdinalIgnoreCase) ? PortProtocol.Udp : PortProtocol.Tcp;
            var tls = m.Groups["tls"].Value.ToLowerInvariant() switch
                      {
                          "tls"      => ServerTls.Implicit,
                          "starttls" => ServerTls.StartTls,
                          _          => ServerTls.None
                      };
            if (protocol == PortProtocol.Udp && tls != ServerTls.None) throw new FormatException($"\"{raw}\": TLS is for TCP only.");

            string? address = m.Groups["addr"].Success ? m.Groups["addr"].Value.Trim('[', ']') : null;
            if (address is not null && !System.Net.IPAddress.TryParse(address, out _)) throw new FormatException($"\"{address}\" is not an IP address.");

            foreach (var p in m.Groups["ports"].Value.Split(',', StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(p, out int port) || port is < 1 or > 65535) throw new FormatException($"{p} is not a port (1 to 65535).");
                list.Add(new ServerEndpoint(protocol, port, tls, address));
            }
        }

        return list;
    }
}

/// <summary>Keeps a Silo's servers in it, as one stored item shared by everyone who opens it.</summary>
public static class ServerStore
{
    public const string Key = "servers/list";

    public static event EventHandler? Changed;

    public static List<ServerConfig> Load(IStoredStore? store)
    {
        if (store?.GetStored(Key, StoredScope.Shared) is not { } row) return [];

        try
        {
            return StoredCodec.Deserialize<List<ServerConfig>>(row.Data) ?? [];
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            Log.Warning(ex, "The Silo's servers could not be read");
            return [];
        }
    }

    public static void Save(IStoredStore store, IEnumerable<ServerConfig> servers)
    {
        store.SetStored(Key, StoredCodec.TypeNameOf(typeof(List<ServerConfig>)), StoredCodec.Json, StoredCodec.Serialize(servers.ToList()), StoredScope.Shared);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
