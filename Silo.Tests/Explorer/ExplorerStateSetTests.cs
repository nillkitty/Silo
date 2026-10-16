using System.Linq;
using Silo.Contracts.Providers.Data;
using Silo.Explorer;

namespace Silo.Tests.Explorer;

public class ExplorerStateSetTests
{
    private static NodeStateRecord Rec(string key, bool expanded = true, bool open = false, bool selected = false)
        => new(key, key, expanded, open, selected);

    private static ExplorerStateSet With(params NodeStateRecord[] records)
    {
        var set = new ExplorerStateSet();
        set.Load(records);
        return set;
    }

    [Test]
    public void A_node_takes_its_state_once()
    {
        var set = With(Rec("a", open: true));

        Assert.That(set.Take("a"), Is.EqualTo(Rec("a", open: true)));
        Assert.That(set.Take("a"), Is.Null);
        Assert.That(set.IsWaiting("a"), Is.False);
    }

    [Test]
    public void A_node_with_nothing_remembered_gets_null()
    {
        Assert.That(With(Rec("a")).Take("b"), Is.Null);
    }

    [Test]
    public void Loading_starts_over_and_the_last_record_for_a_key_wins()
    {
        var set = With(Rec("a"), Rec("b"));
        set.Load([Rec("c", expanded: false), Rec("c", expanded: true)]);

        Assert.That(set.Count,                  Is.EqualTo(1));
        Assert.That(set.Take("c")!.Expanded,    Is.True);
        Assert.That(set.IsWaiting("a"),         Is.False);
    }

    [Test]
    public void Merge_puts_the_live_state_first_and_adds_what_is_still_waiting()
    {
        var set = With(Rec("waiting"), Rec("live", expanded: false));

        var merged = set.Merge([Rec("live", expanded: true), Rec("new")]);

        Assert.That(merged.Select(r => r.Key), Is.EqualTo(new[] { "live", "new", "waiting" }));
        Assert.That(merged.First(r => r.Key == "live").Expanded, Is.True, "the live state replaces the remembered one");
    }

    [Test]
    public void Merge_ignores_a_repeated_live_key()
    {
        var merged = new ExplorerStateSet().Merge([Rec("a", expanded: true), Rec("a", expanded: false)]);

        Assert.That(merged, Has.Count.EqualTo(1));
        Assert.That(merged.Single().Expanded, Is.True);
    }

    [Test]
    public void Merge_does_not_consume_what_is_waiting()
    {
        var set = With(Rec("a"));
        set.Merge([]);

        Assert.That(set.IsWaiting("a"), Is.True);
    }

    [Test]
    public void Dropping_forgets_the_waiting_nodes_that_no_prefix_protects()
    {
        var set = With(Rec("node/silo/gone"), Rec("node/silo/file:///c/a"), Rec("node/silo/file:///c/a/sub"), Rec("other"));

        int dropped = set.DropUnmatched(["node/silo/file:///c/a"]);

        Assert.That(dropped, Is.EqualTo(2));
        Assert.That(set.IsWaiting("node/silo/file:///c/a"),     Is.True);
        Assert.That(set.IsWaiting("node/silo/file:///c/a/sub"), Is.True);
        Assert.That(set.IsWaiting("node/silo/gone"),            Is.False);
        Assert.That(set.IsWaiting("other"),                     Is.False);
    }

    [Test]
    public void Dropping_with_no_prefixes_forgets_everything_waiting()
    {
        var set = With(Rec("a"), Rec("b"));

        Assert.That(set.DropUnmatched([]), Is.EqualTo(2));
        Assert.That(set.Count,             Is.Zero);
    }

    [Test]
    public void Keys_are_compared_exactly()
    {
        Assert.That(With(Rec("Node/A")).Take("node/a"), Is.Null);
    }
}
