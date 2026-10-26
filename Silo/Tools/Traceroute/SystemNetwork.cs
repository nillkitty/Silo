using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Silo.Tools.Traceroute;

/// <summary>Echo requests with <see cref="Ping" /> (ICMP; needs no administrator rights on Windows).</summary>
public sealed class SystemPinger : IPinger
{
    /// <inheritdoc />
    /// <remarks>
    ///     Windows reports no round-trip time for a "TTL expired" answer, so the time is measured here around the request; for an
    ///     answer from the destination the system's own figure is used when it has one.
    /// </remarks>
    public async Task<PingProbeReply> PingAsync(IPAddress target, int ttl, TimeSpan timeout, int payloadSize, CancellationToken cancel)
    {
        var buffer = new byte[payloadSize];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)('a' + i % 23);

        // one Ping per request: a Ping object runs one request at a time
        using var ping = new Ping();
        var clock = Stopwatch.StartNew();
        PingReply reply;
        try
        {
            reply = await ping.SendPingAsync(target, timeout, buffer, new PingOptions(ttl, true), cancel).ConfigureAwait(false);
        }
        catch (PingException ex) when (!cancel.IsCancellationRequested)
        {
            return new PingProbeReply(IPStatus.Unknown, null, null, ex.InnerException?.Message ?? ex.Message);
        }

        clock.Stop();

        IPAddress? from = reply.Status == IPStatus.TimedOut || reply.Address is null || IsUnspecified(reply.Address) ? null : reply.Address;
        double rtt = reply.Status == IPStatus.Success && reply.RoundtripTime > 0 ? reply.RoundtripTime : clock.Elapsed.TotalMilliseconds;
        return new PingProbeReply(reply.Status, from, from is null ? null : rtt);
    }

    private static bool IsUnspecified(IPAddress a)
    {
        return a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);
    }
}

/// <summary>Name lookups with <see cref="Dns" />.</summary>
public sealed class SystemHostResolver : IHostResolver
{
    /// <inheritdoc />
    /// <remarks>The computer's own (NetBIOS) name, as Windows shows it: <c>NILLBOOK</c>.</remarks>
    public string LocalHostName => Environment.MachineName;

    /// <inheritdoc />
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancel)
    {
        return Dns.GetHostAddressesAsync(host, cancel);
    }

    /// <inheritdoc />
    public async Task<string?> ReverseAsync(IPAddress address, CancellationToken cancel)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), cancel).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch (SocketException)
        {
            return null; // no PTR record
        }
    }

    /// <inheritdoc />
    /// <remarks>Connecting a UDP socket sends nothing; it only makes the system pick the route, and so the local address.</remarks>
    public IPAddress? LocalAddressFor(IPAddress destination)
    {
        using var socket = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(new IPEndPoint(destination, 33434));
        return (socket.LocalEndPoint as IPEndPoint)?.Address;
    }
}
