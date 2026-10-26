using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;

namespace Silo.Tools.Traceroute;

/// <summary>How hops and traces read on the diagram and in the status bar.</summary>
public static class TraceText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A time in milliseconds: <c>&lt;1 ms</c>, <c>4.2 ms</c>, <c>38 ms</c>.</summary>
    public static string Ms(double ms)
    {
        return Number(ms) + " ms";
    }

    /// <summary>The probes of a hop in order: <c>12 · 14 · * ms</c>; probes not back yet are <c>…</c>.</summary>
    public static string Probes(TraceHop hop)
    {
        var parts = new List<string>();
        for (int i = 0; i < hop.ProbeCount; i++)
        {
            var probe = hop.Probes.FirstOrDefault(p => p.Index == i);
            parts.Add(probe switch
                      {
                          null               => hop.IsProbing ? "…" : "-",
                          { Answered: true } => Number(probe.RttMs!.Value),
                          _                  => "*"
                      });
        }

        return string.Join(" · ", parts) + (hop.Probes.Any(p => p.Answered) ? " ms" : "");
    }

    /// <summary>A time as the diagram's connectors show it: <c>33ms</c>.</summary>
    public static string MsCompact(double ms)
    {
        return Number(ms) + "ms";
    }

    /// <summary>The hop's average round-trip time for its connector (<c>33ms</c>); empty when nobody answered.</summary>
    public static string Latency(TraceHop hop)
    {
        return hop.Average is { } avg ? MsCompact(avg) : "";
    }

    /// <summary>
    ///     What the hop's box shows in bold: its address (with how many more answered), <c>*</c> when nobody answered, <c>…</c>
    ///     while waiting.
    /// </summary>
    public static string BoxAddress(TraceHop hop)
    {
        if (hop.Address is not { } a) return hop.State == TraceHopState.Pending ? "…" : "*";
        return hop.Addresses.Count > 1 ? $"{a} (+{hop.Addresses.Count - 1})" : a.ToString();
    }

    /// <summary>The hop's address (with how many more answered), or what stands in for it.</summary>
    public static string Address(TraceHop hop)
    {
        if (hop.Address is not { } a) return hop.State == TraceHopState.Pending ? "probing…" : "no reply";
        return hop.Addresses.Count > 1 ? $"{a} (+{hop.Addresses.Count - 1})" : a.ToString();
    }

    /// <summary>The hop's host name, or how its lookup stands.</summary>
    public static string Name(TraceHop hop)
    {
        return hop.NameState switch
               {
                   TraceNameState.Resolving => "resolving…",
                   TraceNameState.Resolved  => hop.Name!,
                   TraceNameState.NoName    => "(no name)",
                   _                        => ""
               };
    }

    /// <summary><c>33% loss</c> when some (not all) probes went unanswered; empty otherwise.</summary>
    public static string Loss(TraceHop hop)
    {
        if (hop.Probes.Count == 0 || hop.State == TraceHopState.NoReply || hop.Loss <= 0) return "";
        return hop.Loss.ToString("0%", Inv) + " loss";
    }

    /// <summary>Change of the best time from the previous hop (from this computer for hop 1): <c>+12 ms</c>, <c>-3 ms</c>.</summary>
    public static string Delta(TraceHop? previous, TraceHop hop)
    {
        if (hop.Best is not { } best) return "";
        if (previous is null) return "+" + Ms(best);
        if (previous.Best is not { } before) return "";

        double d = best - before;
        return (d < 0 ? "-" : "+") + Ms(Math.Abs(d));
    }

    /// <summary>Everything about a hop on one line, for its tooltip.</summary>
    public static string Tooltip(TraceHop hop)
    {
        var parts = new List<string> { $"Hop {hop.Number}" };

        foreach (var a in hop.Addresses)
            parts.Add(hop.Names.TryGetValue(a, out var n) && !string.IsNullOrEmpty(n) ? $"{a} ({n})" : a.ToString());

        if (hop.Best is { } best)
            parts.Add($"min {Number(best)} / avg {Number(hop.Average!.Value)} / max {Number(hop.Worst!.Value)} ms");

        if (hop.Probes.Count > 0) parts.Add($"{hop.Probes.Count(p => p.Answered)} of {hop.Probes.Count} answered");

        foreach (var status in hop.Probes.Select(p => p.Status).Distinct())
            if (status is not (IPStatus.Success or IPStatus.TimedOut) && !TraceStatus.IsTtlExpired(status))
                parts.Add(Describe(status));

        if (hop.IsDestination) parts.Add("destination");
        if (hop.IsProbing) parts.Add("probing");
        return string.Join(" · ", parts);
    }

    /// <summary>A probe status in words.</summary>
    public static string Describe(IPStatus status)
    {
        return status switch
               {
                   IPStatus.DestinationHostUnreachable     => "host unreachable",
                   IPStatus.DestinationNetworkUnreachable  => "network unreachable",
                   IPStatus.DestinationPortUnreachable     => "port unreachable",
                   IPStatus.DestinationProtocolUnreachable => "protocol unreachable / prohibited",
                   IPStatus.DestinationUnreachable         => "destination unreachable",
                   IPStatus.DestinationScopeMismatch       => "scope mismatch",
                   IPStatus.TimedOut                       => "timed out",
                   IPStatus.Unknown                        => "probe failed",
                   _                                       => status.ToString()
               };
    }

    /// <summary>The headline: <c>Traceroute to example.com (93.184.215.14)</c>.</summary>
    public static string Title(string? destination, TraceTarget? target)
    {
        if (target is null) return string.IsNullOrWhiteSpace(destination) ? "Visual Traceroute" : $"Traceroute to {destination.Trim()}";
        return target.HostName is { } host ? $"Traceroute to {host} ({target.Address})" : $"Traceroute to {target.Address}";
    }

    /// <summary>Where the trace stands, in a sentence.</summary>
    public static string Status(string? destination, TraceTarget? target, IReadOnlyCollection<TraceHop> hops, TraceResult? result)
    {
        if (result is not null)
            return result.Outcome switch
                   {
                       TraceOutcome.Reached         => $"Reached in {Plural(result.Hops, "hop")} · {Seconds(result.Elapsed)}",
                       TraceOutcome.Unreachable     => $"Destination unreachable (reported at hop {result.Hops}) · {Seconds(result.Elapsed)}",
                       TraceOutcome.MaxHopsExceeded => $"Not reached within {Plural(result.Hops, "hop")} · {Seconds(result.Elapsed)}",
                       TraceOutcome.Cancelled       => $"Stopped after {Plural(hops.Count, "hop")}",
                       _                            => result.Message ?? "The trace failed."
                   };

        if (string.IsNullOrWhiteSpace(destination)) return "Enter a host name or address and press Trace.";
        if (target is null) return $"Resolving {destination.Trim()}…";

        int probing = hops.Count(h => h.IsProbing);
        int highest = hops.Count == 0 ? 0 : hops.Max(h => h.Number);
        return probing > 0
                   ? $"Probing… {Plural(highest, "hop")} so far, {probing} in flight (max {target.Options.MaxHops})"
                   : $"Probing… {Plural(highest, "hop")} so far";
    }

    private static string Number(double ms)
    {
        if (ms < 1) return "<1";
        return ms < 10 ? ms.ToString("0.#", Inv) : Math.Round(ms).ToString("0", Inv);
    }

    private static string Seconds(TimeSpan t)
    {
        return t.TotalSeconds.ToString("0.0", Inv) + " s";
    }

    private static string Plural(int n, string word)
    {
        return n == 1 ? $"1 {word}" : $"{n} {word}s";
    }
}

