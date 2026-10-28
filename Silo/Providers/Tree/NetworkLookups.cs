using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace Silo.Providers.Tree;

/// <summary>
///     The questions a network node can put to the outside world: who the registries say holds a block (RDAP, WHOIS), what
///     the reverse DNS says, and the addresses of the web pages that answer the same.
/// </summary>
public static class NetworkLookups
{
    private const string RdapFallback = "https://rdap.org/";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    ///     Asks the RDAP service that is responsible for the block (found in IANA's RDAP bootstrap file, with rdap.org --
    ///     which
    ///     redirects to the right one -- as the fallback) and returns its answer, formatted.
    /// </summary>
    public static async Task<(Uri Url, string Json)> RdapAsync(IPSubnet subnet, CancellationToken cancel = default)
    {
        string baseUrl                      = await RdapBaseAsync(subnet, cancel) ?? RdapFallback;
        if (!baseUrl.EndsWith('/')) baseUrl += '/';

        string query                                            = subnet.Network.ToString();
        if (subnet.PrefixLength < subnet.MaxPrefixLength) query += "/" + subnet.PrefixLength;

        var       url     = new Uri($"{baseUrl}ip/{query}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/rdap+json");
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await Http.SendAsync(request, cancel);
        string    body     = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{url.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}");

        return (response.RequestMessage?.RequestUri ?? url, Indent(body));
    }

    /// <summary>
    ///     Asks whois.iana.org who holds the block, then the registry it refers to, and returns both answers as text (the
    ///     second is the one with the details).
    /// </summary>
    public static async Task<string> WhoisAsync(IPSubnet subnet, CancellationToken cancel = default)
    {
        string query = subnet.Network.ToString();
        string first = await QueryWhoisAsync("whois.iana.org", query, cancel);

        string? refer = null;
        foreach (string line in first.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("refer:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("whois:", StringComparison.OrdinalIgnoreCase))
            {
                refer = trimmed[(trimmed.IndexOf(':') + 1)..].Trim();
                if (refer.Length > 0) break;
            }
        }

        var sb = new StringBuilder();
        sb.Append("# whois.iana.org: ").AppendLine(query).AppendLine(first.TrimEnd());
        if (!string.IsNullOrEmpty(refer) && !refer.Equals("whois.iana.org", StringComparison.OrdinalIgnoreCase))
        {
            // ARIN wants "n" for a network lookup; the others take the bare address
            string asked = refer.Contains("arin.net", StringComparison.OrdinalIgnoreCase) ? "n + " + query : query;
            sb.AppendLine().Append('#').Append(' ').Append(refer).Append(": ").AppendLine(asked);
            try
            {
                sb.AppendLine((await QueryWhoisAsync(refer, asked, cancel)).TrimEnd());
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
            {
                sb.AppendLine($"# {refer} could not be asked: {ex.Message}");
            }
        }

        return sb.ToString();
    }

    /// <summary>Asks rdap.org, which redirects to the registry that holds the domain, and returns its answer, formatted.</summary>
    public static async Task<(Uri Url, string Json)> RdapDomainAsync(string name, CancellationToken cancel = default)
    {
        var url = new Uri($"{RdapFallback}domain/{name.TrimEnd('.')}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/rdap+json");
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await Http.SendAsync(request, cancel);
        string    body     = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{response.RequestMessage?.RequestUri?.Host ?? url.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}");

        return (response.RequestMessage?.RequestUri ?? url, Indent(body));
    }

    /// <summary>The host names the reverse DNS gives an address; throws a <see cref="SocketException" /> when there is none.</summary>
    public static async Task<string[]> ReverseLookupAsync(IPAddress address, CancellationToken cancel = default)
    {
        var entry = await Dns.GetHostEntryAsync(address).WaitAsync(cancel);
        return entry.Aliases.Length == 0 ? [entry.HostName] : [entry.HostName, .. entry.Aliases];
    }

    /// <summary>
    ///     Runs <c>nslookup -type=NS|SOA zone</c> and returns what it printed (the system resolver answers, so this sees
    ///     the same DNS the rest of the computer does).
    /// </summary>
    public static async Task<string> NsLookupAsync(string zone, string type, CancellationToken cancel = default)
    {
        var info = new ProcessStartInfo("nslookup", $"-type={type} {zone}")
                   {
                       RedirectStandardOutput = true,
                       RedirectStandardError  = true,
                       UseShellExecute        = false,
                       CreateNoWindow         = true
                   };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("nslookup could not be started");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error  = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // it ended in the meantime
            }

            throw new TimeoutException("nslookup did not answer in time");
        }

        return (await output + await error).Trim();
    }

    /// <summary>The web pages that tell about a block, by name.</summary>
    public static IReadOnlyList<(string Name, string Group, Uri Url)> Links(IPSubnet subnet)
    {
        string cidr = subnet.Cidr();
        string host = subnet.Network.ToString();
        bool   one  = subnet.PrefixLength == subnet.MaxPrefixLength;
        string kind = subnet.IsV6() ? "ipv6" : "ipv4";

        return
        [
            ("IANA _Address Space Registry", "Registry",
             new Uri($"https://www.iana.org/assignments/{kind}-address-space/{kind}-address-space.xhtml")),
            ("IANA _Special-Purpose Registry", "Registry",
             new Uri($"https://www.iana.org/assignments/iana-{kind}-special-registry/iana-{kind}-special-registry.xhtml")),
            ("_ARIN Search", "Registry", new Uri("https://search.arin.net/rdap/?query=" + Uri.EscapeDataString(one ? host : cidr))),
            ("RIPE_stat", "Registry", new Uri("https://stat.ripe.net/"                  + (one ? host : cidr))),
            ("BGP _Toolkit (Hurricane Electric)", "Registry",
             new Uri(one ? $"https://bgp.he.net/ip/{host}" : $"https://bgp.he.net/net/{cidr}"))
        ];
    }

    private static async Task<string?> RdapBaseAsync(IPSubnet subnet, CancellationToken cancel)
    {
        try
        {
            string  file = subnet.IsV6() ? "ipv6.json" : "ipv4.json";
            string? text = await AddressRegistry.GetCachedTextAsync("https://data.iana.org/rdap/" + file, "rdap-" + file, false, cancel);
            if (text is null) return null;

            using var document = JsonDocument.Parse(text);
            string?   best     = null;
            int       bestBits = -1;
            foreach (var service in document.RootElement.GetProperty("services").EnumerateArray())
            {
                var urls = service[1].EnumerateArray().Select(u => u.GetString()).Where(u => u is not null).ToList();
                if (urls.Count == 0) continue;

                foreach (var entry in service[0].EnumerateArray())
                    if (SubnetMath.TryParse(entry.GetString(), out var block) && block.Covers(subnet) && block.PrefixLength > bestBits)
                    {
                        bestBits = block.PrefixLength;
                        best     = urls.FirstOrDefault(u => u!.StartsWith("https", StringComparison.OrdinalIgnoreCase)) ?? urls[0];
                    }
            }

            return best;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static async Task<string> QueryWhoisAsync(string server, string query, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(server, 43, timeout.Token);
            await using var stream = client.GetStream();

            byte[] bytes = Encoding.ASCII.GetBytes(query + "\r\n");
            await stream.WriteAsync(bytes, timeout.Token);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"{server} did not answer in time");
        }
    }

    private static string Indent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Silo");
        return client;
    }
}