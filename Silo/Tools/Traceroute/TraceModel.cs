using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace Silo.Tools.Traceroute;

/// <summary>Which kind of address a host name is traced to.</summary>
public enum TraceAddressFamily
{
    /// <summary>IPv4 when the name has one, otherwise IPv6.</summary>
    Any,

    IPv4,

    IPv6
}

/// <summary>How a trace is run.</summary>
public sealed record TraceOptions
{
    /// <summary>The highest TTL (hop limit) probed.</summary>
    public int MaxHops { get; init; } = 30;

    /// <summary>Echo requests sent per hop.</summary>
    public int ProbesPerHop { get; init; } = 3;

    /// <summary>How long one probe waits for an answer.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How many hops are probed at the same time (1 = one after another, like <c>tracert</c>).</summary>
    public int HopsInFlight { get; init; } = 4;

    /// <summary>Pause between the probes of one hop.</summary>
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.Zero;

    /// <summary>Whether the addresses found are looked up in DNS (reverse lookup), each as soon as it is seen.</summary>
    public bool ResolveNames { get; init; } = true;

    /// <summary>How long one reverse lookup may take.</summary>
    public TimeSpan ResolveTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>The address family a host name is traced to.</summary>
    public TraceAddressFamily Family { get; init; } = TraceAddressFamily.Any;

    /// <summary>Bytes of payload in each echo request.</summary>
    public int PayloadSize { get; init; } = 32;

    /// <summary>The same options with every value brought into a usable range.</summary>
    public TraceOptions Normalized()
    {
        return this with
               {
                   MaxHops = Math.Clamp(MaxHops, 1, 255),
                   ProbesPerHop = Math.Clamp(ProbesPerHop, 1, 10),
                   Timeout = Clamp(Timeout, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30)),
                   HopsInFlight = Math.Clamp(HopsInFlight, 1, 32),
                   ProbeInterval = Clamp(ProbeInterval, TimeSpan.Zero, TimeSpan.FromSeconds(10)),
                   ResolveTimeout = Clamp(ResolveTimeout, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30)),
                   PayloadSize = Math.Clamp(PayloadSize, 0, 1472)
               };
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        return value < min ? min : value > max ? max : value;
    }
}

/// <summary>One echo request's answer, as an <see cref="IPinger" /> reports it.</summary>
/// <param name="Status">What came back (<see cref="IPStatus.TtlExpired" /> from a router on the way, <see cref="IPStatus.Success" /> from the destination).</param>
/// <param name="From">Who answered; null when nobody did.</param>
/// <param name="RttMs">Round-trip time in milliseconds; null when nobody answered.</param>
/// <param name="Error">Why the request could not be sent, when it failed locally.</param>
public sealed record PingProbeReply(IPStatus Status, IPAddress? From, double? RttMs, string? Error = null);

/// <summary>One probe of a hop.</summary>
/// <param name="Index">0-based position within the hop.</param>
/// <param name="Status">The answer's status.</param>
/// <param name="From">Who answered, if anyone.</param>
/// <param name="RttMs">Round-trip time in milliseconds, if anyone answered.</param>
public sealed record TraceProbe(int Index, IPStatus Status, IPAddress? From, double? RttMs)
{
    /// <summary>Whether someone answered this probe.</summary>
    public bool Answered => From is not null && RttMs is not null;
}

/// <summary>Where a hop stands.</summary>
public enum TraceHopState
{
    /// <summary>Probes are out and nobody has answered yet.</summary>
    Pending,

    /// <summary>A router on the way answered (TTL expired).</summary>
    Responded,

    /// <summary>Every probe timed out.</summary>
    NoReply,

    /// <summary>The destination itself answered.</summary>
    Destination,

    /// <summary>A router reported the destination unreachable.</summary>
    Unreachable
}

/// <summary>Whether the host name of a hop's address is known.</summary>
public enum TraceNameState
{
    /// <summary>Name lookups are off, or there is no address to look up.</summary>
    NotRequested,

    Resolving,

    Resolved,

    /// <summary>The address has no name (or the lookup failed or timed out).</summary>
    NoName
}

