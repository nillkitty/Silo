using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Silo.Drawio.Live;
using Silo.Drawio.Mx;
using Silo.Tools.Traceroute;

namespace Silo.Tests.Tools;

public class TracerouteTests
{
    private static readonly IPAddress Target = IPAddress.Parse("93.184.215.14");

    /// <summary>A pretend network: routers by hop (null = a router that never answers), then the destination.</summary>
    private sealed class FakeNetwork : IPinger, IHostResolver
    {
        public List<IPAddress?> Routers { get; init; } = [];

        public Dictionary<IPAddress, string> Names { get; init; } = [];

        public Func<int, TimeSpan> Delay { get; init; } = _ => TimeSpan.Zero;

        public TimeSpan NameDelay { get; init; } = TimeSpan.FromMilliseconds(5);

        /// <summary>A hop whose router reports the destination unreachable.</summary>
        public int? UnreachableAt { get; init; }

        public bool FailResolve { get; init; }

        public ConcurrentBag<int> Sent { get; } = [];

        public string LocalHostName => "testbox";

        public async Task<PingProbeReply> PingAsync(IPAddress target, int ttl, TimeSpan timeout, int payloadSize, CancellationToken cancel)
        {
            Sent.Add(ttl);
            await Task.Delay(Delay(ttl), cancel);

            if (UnreachableAt == ttl) return new PingProbeReply(IPStatus.DestinationHostUnreachable, IPAddress.Parse("10.0.0." + ttl), ttl * 10);
            if (ttl > Routers.Count) return new PingProbeReply(IPStatus.Success, target, 100);
            return Routers[ttl - 1] is { } router
                       ? new PingProbeReply(IPStatus.TtlExpired, router, ttl * 10)
                       : new PingProbeReply(IPStatus.TimedOut, null, null);
        }

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancel)
        {
            if (FailResolve) throw new SocketException((int)SocketError.HostNotFound);
            return Task.FromResult(new[] { IPAddress.Parse("2606:2800:21f:cb07:6820:80da:af6b:8b2c"), Target });
        }

        public async Task<string?> ReverseAsync(IPAddress address, CancellationToken cancel)
        {
            await Task.Delay(NameDelay, cancel);
            return Names.TryGetValue(address, out var n) ? n : null;
        }

