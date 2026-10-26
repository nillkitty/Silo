using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Silo.Tools.Traceroute;

/// <summary>
///     An asynchronous traceroute: ICMP echo requests with a growing TTL, each router on the way answering "TTL expired" until the
///     destination answers itself. Several hops are probed at once (<see cref="TraceOptions.HopsInFlight" />), every address is
///     looked up in DNS as soon as it is seen, and every change is reported as it happens (<see cref="TraceUpdate" />).
/// </summary>
/// <remarks>
///     <para>
///         <c>report</c> is called from worker threads, one call at a time and in order, while an internal lock is held: keep it
///         quick (hand the update to the UI thread, e.g. with <c>Dispatcher.BeginInvoke</c>) and do not call back into the engine.
///     </para>
///     <para>
///         Hops probed in parallel can lie beyond the destination; once the destination answers they are cancelled and reported
///         as <see cref="TraceUpdate.HopRemoved" />. <see cref="RunAsync" /> never throws for network problems: a trace that cannot
///         start ends with <see cref="TraceOutcome.Failed" />.
///     </para>
/// </remarks>
public sealed class TraceRoute
{
    private readonly IPinger       _pinger;
    private readonly IHostResolver _resolver;

    /// <summary>A trace over the given seams; the defaults use <see cref="Ping" /> and <see cref="Dns" />.</summary>
    public TraceRoute(IPinger? pinger = null, IHostResolver? resolver = null)
    {
        _pinger   = pinger ?? new SystemPinger();
        _resolver = resolver ?? new SystemHostResolver();
    }

    /// <summary>Traces the route to <paramref name="destination" /> (a host name, an IPv4/IPv6 address or a URL).</summary>
    /// <param name="destination">Where to trace to.</param>
    /// <param name="options">How (null = defaults).</param>
    /// <param name="report">Receives every update, the last one always <see cref="TraceUpdate.Finished" />.</param>
    /// <param name="cancel">Stops the trace; it then ends with <see cref="TraceOutcome.Cancelled" />.</param>
    public Task<TraceResult> RunAsync(string destination, TraceOptions? options, Action<TraceUpdate> report, CancellationToken cancel = default)
    {
        var run = new Run(_pinger, _resolver, (options ?? new TraceOptions()).Normalized(), report.Required(), cancel);
        return run.ExecuteAsync(destination ?? "");
    }