/// <summary>What a hop's colour says about it.</summary>
public enum TraceHopRole
{
    /// <summary>Probes are out, nobody has answered yet (white).</summary>
    Pending,

    /// <summary>The destination (yellow).</summary>
    Destination,

    /// <summary>A router that answered and has a host name (green).</summary>
    Named,

    /// <summary>A router that answered without a host name, or whose name is still being looked up (blue).</summary>
    Unnamed,

    /// <summary>A silent hop with an answering hop after it: a router that does not reply, the path goes on (gray).</summary>
    Missing,

    /// <summary>The path fails here: unreachable, or silent with nothing answering after it (red).</summary>
    Failing
}

/// <summary>
///     The colours of hops: yellow = this computer and the destination, green = router with a host name, blue = router without one, white = pending,
///     gray = silent router the path goes on past, red = failing from here on.
/// </summary>
public static class TracePalette
{
    /// <summary>
    ///     What <paramref name="hop" /> is, given every hop of the trace: a silent hop is only <see cref="TraceHopRole.Failing" />
    ///     once nothing after it answered and nothing after it is still waiting for an answer (or the trace is over).
    /// </summary>
    public static TraceHopRole RoleOf(TraceHop hop, IEnumerable<TraceHop> hops, bool finished)
    {
        switch (hop.State)
        {
            case TraceHopState.Pending:
                return TraceHopRole.Pending;
            case TraceHopState.Destination:
                return TraceHopRole.Destination;
            case TraceHopState.Unreachable:
                return TraceHopRole.Failing;
            case TraceHopState.Responded:
                return hop.NameState == TraceNameState.Resolved ? TraceHopRole.Named : TraceHopRole.Unnamed;
        }

        // silent: does the path go on past it?
        var later = hops.Where(h => h.Number > hop.Number).ToList();
        if (later.Any(h => h.State is TraceHopState.Responded or TraceHopState.Destination or TraceHopState.Unreachable)) return TraceHopRole.Missing;
        if (!finished && (later.Any(h => h.IsProbing) || later.Count == 0)) return TraceHopRole.Missing; // not known yet
        return TraceHopRole.Failing;
    }

    /// <summary>The fill of a hop: none, the boxes are outlines (the role is in the outline colour).</summary>
    public static string Fill(TraceHopRole role)
    {
        return "none";
    }

    /// <summary>The outline colour of a role, as a draw.io <c>light-dark(light, dark)</c> colour.</summary>
    public static string Stroke(TraceHopRole role)
    {
        return role switch
               {
                   TraceHopRole.Destination => EndStroke,
                   TraceHopRole.Named       => "light-dark(#82b366,#97d077)",
                   TraceHopRole.Unnamed     => "light-dark(#6c8ebf,#7ea6e0)",
                   TraceHopRole.Missing     => "light-dark(#999999,#808080)",
                   TraceHopRole.Failing     => "light-dark(#b85450,#ff6b6b)",
                   _                        => "light-dark(#666666,#ffffff)"
               };
    }

    /// <summary>
    ///     Yellow, for the two ends of the route (this computer and the destination): a darker yellow on draw.io's light page and
    ///     a bright pale yellow on its dark page, as a <c>light-dark(light, dark)</c> colour so draw.io's dark mode leaves it alone.
    /// </summary>
    public const string EndStroke = "light-dark(#d6b656,#fff59d)";

    /// <summary><c>1</c> when the hop is drawn dashed (still waiting for its first answer), else <c>0</c>.</summary>
    public static string Dashed(TraceHop hop)
    {
        return hop.State == TraceHopState.Pending ? "1" : "0";
    }
}