        public IPAddress? LocalAddressFor(IPAddress destination)
        {
            return IPAddress.Parse("192.168.1.10");
        }
    }

    private static IPAddress Ip(string s)
    {
        return IPAddress.Parse(s);
    }

    private static TraceOptions Fast(int inFlight = 1, int maxHops = 30)
    {
        return new TraceOptions { HopsInFlight = inFlight, MaxHops = maxHops, ProbesPerHop = 3, Timeout = TimeSpan.FromMilliseconds(200) };
    }

    private static async Task<(TraceResult Result, List<TraceUpdate> Updates)> TraceAsync(FakeNetwork net, TraceOptions options, string destination = "example.test")
    {
        var updates = new List<TraceUpdate>();
        var result  = await new TraceRoute(net, net).RunAsync(destination, options, u => updates.Add(u));
        return (result, updates);
    }

    /// <summary>The last state of every hop still there at the end.</summary>
    private static Dictionary<int, TraceHop> FinalHops(IEnumerable<TraceUpdate> updates)
    {
        var hops = new Dictionary<int, TraceHop>();
        foreach (var u in updates)
            switch (u)
            {
                case TraceUpdate.HopChanged c:
                    hops[c.Hop.Number] = c.Hop;
                    break;
                case TraceUpdate.HopRemoved r:
                    hops.Remove(r.Number);
                    break;
            }

        return hops;
    }

    // ---- the engine --------------------------------------------------------------------------------

    [Test]
    public async Task A_trace_reaches_the_destination_through_every_router()
    {
        var net = new FakeNetwork
                  {
                      Routers = [Ip("10.0.0.1"), Ip("10.0.0.2"), null, Ip("10.0.0.4")],
                      Names   = new() { [Ip("10.0.0.1")] = "gw.lan", [Target] = "example.test" }
                  };

        var (result, updates) = await TraceAsync(net, Fast());

        Assert.That(result.Outcome, Is.EqualTo(TraceOutcome.Reached));
        Assert.That(result.Hops, Is.EqualTo(5));
        Assert.That(updates[0], Is.InstanceOf<TraceUpdate.Started>());
        Assert.That(updates[^1], Is.InstanceOf<TraceUpdate.Finished>());

        var started = (TraceUpdate.Started)updates[0];
        Assert.That(started.Target.Address, Is.EqualTo(Target)); // IPv4 preferred for Any
        Assert.That(started.Target.HostName, Is.EqualTo("example.test"));
        Assert.That(started.Target.LocalName, Is.EqualTo("testbox"));

        var hops = FinalHops(updates);
        Assert.That(hops.Keys.OrderBy(k => k).ToArray(), Is.EqualTo(new[] { 1, 2, 3, 4, 5 }));
        Assert.That(hops[1].State, Is.EqualTo(TraceHopState.Responded));
        Assert.That(hops[1].Name, Is.EqualTo("gw.lan"));
        Assert.That(hops[2].NameState, Is.EqualTo(TraceNameState.NoName));
        Assert.That(hops[3].State, Is.EqualTo(TraceHopState.NoReply));
        Assert.That(hops[3].Probes.Count, Is.EqualTo(3));
        Assert.That(hops[5].IsDestination, Is.True);
        Assert.That(hops[5].Name, Is.EqualTo("example.test"));
        Assert.That(hops.Values.All(h => !h.IsProbing), Is.True);
        Assert.That(hops[4].Best, Is.EqualTo(40));
    }

    [Test]
    public async Task Hops_probed_in_parallel_beyond_the_destination_are_dropped()
    {
        // later TTLs answer first, so the destination is first seen at a TTL that is too high
        var net = new FakeNetwork
                  {
                      Routers = [Ip("10.0.0.1"), Ip("10.0.0.2"), Ip("10.0.0.3")],
                      Delay   = ttl => TimeSpan.FromMilliseconds(Math.Max(1, (12 - ttl) * 4))
                  };

        var (result, updates) = await TraceAsync(net, Fast(inFlight: 8));

        Assert.That(result.Outcome, Is.EqualTo(TraceOutcome.Reached));
        Assert.That(result.Hops, Is.EqualTo(4));
        Assert.That(FinalHops(updates).Keys.OrderBy(k => k).ToArray(), Is.EqualTo(new[] { 1, 2, 3, 4 }));
        Assert.That(updates.OfType<TraceUpdate.HopRemoved>().Any(), Is.True);

        // nothing about a dropped hop arrives after it was dropped
        var removedAt = new Dictionary<int, int>();
        for (int i = 0; i < updates.Count; i++)
            if (updates[i] is TraceUpdate.HopRemoved r)
                removedAt[r.Number] = i;
        for (int i = 0; i < updates.Count; i++)
            if (updates[i] is TraceUpdate.HopChanged c && removedAt.TryGetValue(c.Hop.Number, out int at))
                Assert.That(i, Is.LessThan(at), $"hop {c.Hop.Number} changed after it was removed");
    }

    [Test]
    public async Task A_router_reporting_unreachable_ends_the_trace()
    {
        var net = new FakeNetwork { Routers = [Ip("10.0.0.1"), Ip("10.0.0.2"), Ip("10.0.0.3"), Ip("10.0.0.4")], UnreachableAt = 3 };

        var (result, updates) = await TraceAsync(net, Fast(inFlight: 2));

        Assert.That(result.Outcome, Is.EqualTo(TraceOutcome.Unreachable));
        Assert.That(result.Hops, Is.EqualTo(3));
        var hops = FinalHops(updates);
        Assert.That(hops.Keys.Max(), Is.EqualTo(3));
        Assert.That(hops[3].State, Is.EqualTo(TraceHopState.Unreachable));
    }

    [Test]
    public async Task A_silent_route_stops_at_max_hops()
    {
        var net = new FakeNetwork { Routers = Enumerable.Repeat<IPAddress?>(null, 40).ToList() };

        var (result, updates) = await TraceAsync(net, Fast(inFlight: 3, maxHops: 5), "198.51.100.7");

        Assert.That(result.Outcome, Is.EqualTo(TraceOutcome.MaxHopsExceeded));
        Assert.That(result.Hops, Is.EqualTo(5));
        Assert.That(FinalHops(updates).Values.All(h => h.State == TraceHopState.NoReply), Is.True);
        Assert.That(net.Sent.Max(), Is.EqualTo(5));
        Assert.That(((TraceUpdate.Started)updates[0]).Target.HostName, Is.Null);
    }

    [Test]
    public async Task Stopping_a_trace_ends_it_as_cancelled()
    {
        var net    = new FakeNetwork { Routers = Enumerable.Repeat<IPAddress?>(Ip("10.9.9.9"), 30).ToList(), Delay = _ => TimeSpan.FromMilliseconds(30) };
        var cancel = new CancellationTokenSource();
        var updates = new List<TraceUpdate>();

        var run = new TraceRoute(net, net).RunAsync("example.test", Fast(), u =>
                                                                       {
                                                                           updates.Add(u);
                                                                           if (u is TraceUpdate.HopChanged { Hop.Number: 2 }) cancel.Cancel();
                                                                       }, cancel.Token);
        var result = await run;

        Assert.That(result.Outcome, Is.EqualTo(TraceOutcome.Cancelled));
        Assert.That(updates[^1], Is.InstanceOf<TraceUpdate.Finished>());
        Assert.That(net.Sent.Max(), Is.LessThan(5));
    }

    [Test]
    public async Task A_name_that_does_not_resolve_fails_the_trace_without_throwing()
    {
        var (result, updates) = await TraceAsync(new FakeNetwork { FailResolve = true }, Fast(), "nowhere.invalid");

        Assert.That(result.Outcome, Is.EqualTo(TraceOutcome.Failed));
        Assert.That(result.Message, Does.Contain("nowhere.invalid"));
        Assert.That(updates.Count, Is.EqualTo(1));
    }

    [TestCase("example.com", "example.com")]
    [TestCase("  example.com ", "example.com")]
    [TestCase("https://example.com/some/page?q=1", "example.com")]
    [TestCase("[2001:db8::1]", "2001:db8::1")]
    [TestCase("http://[2001:db8::1]:8080/", "2001:db8::1")]
    public void The_host_is_taken_from_what_was_typed(string typed, string host)
    {
        Assert.That(TraceRoute.HostOf(typed), Is.EqualTo(host));
    }

    [Test]
    public void The_address_family_picks_the_address()
    {
        var all = new[] { Ip("2001:db8::1"), Ip("192.0.2.1") };

        Assert.That(TraceRoute.Pick(all, TraceAddressFamily.Any), Is.EqualTo(Ip("192.0.2.1")));
        Assert.That(TraceRoute.Pick(all, TraceAddressFamily.IPv6), Is.EqualTo(Ip("2001:db8::1")));
        Assert.That(TraceRoute.Pick([Ip("2001:db8::1")], TraceAddressFamily.Any), Is.EqualTo(Ip("2001:db8::1")));
        Assert.That(TraceRoute.Pick([Ip("2001:db8::1")], TraceAddressFamily.IPv4), Is.Null);
    }

    [Test]
    public void Options_are_brought_into_range()
    {
        var o = new TraceOptions { MaxHops = 0, ProbesPerHop = 99, HopsInFlight = -3, Timeout = TimeSpan.Zero }.Normalized();

        Assert.That(o.MaxHops, Is.EqualTo(1));
        Assert.That(o.ProbesPerHop, Is.EqualTo(10));
        Assert.That(o.HopsInFlight, Is.EqualTo(1));
        Assert.That(o.Timeout, Is.EqualTo(TimeSpan.FromMilliseconds(100)));
    }

    // ---- text and colours --------------------------------------------------------------------------

    private static TraceHop Hop(int n, bool probing, params (IPStatus Status, string? From, double? Rtt)[] probes)
    {
        var list  = probes.Select((p, i) => new TraceProbe(i, p.Status, p.From is null ? null : Ip(p.From), p.Rtt)).ToList();
        var addrs = list.Where(p => p.From is not null).Select(p => p.From!).Distinct().ToList();
        return new TraceHop(n, 3, list, addrs, new Dictionary<IPAddress, string?>(), probing);
    }

    [Test]
    public void Probe_times_read_in_order_with_timeouts_and_outstanding_probes()
    {
        var hop = Hop(2, true, (IPStatus.TtlExpired, "10.0.0.2", 12.4), (IPStatus.TimedOut, null, null));

        Assert.That(TraceText.Probes(hop), Is.EqualTo("12 · * · … ms"));
        Assert.That(TraceText.Loss(hop), Is.EqualTo("50% loss"));
        Assert.That(TraceText.Address(hop), Is.EqualTo("10.0.0.2"));
        Assert.That(TraceText.Ms(0.4), Is.EqualTo("<1 ms"));
        Assert.That(TraceText.Ms(4.2), Is.EqualTo("4.2 ms"));
        Assert.That(TraceText.Ms(38.4), Is.EqualTo("38 ms"));
    }

    [Test]
    public void A_silent_hop_reads_as_no_reply_and_is_drawn_dashed()
    {
        var hop = Hop(3, false, (IPStatus.TimedOut, null, null), (IPStatus.TimedOut, null, null), (IPStatus.TimedOut, null, null));

        Assert.That(hop.State, Is.EqualTo(TraceHopState.NoReply));
        Assert.That(TraceText.Address(hop), Is.EqualTo("no reply"));
        Assert.That(TraceText.Probes(hop), Is.EqualTo("* · * · *"));
        Assert.That(TraceText.Loss(hop), Is.EqualTo(""));
        Assert.That(TracePalette.Dashed(hop), Is.EqualTo("0")); // only a hop still waiting is dashed
    }

    [Test]
    public void The_latency_change_is_measured_from_the_previous_hop()
    {
        var one = Hop(1, false, (IPStatus.TtlExpired, "10.0.0.1", 3));
        var two = Hop(2, false, (IPStatus.TtlExpired, "10.0.0.2", 25));
        var silent = Hop(2, false, (IPStatus.TimedOut, null, null));

        Assert.That(TraceText.Delta(null, one), Is.EqualTo("+3 ms"));
        Assert.That(TraceText.Delta(one, two), Is.EqualTo("+22 ms"));
        Assert.That(TraceText.Delta(two, one), Is.EqualTo("-22 ms"));
        Assert.That(TraceText.Delta(silent, two), Is.EqualTo(""));
    }

    // ---- the diagram source ------------------------------------------------------------------------

    private static TraceHop Silent(int n) => Hop(n, false, (IPStatus.TimedOut, null, null));

    private static TraceHop Router(int n) => Hop(n, false, (IPStatus.TtlExpired, "10.0.0." + n, n * 10));

    private static TraceHop Named(TraceHop h, string name) => h with { Names = new Dictionary<IPAddress, string?> { [h.Address!] = name } };

    private static TraceHop Dest(int n) => Hop(n, false, (IPStatus.Success, "140.82.114.3", 200));

    private static TracerouteDiagramSource Traced(bool collapse, params TraceHop[] hops)
    {
        var source = new TracerouteDiagramSource { CollapseSilent = collapse };
        source.Begin("example.test");
        source.Apply(new TraceUpdate.Started(new TraceTarget("example.test", Ip("140.82.114.3"), "example.test", "NILLBOOK", Ip("10.139.76.22"), new TraceOptions())));
        foreach (var hop in hops) source.Apply(new TraceUpdate.HopChanged(hop));
        return source;
    }

    private static DiagramNode Node(DiagramModel m, string id) => m.Nodes.Single(n => n.Id == id);

    private static DiagramEdge EdgeOf(DiagramModel m, string id) => m.Edges.Single(e => e.Id == id);

    private static string? LabelOf(DiagramEdge e) => e.Data!.TryGetValue(DiagramBindings.LabelAttribute, out var l) ? l : null;

    [Test]
    public void The_route_is_laid_out_in_rows_like_the_mockup()
    {
        // NILLBOOK -> 1 -> 2 (named) -> * -> 4 -> * * * -> destination
        var source = Traced(false, Router(1), Named(Router(2), "158.qarestr.sub-172-19-12.myvzw.com"), Silent(3), Router(4),
                            Silent(5), Silent(6), Silent(7), Dest(8));
        var m = source.Build();

        var local = Node(m, TracerouteDiagramSource.LocalId);
        var hop1  = Node(m, TracerouteDiagramSource.HopId(1));
        var hop2  = Node(m, TracerouteDiagramSource.HopId(2));
        var hop3  = Node(m, TracerouteDiagramSource.HopId(3));
        var hop4  = Node(m, TracerouteDiagramSource.HopId(4));
        var five  = Node(m, TracerouteDiagramSource.HopId(5));
        var six   = Node(m, TracerouteDiagramSource.HopId(6));
        var dest  = Node(m, TracerouteDiagramSource.HopId(8));

        // this computer and hop 1 share the first row; later hops each start a row in hop 1's column
        Assert.That(hop1.Y + hop1.Height / 2, Is.EqualTo(local.Y + local.Height / 2));
        Assert.That(hop1.X, Is.GreaterThan(local.X + local.Width));
        foreach (var n in new[] { hop2, hop3, hop4, five }) Assert.That(n.X, Is.EqualTo(hop1.X));
        Assert.That(hop2.Y, Is.GreaterThan(hop1.Y + hop1.Height));
        Assert.That(hop3.Y, Is.GreaterThan(hop2.Y + hop2.Height));

        // a named hop is two lines and as wide as its name; a single silent hop is a normal box
        Assert.That(hop2.Height, Is.GreaterThan(hop1.Height));
        Assert.That(hop2.Width, Is.GreaterThan(hop1.Width));
        Assert.That(hop3.Width, Is.EqualTo(hop1.Width));

        // a run of silent hops is one row of small boxes; the destination ends that row
        Assert.That(six.Y, Is.EqualTo(five.Y));
        Assert.That(six.X, Is.GreaterThan(five.X + five.Width));
        Assert.That(five.Width, Is.LessThan(hop1.Width));
        Assert.That(dest.Y + dest.Height / 2, Is.EqualTo(five.Y + five.Height / 2));
        Assert.That(dest.X, Is.GreaterThan(Node(m, TracerouteDiagramSource.HopId(7)).X));

        // the ends are yellow outlines, the boxes have no fill
        Assert.That(local.Style, Does.Contain(TracePalette.EndStroke));
        Assert.That(hop1.Style, Does.Contain("fillColor=none"));
        Assert.That(DiagramBindings.Expand("{trace:h8.stroke}", false), Is.EqualTo(TracePalette.EndStroke));
        Assert.That(DiagramBindings.Expand("<b>{trace:local.name}</b>", false), Is.EqualTo("<b>NILLBOOK</b>"));
        Assert.That(DiagramBindings.Expand("{trace:h2.dns}|{trace:h2.addr}|{trace:h3.addr}", false),
                    Is.EqualTo("158.qarestr.sub-172-19-12.myvzw.com|10.0.0.2|*"));
    }

    [Test]
    public void Connectors_carry_the_time_of_the_hop_they_leave_and_bridge_silent_hops()
    {
        var m = Traced(false, Router(1), Router(2), Silent(3), Router(4), Silent(5), Silent(6), Dest(7)).Build();

        Assert.That(LabelOf(EdgeOf(m, TracerouteDiagramSource.EdgeId(1))), Is.Null); // from this computer
        Assert.That(LabelOf(EdgeOf(m, TracerouteDiagramSource.EdgeId(2))), Is.EqualTo("{trace:h1.ms}"));
        Assert.That(DiagramBindings.Expand("{trace:h1.ms}", false), Is.EqualTo("10ms"));

        // hop 2 is followed by a silent hop: its time is on the dashed bridge to hop 4, which enters hop 4 on the right
        Assert.That(LabelOf(EdgeOf(m, TracerouteDiagramSource.EdgeId(3))), Is.Null);
        var bridge = EdgeOf(m, TracerouteDiagramSource.BridgeId(2));
        Assert.That((bridge.SourceId, bridge.TargetId), Is.EqualTo((TracerouteDiagramSource.HopId(2), TracerouteDiagramSource.HopId(4))));
        Assert.That(LabelOf(bridge), Is.EqualTo("{trace:h2.ms}"));
        Assert.That(bridge.Style, Does.Contain("dashed=1"));
        Assert.That(bridge.Style, Does.Contain("entryX=1;"));

        // the bridge to the destination at the end of the run's row enters it on the left
        var last = EdgeOf(m, TracerouteDiagramSource.BridgeId(4));
        Assert.That(last.TargetId, Is.EqualTo(TracerouteDiagramSource.HopId(7)));
        Assert.That(last.Style, Does.Contain("entryX=0;"));
        Assert.That(last.Style, Does.Not.Contain("entryX=1;"));

        Assert.That(m.Edges.All(e => e.Style.Contains("exitX=1;")), Is.True);
    }

    [Test]
    public void The_source_reports_the_difference_as_edits()
    {
        var source  = Traced(false);
        var edits   = new List<DiagramEdit>();
        int rebuilt = 0;
        source.EditsReady += e => edits.AddRange(e);
        source.Changed    += () => rebuilt++;

        source.Apply(new TraceUpdate.HopChanged(Hop(1, true)));
        Assert.That(edits.OfType<DiagramEdit.AddNode>().Select(a => a.Node.Id).ToArray(), Is.EqualTo(new[] { TracerouteDiagramSource.HopId(1) }));
        Assert.That(edits.OfType<DiagramEdit.AddEdge>().Single().Edge.SourceId, Is.EqualTo(TracerouteDiagramSource.LocalId));

        // the name arrives: the box grows (an update of its bounds), nothing is added
        edits.Clear();
        source.Apply(new TraceUpdate.HopChanged(Named(Router(1), "a-rather-long-router-name.example.net")));
        var update = edits.OfType<DiagramEdit.Update>().Single(u => u.Id == TracerouteDiagramSource.HopId(1));
        Assert.That(update.Bounds, Is.Not.Null);
        Assert.That(edits.OfType<DiagramEdit.AddNode>().Any(), Is.False);

        // a hop dropped (probed beyond the destination) is removed with its connector
        source.Apply(new TraceUpdate.HopChanged(Hop(2, true)));
        edits.Clear();
        source.Apply(new TraceUpdate.HopRemoved(2));
        Assert.That(edits.OfType<DiagramEdit.Remove>().Select(r => r.Id).OrderBy(i => i).ToArray(),
                    Is.EqualTo(new[] { TracerouteDiagramSource.EdgeId(2), TracerouteDiagramSource.HopId(2) }));
        Assert.That(rebuilt, Is.EqualTo(0));
    }

    [Test]
    public void A_hop_added_and_dropped_in_one_batch_leaves_nothing_behind()
    {
        // what a parallel trace produces when hop 12 answers before hop 11 turns out to be the destination
        var source = Traced(false, Router(10));
        var edits  = new List<DiagramEdit>();
        source.EditsReady += e => edits.AddRange(e);

        source.Apply(new TraceUpdate.HopChanged(Hop(11, true)));
        source.Apply(new TraceUpdate.HopChanged(Hop(12, true)));
        source.Apply(new TraceUpdate.HopChanged(Dest(12)));
        source.Apply(new TraceUpdate.HopChanged(Dest(11)));
        source.Apply(new TraceUpdate.HopRemoved(12));

        var batch = DiagramEdit.Coalesce(edits);
        Assert.That(batch.Select(DiagramEdit.IdOf).Any(id => id is not null && id.EndsWith("-12")), Is.False);
        Assert.That(batch.OfType<DiagramEdit.AddNode>().Single().Node.Id, Is.EqualTo(TracerouteDiagramSource.HopId(11)));
    }

    [Test]
    public void Coalescing_keeps_a_remove_of_a_cell_added_in_an_earlier_batch()
    {
        var update = new DiagramEdit.Update("a", Label: "x");
        var batch  = DiagramEdit.Coalesce([update, new DiagramEdit.Remove("a"), new DiagramEdit.Remove("b")]);

        Assert.That(batch.Select(DiagramEdit.IdOf).ToArray(), Is.EqualTo(new[] { "a", "b" }));
        Assert.That(batch.All(e => e is DiagramEdit.Remove), Is.True);
    }

    [Test]
    public void The_diff_updates_in_place_and_asks_for_a_rebuild_when_it_cannot()
    {
        var a = new DiagramModel();
        a.Nodes.Add(new DiagramNode("n", "L", 0, 0, 10, 10, "rounded=1;fillColor=#fff;", new Dictionary<string, string> { ["k"] = "1" }));
        a.Edges.Add(new DiagramEdge("e", "n", "n", "", "dashed=1;"));

        var b = new DiagramModel();
        b.Nodes.Add(new DiagramNode("n", "L", 5, 0, 10, 10, "rounded=1;strokeColor=#000;", new Dictionary<string, string> { ["k"] = "2" }));
        b.Edges.Add(new DiagramEdge("e", "n", "n", "", "dashed=0;"));

        var edits  = DiagramDiff.Between(a, b)!;
        var update = edits.OfType<DiagramEdit.Update>().Single();
        Assert.That(update.Bounds, Is.EqualTo(new DiagramBounds(5, 0, 10, 10)));
        Assert.That(update.Style!["fillColor"], Is.Null);
        Assert.That(update.Style!["strokeColor"], Is.EqualTo("#000"));
        Assert.That(update.Attributes!["k"], Is.EqualTo("2"));
        Assert.That(edits.OfType<DiagramEdit.Remove>().Single().Id, Is.EqualTo("e"));
        Assert.That(edits.OfType<DiagramEdit.AddEdge>().Single().Edge.Style, Is.EqualTo("dashed=0;"));
        Assert.That(DiagramDiff.Between(a, a), Is.Empty);

        var moved = new DiagramModel();
        moved.Nodes.Add(a.Nodes[0] with { Parent = "box" });
        Assert.That(DiagramDiff.Between(a, moved), Is.Null);
    }

    [Test]
    public void Freezing_keeps_the_values_and_drops_the_bindings()
    {
        var source = Traced(false, Named(Router(1), "gw.lan"));

        var doc = DrawioDocument.CreateEmpty();
        doc.AddModel(source.Build(), true, true);
        DiagramBindings.Freeze(doc);
        string xml = doc.ToXml();

        Assert.That(xml, Does.Not.Contain("silo-"));
        Assert.That(xml, Does.Not.Contain(DrawioDocument.GeneratedAttribute));
        Assert.That(xml, Does.Not.Contain("movable=0"));
        Assert.That(xml, Does.Contain("10.0.0.1"));
        Assert.That(xml, Does.Contain("gw.lan"));
        Assert.That(xml, Does.Contain("strokeColor=light-dark(#82b366,#97d077)")); // named: green
    }

    [Test]
    public void Diagram_values_are_one_set_per_scheme_and_notify_once_per_call()
    {
        var values  = DiagramValues.For("trace-test");
        int changes = 0;
        values.Changed += () => changes++;

        Assert.That(DiagramValues.For("TRACE-TEST"), Is.SameAs(values));

        values.SetMany(new Dictionary<string, object?> { ["a"] = 1, ["b"] = "x" });
        values.Set("a", 1); // unchanged
        values.RemovePrefix("b");

        Assert.That(changes, Is.EqualTo(2));
        Assert.That(DiagramBindings.Expand("{trace-test:a}/{trace-test:b}", false), Is.EqualTo("1/?"));
        values.Clear();
    }

    // ---- colours -----------------------------------------------------------------------------------

    [Test]
    public void Hops_are_coloured_by_what_they_are()
    {
        var pending = Hop(1, true);
        var named   = Named(Hop(1, false, (IPStatus.TtlExpired, "10.0.0.1", 3)), "r1.example");
        var unnamed = Hop(2, false, (IPStatus.TtlExpired, "10.0.0.2", 5));
        var silent  = Silent(3);
        var after   = Hop(4, false, (IPStatus.TtlExpired, "10.0.0.4", 9));
        var dest    = Hop(5, false, (IPStatus.Success, "93.184.215.14", 12));
        var waiting = Hop(4, true);
        var unreach = Hop(4, false, (IPStatus.DestinationHostUnreachable, "10.0.0.4", 9));

        Assert.That(TracePalette.RoleOf(pending, [pending], false), Is.EqualTo(TraceHopRole.Pending));
        Assert.That(TracePalette.RoleOf(named, [named], false), Is.EqualTo(TraceHopRole.Named));
        Assert.That(TracePalette.RoleOf(unnamed, [unnamed], false), Is.EqualTo(TraceHopRole.Unnamed));
        Assert.That(TracePalette.RoleOf(dest, [dest], true), Is.EqualTo(TraceHopRole.Destination));
        Assert.That(TracePalette.RoleOf(unreach, [unreach], true), Is.EqualTo(TraceHopRole.Failing));

        Assert.That(TracePalette.RoleOf(silent, [silent, after], false), Is.EqualTo(TraceHopRole.Missing));
        Assert.That(TracePalette.RoleOf(silent, [silent, waiting], false), Is.EqualTo(TraceHopRole.Missing));
        Assert.That(TracePalette.RoleOf(silent, [silent], false), Is.EqualTo(TraceHopRole.Missing));
        Assert.That(TracePalette.RoleOf(silent, [silent], true), Is.EqualTo(TraceHopRole.Failing));
        Assert.That(TracePalette.RoleOf(silent, [silent, Silent(4)], false), Is.EqualTo(TraceHopRole.Failing));

        Assert.That(TracePalette.Stroke(TraceHopRole.Destination), Is.EqualTo(TracePalette.EndStroke));
        Assert.That(TracePalette.Stroke(TraceHopRole.Named), Does.Contain("#82b366"));
        Assert.That(TracePalette.Stroke(TraceHopRole.Unnamed), Does.Contain("#6c8ebf"));
        Assert.That(TracePalette.Stroke(TraceHopRole.Pending), Does.Contain("#ffffff"));
        Assert.That(TracePalette.Stroke(TraceHopRole.Missing), Does.Contain("#999999"));
        Assert.That(TracePalette.Stroke(TraceHopRole.Failing), Does.Contain("#b85450"));
        Assert.That(TracePalette.Fill(TraceHopRole.Named), Is.EqualTo("none"));
    }

    [Test]
    public void Silent_hops_at_the_end_turn_red_when_the_trace_gives_up()
    {
        var source = Traced(false, Router(1), Silent(2));
        Assert.That(DiagramBindings.Expand("{trace:h2.stroke}", false), Does.Contain("#999999"));

        source.Apply(new TraceUpdate.Finished(new TraceResult(TraceOutcome.MaxHopsExceeded, 2, TimeSpan.FromSeconds(1))));
        Assert.That(DiagramBindings.Expand("{trace:h2.stroke}", false), Does.Contain("#b85450"));
        Assert.That(DiagramBindings.Expand("{trace:h1.stroke}", false), Does.Contain("#6c8ebf"));
    }

    // ---- collapsing silent hops ----------------------------------------------------------------------

    [Test]
    public void With_collapsing_a_run_is_one_collapsed_box_holding_its_row()
    {
        var m   = Traced(true, Router(1), Silent(2), Silent(3), Silent(4), Router(5), Silent(6), Dest(7)).Build();
        var gap = Node(m, TracerouteDiagramSource.GapId(2));

        Assert.That(gap.Fold, Is.Not.Null);
        Assert.That(gap.Fold!.Collapsed, Is.True);
        Assert.That(gap.Label, Does.Contain("×3"));
        Assert.That(gap.Fold.Width, Is.GreaterThan(gap.Width));

        foreach (int n in new[] { 2, 3, 4 }) Assert.That(Node(m, TracerouteDiagramSource.HopId(n)).Parent, Is.EqualTo(gap.Id));
        Assert.That(Node(m, TracerouteDiagramSource.HopId(6)).Parent, Is.Null);         // a single silent hop does not fold
        Assert.That(m.Nodes.FindIndex(x => x.Id == gap.Id), Is.LessThan(m.Nodes.FindIndex(x => x.Id == TracerouteDiagramSource.HopId(2))));
        Assert.That(EdgeOf(m, TracerouteDiagramSource.EdgeId(3)).Parent, Is.EqualTo(gap.Id));
        Assert.That(EdgeOf(m, TracerouteDiagramSource.EdgeId(2)).Parent, Is.Null);
        Assert.That(EdgeOf(m, TracerouteDiagramSource.EdgeId(5)).Parent, Is.Null);

        var plain = Traced(false, Router(1), Silent(2), Silent(3), Silent(4), Router(5), Silent(6), Dest(7)).Build();
        Assert.That(plain.Nodes.Any(x => x.Fold is not null || x.Parent is not null), Is.False);
    }

    [Test]
    public void A_run_forming_under_collapsing_rebuilds_instead_of_editing()
    {
        var source  = Traced(true);
        int rebuilt = 0;
        source.Changed += () => rebuilt++;

        source.Apply(new TraceUpdate.HopChanged(Silent(1)));
        source.Apply(new TraceUpdate.HopChanged(Hop(2, true)));
        Assert.That(rebuilt, Is.EqualTo(0));

        source.Apply(new TraceUpdate.HopChanged(Silent(2))); // 1-2 is now a run: boxes change container
        Assert.That(rebuilt, Is.EqualTo(1));

        source.CollapseSilent = false;
        Assert.That(rebuilt, Is.EqualTo(2));
    }

    [Test]
    public void A_box_the_person_expanded_stays_expanded_across_a_rebuild()
    {
        var source = Traced(true, Router(1), Silent(2), Silent(3));
        var doc    = DrawioDocument.CreateEmpty();
        doc.AddModel(source.Build(), true, true);

        string gap = TracerouteDiagramSource.GapId(2);
        Assert.That(doc.GeneratedFoldStates()[gap], Is.True);
        doc.ApplyFoldStates(new Dictionary<string, bool> { [gap] = false }); // what clicking + in draw.io does
        var expanded = doc.GeneratedFoldStates();
        double wide = double.Parse(DrawioDocument.CellOf(doc.Find(gap)!).Element("mxGeometry")!.Attribute("width")!.Value, System.Globalization.CultureInfo.InvariantCulture);

        // the run grows: rebuild, carrying the fold over
        source.Apply(new TraceUpdate.HopChanged(Silent(4)));
        doc.RemoveGenerated();
        doc.AddModel(source.Build(), true, true);
        doc.ApplyFoldStates(expanded);

        Assert.That(doc.GeneratedFoldStates()[gap], Is.False);
        double width = double.Parse(DrawioDocument.CellOf(doc.Find(gap)!).Element("mxGeometry")!.Attribute("width")!.Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.That(width, Is.GreaterThan(wide));
    }

    [Test]
    public void A_rebuild_can_keep_where_the_person_moved_a_box_and_leave_out_what_they_deleted()
    {
        // what DrawioView.Compose does with the cells the plugin reported as moved / deleted
        var source = Traced(false, Router(1), Router(2), Router(3));
        var doc    = DrawioDocument.CreateEmpty();
        doc.AddModel(source.Build(), true, false);

        string moved = TracerouteDiagramSource.HopId(1);
        var    g     = doc.GeometryOf(moved)!;
        g.SetAttributeValue("x", "999");
        doc.SetGeometry(moved, g);                 // the person dragged hop 1
        var kept = doc.GeometryOf(moved)!;

        source.Apply(new TraceUpdate.HopChanged(Router(4)));
        doc.RemoveGenerated();
        doc.AddModel(source.Build(), true, false);
        Assert.That(doc.GeometryOf(moved)!.Attribute("x")!.Value, Is.Not.EqualTo("999"));

        doc.SetGeometry(moved, kept);
        doc.Remove(TracerouteDiagramSource.HopId(2)); // the person deleted hop 2: its connectors go too

        Assert.That(doc.GeometryOf(moved)!.Attribute("x")!.Value, Is.EqualTo("999"));
        Assert.That(doc.Find(TracerouteDiagramSource.HopId(2)), Is.Null);
        Assert.That(doc.Find(TracerouteDiagramSource.EdgeId(2)), Is.Null);
        Assert.That(doc.Find(TracerouteDiagramSource.EdgeId(3)), Is.Null);
        Assert.That(doc.Find(TracerouteDiagramSource.HopId(3)), Is.Not.Null);
        Assert.That(doc.ToXml(), Does.Not.Contain("movable=0"));
    }
}