/// <summary>A snapshot of one hop: immutable, so it can be handed to another thread.</summary>
/// <param name="Number">The TTL that reached it (1 = the first router).</param>
/// <param name="ProbeCount">How many probes the hop gets in total.</param>
/// <param name="Probes">The probes answered so far (timeouts included), in order.</param>
/// <param name="Addresses">Every address that answered, in the order first seen (more than one on load-balanced paths).</param>
/// <param name="Names">Host names found so far, by address; an address that is still being looked up has a null name, one with no name an empty one.</param>
/// <param name="IsProbing">Whether probes are still outstanding.</param>
public sealed record TraceHop(
    int                                       Number,
    int                                       ProbeCount,
    IReadOnlyList<TraceProbe>                 Probes,
    IReadOnlyList<IPAddress>                  Addresses,
    IReadOnlyDictionary<IPAddress, string?>   Names,
    bool                                      IsProbing)
{
    /// <summary>The first address that answered.</summary>
    public IPAddress? Address => Addresses.Count > 0 ? Addresses[0] : null;

    /// <summary>Where the hop stands.</summary>
    public TraceHopState State
    {
        get
        {
            if (Probes.Any(p => p.Status == IPStatus.Success)) return TraceHopState.Destination;
            if (Probes.Any(p => TraceStatus.IsUnreachable(p.Status))) return TraceHopState.Unreachable;
            if (Probes.Any(p => p.Answered)) return TraceHopState.Responded;
            return IsProbing ? TraceHopState.Pending : TraceHopState.NoReply;
        }
    }

    /// <summary>Whether this hop is the destination.</summary>
    public bool IsDestination => State == TraceHopState.Destination;

    /// <summary>The host name of <see cref="Address" />, when known.</summary>
    public string? Name => Address is { } a && Names.TryGetValue(a, out var n) && !string.IsNullOrEmpty(n) ? n : null;

    /// <summary>Whether the name of <see cref="Address" /> is known.</summary>
    public TraceNameState NameState
    {
        get
        {
            if (Address is not { } a || !Names.TryGetValue(a, out var n)) return TraceNameState.NotRequested;
            return n switch
                   {
                       null => TraceNameState.Resolving,
                       ""   => TraceNameState.NoName,
                       _    => TraceNameState.Resolved
                   };
        }
    }

    /// <summary>Round-trip times of the probes that were answered.</summary>
    public IReadOnlyList<double> Rtts => Probes.Where(p => p.Answered).Select(p => p.RttMs!.Value).ToList();

    public double? Best => Rtts.Count > 0 ? Rtts.Min() : null;

    public double? Worst => Rtts.Count > 0 ? Rtts.Max() : null;

    public double? Average => Rtts.Count > 0 ? Rtts.Average() : null;

    /// <summary>The share of finished probes nobody answered (0..1).</summary>
    public double Loss => Probes.Count == 0 ? 0 : Probes.Count(p => !p.Answered) / (double)Probes.Count;
}

/// <summary>What is being traced, known once the destination is resolved.</summary>
/// <param name="Destination">What the person typed.</param>
/// <param name="Address">The address traced to.</param>
/// <param name="HostName">The host name it was resolved from (null when an address was typed).</param>
/// <param name="LocalName">This computer's name.</param>
/// <param name="LocalAddress">The local address the probes leave from, when it can be told.</param>
/// <param name="Options">The (normalized) options in force.</param>
public sealed record TraceTarget(string Destination, IPAddress Address, string? HostName, string LocalName, IPAddress? LocalAddress, TraceOptions Options);

/// <summary>How a trace ended.</summary>
public enum TraceOutcome
{
    Reached,

    Unreachable,

    /// <summary>The destination did not answer within <see cref="TraceOptions.MaxHops" />.</summary>
    MaxHopsExceeded,

    Cancelled,

    /// <summary>The trace could not start (the name did not resolve, no network...).</summary>
    Failed
}

/// <summary>The end of a trace.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Hops">How many hops it kept (the destination's number when it was reached).</param>
/// <param name="Elapsed">How long it took.</param>
/// <param name="Message">Details for <see cref="TraceOutcome.Failed" />.</param>
public sealed record TraceResult(TraceOutcome Outcome, int Hops, TimeSpan Elapsed, string? Message = null);

/// <summary>Something that happened during a trace, in the order it happened.</summary>
public abstract record TraceUpdate
{
    /// <summary>The destination resolved; probing starts.</summary>
    public sealed record Started(TraceTarget Target) : TraceUpdate;

    /// <summary>A hop appeared or changed (a probe came back, a name was found).</summary>
    public sealed record HopChanged(TraceHop Hop) : TraceUpdate;

    /// <summary>A hop probed in parallel turned out to lie beyond the destination and was dropped.</summary>
    public sealed record HopRemoved(int Number) : TraceUpdate;

    /// <summary>The trace is over; nothing follows.</summary>
    public sealed record Finished(TraceResult Result) : TraceUpdate;
}

/// <summary>Sends one echo request with a given TTL. The seam that lets the engine be tested without a network.</summary>
public interface IPinger
{
    /// <summary>Sends one echo request to <paramref name="target" /> that may cross at most <paramref name="ttl" /> routers.</summary>
    Task<PingProbeReply> PingAsync(IPAddress target, int ttl, TimeSpan timeout, int payloadSize, CancellationToken cancel);
}

/// <summary>Name lookups for a trace.</summary>
public interface IHostResolver
{
    /// <summary>This computer's name.</summary>
    string LocalHostName { get; }

    /// <summary>The addresses of a host name.</summary>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancel);

    /// <summary>The host name of an address (reverse lookup); null when it has none.</summary>
    Task<string?> ReverseAsync(IPAddress address, CancellationToken cancel);

    /// <summary>The local address packets to <paramref name="destination" /> leave from; null when it cannot be told.</summary>
    IPAddress? LocalAddressFor(IPAddress destination);
}

/// <summary>Classification of <see cref="IPStatus" /> values.</summary>
public static class TraceStatus
{
    /// <summary>A router on the way answered: the TTL (hop limit) ran out there.</summary>
    public static bool IsTtlExpired(IPStatus status)
    {
        return status is IPStatus.TtlExpired or IPStatus.TimeExceeded;
    }

    /// <summary>A router said the destination cannot be reached: the trace ends there.</summary>
    public static bool IsUnreachable(IPStatus status)
    {
        // DestinationProhibited (IPv6) has the same value as DestinationProtocolUnreachable, so it is covered by it
        return status is IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable or
                   IPStatus.DestinationPortUnreachable or IPStatus.DestinationProtocolUnreachable or
                   IPStatus.DestinationUnreachable or IPStatus.DestinationScopeMismatch;
    }
}
