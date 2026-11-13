using System.Collections.Concurrent;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Silo.Certificates;
using Silo.Model;
using Silo.Providers.Crypto;
using Silo.Providers.Network;
using Silo.Servers.Dns;
using Silo.Servers.Smtp;
using Silo.Tabular;

namespace Silo.Servers;

/// <summary>The server services there are: the registered ones, and the ones built in (so tests and early callers see them too).</summary>
public static class ServerServices
{
    public static IReadOnlyList<IServerService> All
    {
        get
        {
            var list  = new List<IServerService>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Registered().Concat(BuiltIn()))
                if (names.Add(s.Name))
                    list.Add(s);
            return list;
        }
    }

    public static IServerService? Find(string name) => All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<IServerService> Registered()
    {
        try
        {
            return App.Resolve<IEnumerable<IServerService>>()?.ToList() ?? [];
        }
        catch (Exception)
        {
            return []; // no container yet (a test): the built-in ones are used
        }
    }

    private static IEnumerable<IServerService> BuiltIn()
    {
        yield return new DnsStub();
        yield return new SmtpStub();
        yield return new TcpRelay();
    }
}

/// <summary>
///     The servers running in this process, whichever Silo they came from. A server stops when it is stopped, when its Silo
///     closes, or when the app exits.
/// </summary>
public sealed class ServerHost
{
    private const int MaxLog = 500;

    private readonly ConcurrentDictionary<string, (ServerRunner Runner, OpenSilo Silo)> _running = new();
    private readonly ConcurrentDictionary<string, string>                               _errors  = new();
    private readonly ConcurrentQueue<string>                                            _log     = new();

    public static ServerHost Instance { get; } = new();

    private ServerHost()
    {
        if (App.Instance is { } app) app.SiloClosing += (_, silo) => _ = StopAllAsync(silo);
    }

    /// <summary>A server started or stopped, or failed to start.</summary>
    public event EventHandler? Changed;

    /// <summary>A line was written to the log (raised on whatever thread wrote it).</summary>
    public event Action<string>? Logged;

    public IReadOnlyList<string> RecentLog => _log.ToList();

    public bool IsRunning(string id) => _running.ContainsKey(id);

    /// <summary>Why the server last failed to start; null when it did not.</summary>
    public string? ErrorOf(string id) => _errors.GetValueOrDefault(id);

    public ServerRunner? RunnerOf(string id) => _running.TryGetValue(id, out var r) ? r.Runner : null;

    public void Log(string line)
    {
        line = $"{DateTime.Now:HH:mm:ss}  {line}";
        _log.Enqueue(line);
        while (_log.Count > MaxLog) _log.TryDequeue(out _);
        Logged?.Invoke(line);
    }

    /// <summary>Starts the server (restarting it if it runs). False, with the reason in <see cref="ErrorOf" /> and the log, when it cannot.</summary>
    public async Task<bool> StartAsync(OpenSilo silo, ServerConfig config)
    {
        await StopAsync(config.Id);
        _errors.TryRemove(config.Id, out _);
        try
        {
            var service = ServerServices.Find(config.Service) ?? throw new InvalidOperationException($"There is no \"{config.Service}\" service.");
            if (silo.Data is not ITableStore store) throw new InvalidOperationException("This Silo cannot hold tables.");

            var certificate = CertificateOf(silo, config);
            var runner = await Task.Run(() => ServerRunner.Start(config.Clone(), service, new SiloServerTables(store), certificate, Log));
            _running[config.Id] = (runner, silo);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SocketException or System.Security.Cryptography.CryptographicException or SecretException or FormatException)
        {
            string why = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }
                             ? $"a port is already in use or may not be bound ({ex.Message})"
                             : ex.Message;
            _errors[config.Id] = why;
            Log($"{config.Name}: could not start: {why}");
            return false;
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task StopAsync(string id)
    {
        if (!_running.TryRemove(id, out var entry)) return;

        await entry.Runner.DisposeAsync();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops every server (of one Silo, when it is given).</summary>
    public async Task StopAllAsync(OpenSilo? silo = null)
    {
        foreach (var (id, entry) in _running.ToList())
            if (silo is null || ReferenceEquals(entry.Silo, silo))
                await StopAsync(id);
    }

    private static X509Certificate2? CertificateOf(OpenSilo silo, ServerConfig config)
    {
        if (string.IsNullOrEmpty(config.CertificateId)) return null;

        var vault = CertificateActions.OpenVault(silo) ?? throw new InvalidOperationException("This Silo cannot hold certificates.");
        return vault.OpenKeyPair(config.CertificateId) ?? throw new InvalidOperationException("The Silo does not hold the private key of the server's certificate.");
    }
}