    /// <summary>The host part of what the person typed: a URL's host, an address without IPv6 brackets, or the text itself.</summary>
    public static string HostOf(string destination)
    {
        string text = destination.Trim();
        if (text.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            text = uri.Host;

        if (text.Length > 2 && text[0] == '[' && text[^1] == ']') text = text[1..^1];
        return text;
    }

    /// <summary>The address to trace to among <paramref name="addresses" /> for the wanted family; null when there is none.</summary>
    public static IPAddress? Pick(IEnumerable<IPAddress> addresses, TraceAddressFamily family)
    {
        var list = addresses.ToList();
        IPAddress? v4 = list.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        IPAddress? v6 = list.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
        return family switch
               {
                   TraceAddressFamily.IPv4 => v4,
                   TraceAddressFamily.IPv6 => v6,
                   _                       => v4 ?? v6
               };
    }

    // ---- one run --------------------------------------------------------------------------------

    private sealed class Hop(int number, CancellationTokenSource cancel)
    {
        public int                     Number    { get; } = number;
        public CancellationTokenSource Cancel    { get; } = cancel;
        public List<TraceProbe>        Probes    { get; } = [];
        public List<IPAddress>         Addresses { get; } = [];
        public bool                    Probing   { get; set; } = true;
        public bool                    Removed   { get; set; }
    }

    private sealed class Run(IPinger pinger, IHostResolver resolver, TraceOptions options, Action<TraceUpdate> report, CancellationToken cancel)
    {
        private readonly List<CancellationTokenSource>      _allCancels = [];
        private readonly object                             _gate       = new();
        private readonly Dictionary<int, Hop>               _hops       = [];
        private readonly Dictionary<IPAddress, string?>     _names      = [];
        private readonly List<Task>                         _resolves   = [];
        private readonly Stopwatch                          _clock      = new();

        private int        _endAt = int.MaxValue; // the lowest hop that ended the trace (destination or unreachable)
        private IPAddress? _target;

        public async Task<TraceResult> ExecuteAsync(string destination)
        {
            _clock.Start();

            TraceTarget target;
            try
            {
                target = await ResolveTargetAsync(destination).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return Finish(new TraceResult(TraceOutcome.Cancelled, 0, _clock.Elapsed));
            }
            catch (Exception ex)
            {
                return Finish(new TraceResult(TraceOutcome.Failed, 0, _clock.Elapsed, Describe(ex, destination)));
            }

            _target = target.Address;
            lock (_gate) report(new TraceUpdate.Started(target));

            var probing = new List<Task>();
            using (var window = new SemaphoreSlim(options.HopsInFlight))
            {
                try
                {
                    for (int ttl = 1; ttl <= options.MaxHops; ttl++)
                    {
                        await window.WaitAsync(cancel).ConfigureAwait(false);

                        Hop hop;
                        lock (_gate)
                        {
                            if (ttl > _endAt)
                            {
                                window.Release();
                                break;
                            }

                            hop = Begin(ttl);
                        }

                        probing.Add(ProbeAndReleaseAsync(hop, window));
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    // stopped while waiting for a free slot: the hops already out finish (cancelled) below
                }

                await Task.WhenAll(probing).ConfigureAwait(false);
            }

            Task[] resolving;
            lock (_gate) resolving = _resolves.ToArray();
            await Task.WhenAll(resolving).ConfigureAwait(false);

            lock (_gate)
            {
                foreach (var c in _allCancels) c.Dispose();
                _allCancels.Clear();
            }

            return Finish(Outcome());
        }

        private async Task<TraceTarget> ResolveTargetAsync(string destination)
        {
            string host = HostOf(destination);
            if (host.Length == 0) throw new ArgumentException("Enter a host name or an address to trace the route to.");

            IPAddress address;
            string?   hostName = null;
            if (IPAddress.TryParse(host, out var typed))
            {
                address = typed;
            }
            else
            {
                var all = await resolver.ResolveAsync(host, cancel).ConfigureAwait(false);
                address = Pick(all, options.Family) ??
                          throw new InvalidOperationException(options.Family == TraceAddressFamily.Any
                                                                  ? $"{host} has no address."
                                                                  : $"{host} has no {options.Family} address.");
                hostName = host;
            }

            IPAddress? local = null;
            try
            {
                local = resolver.LocalAddressFor(address);
            }
            catch (Exception)
            {
                // the local end is only shown, not needed
            }

            string localName;
            try
            {
                localName = resolver.LocalHostName;
            }
            catch (Exception)
            {
                localName = "This computer";
            }

            return new TraceTarget(destination.Trim(), address, hostName, localName, local, options);
        }

        /// <summary>Under the lock: registers hop <paramref name="ttl" /> and reports it (pending).</summary>
        private Hop Begin(int ttl)
        {
            var hopCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            _allCancels.Add(hopCancel);

            var hop = new Hop(ttl, hopCancel);
            _hops[ttl] = hop;
            report(new TraceUpdate.HopChanged(Snapshot(hop)));
            return hop;
        }

        private async Task ProbeAndReleaseAsync(Hop hop, SemaphoreSlim window)
        {
            try
            {
                await ProbeAsync(hop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // the run was stopped, or the hop lies beyond the destination
            }
            finally
            {
                window.Release();
                lock (_gate)
                {
                    hop.Probing = false;
                    if (!hop.Removed) report(new TraceUpdate.HopChanged(Snapshot(hop)));
                }
            }
        }

        private async Task ProbeAsync(Hop hop)
        {
            var token = hop.Cancel.Token;
            for (int i = 0; i < options.ProbesPerHop; i++)
            {
                token.ThrowIfCancellationRequested();

                PingProbeReply reply;
                try
                {
                    reply = await pinger.PingAsync(_target!, hop.Number, options.Timeout, options.PayloadSize, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    reply = new PingProbeReply(IPStatus.Unknown, null, null, ex.Message);
                }

                List<CancellationTokenSource>? cut = null;
                lock (_gate)
                {
                    if (hop.Removed) return;

                    var probe = new TraceProbe(i, reply.Status, reply.From, reply.From is null ? null : reply.RttMs);
                    hop.Probes.Add(probe);

                    if (reply.From is { } from && !hop.Addresses.Contains(from))
                    {
                        hop.Addresses.Add(from);
                        StartResolve(from);
                    }

                    bool ends = reply.Status == IPStatus.Success || TraceStatus.IsUnreachable(reply.Status);
                    if (ends && hop.Number < _endAt)
                    {
                        _endAt = hop.Number;
                        cut    = Cut(hop.Number);
                    }

                    report(new TraceUpdate.HopChanged(Snapshot(hop)));
                }

                // cancelling runs callbacks: never under the lock
                if (cut is not null)
                    foreach (var c in cut)
                        c.Cancel();

                if (i < options.ProbesPerHop - 1 && options.ProbeInterval > TimeSpan.Zero)
                    await Task.Delay(options.ProbeInterval, token).ConfigureAwait(false);
            }
        }

        /// <summary>Under the lock: drops every hop beyond <paramref name="last" />; returns their cancellations to trigger.</summary>
        private List<CancellationTokenSource> Cut(int last)
        {
            var cancels = new List<CancellationTokenSource>();
            foreach (var hop in _hops.Values.Where(h => h.Number > last).OrderBy(h => h.Number).ToList())
            {
                hop.Removed = true;
                _hops.Remove(hop.Number);
                cancels.Add(hop.Cancel);
                report(new TraceUpdate.HopRemoved(hop.Number));
            }

            return cancels;
        }

        /// <summary>Under the lock: starts the reverse lookup of an address seen for the first time.</summary>
        private void StartResolve(IPAddress address)
        {
            if (!options.ResolveNames || _names.ContainsKey(address)) return;

            _names[address] = null;
            _resolves.Add(Task.Run(() => ResolveNameAsync(address)));
        }

        private async Task ResolveNameAsync(IPAddress address)
        {
            string name = "";
            try
            {
                name = await resolver.ReverseAsync(address, cancel).WaitAsync(options.ResolveTimeout, cancel).ConfigureAwait(false) ?? "";
            }
            catch (Exception)
            {
                // no name: timed out, failed, or the run was stopped
            }

            // a reverse lookup that only gives the address back is no name
            if (string.Equals(name, address.ToString(), StringComparison.OrdinalIgnoreCase)) name = "";

            lock (_gate)
            {
                _names[address] = name;
                foreach (var hop in _hops.Values.Where(h => !h.Removed && h.Addresses.Contains(address)).OrderBy(h => h.Number))
                    report(new TraceUpdate.HopChanged(Snapshot(hop)));
            }
        }

        private TraceHop Snapshot(Hop hop)
        {
            var names = new Dictionary<IPAddress, string?>();
            foreach (var a in hop.Addresses)
                if (_names.TryGetValue(a, out var n))
                    names[a] = n;

            return new TraceHop(hop.Number, options.ProbesPerHop, hop.Probes.ToList(), hop.Addresses.ToList(), names, hop.Probing);
        }

        private TraceResult Outcome()
        {
            lock (_gate)
            {
                if (cancel.IsCancellationRequested)
                    return new TraceResult(TraceOutcome.Cancelled, _hops.Count == 0 ? 0 : _hops.Keys.Max(), _clock.Elapsed);

                if (_endAt != int.MaxValue && _hops.TryGetValue(_endAt, out var last))
                {
                    var state = Snapshot(last).State;
                    return new TraceResult(state == TraceHopState.Destination ? TraceOutcome.Reached : TraceOutcome.Unreachable, _endAt, _clock.Elapsed);
                }

                return new TraceResult(TraceOutcome.MaxHopsExceeded, options.MaxHops, _clock.Elapsed);
            }
        }

        private TraceResult Finish(TraceResult result)
        {
            lock (_gate) report(new TraceUpdate.Finished(result));
            return result;
        }

        private static string Describe(Exception ex, string destination)
        {
            string host = HostOf(destination);
            return ex switch
                   {
                       SocketException s when s.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData
                           => $"{host} could not be found ({s.Message}).",
                       SocketException s => $"{host} could not be resolved: {s.Message}",
                       _                 => ex.Message
                   };
        }
    }
}
