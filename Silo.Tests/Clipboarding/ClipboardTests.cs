using Silo.Clipboarding;
using Silo.Contracts.UI;

namespace Silo.Tests.Clipboarding;

public class ClipboardTests
{
    private static readonly List<string> Reported = [];

    private static void Report(Exception ex, string what)
    {
        Reported.Add(what);
    }

    [SetUp]
    public void Reset()
    {
        Reported.Clear();
    }

    // ---- writer --------------------------------------------------------------------------------

    [Test]
    public void Writer_keeps_formats_in_the_order_offered_and_a_replacement_keeps_its_place()
    {
        var w = new ClipboardWriter(ClipboardOperation.Copy);
        w.Set(ClipboardFormats.Html, "<b>x</b>");
        w.Set(ClipboardFormats.Text, "x");
        w.Set(ClipboardFormats.Html, "<i>x</i>");

        Assert.That(w.Formats,                    Is.EqualTo(new[] { ClipboardFormats.Html, ClipboardFormats.Text }));
        Assert.That(w.Get(ClipboardFormats.Html), Is.EqualTo("<i>x</i>"));
    }

    [Test]
    public void Writer_format_names_ignore_case_and_null_data_removes_a_format()
    {
        var w = new ClipboardWriter(ClipboardOperation.Copy);
        w.Set("Foo", 1);

        Assert.That(w.Contains("foo"), Is.True);
        w.Set("FOO", null);
        Assert.That(w.Contains("Foo"), Is.False);
        Assert.That(w.Formats,         Is.Empty);
        Assert.Throws<ArgumentException>(() => w.Set(" ", 1));
    }

    [Test]
    public void Lazy_formats_render_once_and_only_when_asked()
    {
        int renders = 0;
        var w       = new ClipboardWriter(ClipboardOperation.Copy);
        w.SetLazy(ClipboardFormats.Csv, () => ++renders);

        Assert.That(w.HasLazy,                         Is.True);
        Assert.That(w.IsPending(ClipboardFormats.Csv), Is.True);
        Assert.That(renders,                           Is.EqualTo(0));
        Assert.That(w.Get(ClipboardFormats.Csv),       Is.EqualTo(1));
        Assert.That(w.Get(ClipboardFormats.Csv),       Is.EqualTo(1));
        Assert.That(renders,                           Is.EqualTo(1));
        Assert.That(w.HasLazy,                         Is.False);
    }

    [Test]
    public void A_render_that_throws_is_reported_and_the_format_has_no_data()
    {
        var w = new ClipboardWriter(ClipboardOperation.Copy, Report);
        w.SetLazy("X", () => throw new InvalidOperationException("boom"));

        Assert.That(w.Get("X"), Is.Null);
        Assert.That(Reported,   Has.Count.EqualTo(1));
        Assert.That(w.Get("X"), Is.Null);
        Assert.That(Reported,   Has.Count.EqualTo(1));
    }

    // ---- collecting ----------------------------------------------------------------------------

    [Test]
    public void Copy_needs_a_selection_and_every_object_agreeing()
    {
        Assert.That(ClipboardCollector.CanCopy([]),                                                 Is.False);
        Assert.That(ClipboardCollector.CanCopy([new Item("a")]),                                    Is.True);
        Assert.That(ClipboardCollector.CanCopy([new Item("a"), new Item("b") { Allowed = false }]), Is.False);
    }

    [Test]
    public void Cut_needs_every_object_to_be_cuttable()
    {
        Assert.That(ClipboardCollector.CanCut([new CutItem("a")]),                        Is.True);
        Assert.That(ClipboardCollector.CanCut([new CutItem("a"), new Item("b")]),         Is.False);
        Assert.That(ClipboardCollector.CanCut([new CutItem("a") { CutAllowed = false }]), Is.False);
    }

    [Test]
    public void Several_objects_offer_to_the_same_writer_in_selection_order()
    {
        var a = new Item("a");
        var b = new Item("b");
        var r = ClipboardCollector.Collect(ClipboardOperation.Copy, [a, b]);

        Assert.That(r.Sources,                           Is.EqualTo(new[] { a, b }));
        Assert.That(r.Writer.Get(ClipboardFormats.Text), Is.EqualTo("b")); // the later object replaced the format
        Assert.That(r.Writer.Get(ClipboardFormats.Json), Is.EqualTo("{\"name\":\"b\"}"));
    }

    [Test]
    public void A_cut_asks_for_the_cut_formats_and_tells_the_writer_it_is_a_cut()
    {
        var c = new CutItem("a") { CutText = "moving" };
        var r = ClipboardCollector.Collect(ClipboardOperation.Cut, [c]);

        Assert.That(r.Writer.Operation,                  Is.EqualTo(ClipboardOperation.Cut));
        Assert.That(r.Writer.Get(ClipboardFormats.Text), Is.EqualTo("moving"));
    }

    [Test]
    public void An_object_that_takes_the_operation_over_is_not_a_source()
    {
        var mine  = new Item("mine") { TakeOver = true };
        var other = new Item("other");
        var r     = ClipboardCollector.Collect(ClipboardOperation.Copy, [mine, other]);

        Assert.That(r.Handled, Is.EqualTo(new[] { mine }));
        Assert.That(r.Sources, Is.EqualTo(new[] { other }));
    }

    [Test]
    public void An_object_that_throws_is_reported_and_left_out()
    {
        var bad  = new Item("bad") { ThrowOnCopy = true };
        var good = new Item("good");
        var r    = ClipboardCollector.Collect(ClipboardOperation.Copy, [bad, good], Report);

        Assert.That(r.Sources, Is.EqualTo(new[] { good }));
        Assert.That(Reported,  Has.Count.EqualTo(1));
    }

    [Test]
    public void An_object_that_adds_no_formats_is_still_copyable_inside_the_app()
    {
        var session = new ClipboardSession();
        var result  = session.Begin(ClipboardOperation.Copy, [new Silent()]);

        Assert.That(result.Success,           Is.True);
        Assert.That(result.Snapshot!.Sources, Has.Count.EqualTo(1));
        Assert.That(result.Snapshot.Formats,  Is.EqualTo(new[] { ClipboardFormats.SiloItems }));
    }

    // ---- snapshot ------------------------------------------------------------------------------

    [Test]
    public void The_snapshot_answers_for_its_formats_and_the_in_app_items()
    {
        var item = new Item("a");
        var r    = ClipboardCollector.Collect(ClipboardOperation.Copy, [item]);
        var snap = new ClipboardSnapshot(ClipboardOperation.Copy, r.Sources, r.Writer);

        Assert.That(snap.Contains(ClipboardFormats.Text),   Is.True);
        Assert.That(snap.Contains(ClipboardFormats.Bitmap), Is.False);
        Assert.That(snap.Get(ClipboardFormats.Text),        Is.EqualTo("a"));
        IClipboardData data = snap; // the typed Get is an interface default member
        Assert.That(data.Get<string>(ClipboardFormats.Text),   Is.EqualTo("a"));
        Assert.That(data.Get<int>(ClipboardFormats.Text),      Is.EqualTo(0));
        Assert.That(snap.Contains(ClipboardFormats.SiloItems), Is.True);
        Assert.That(snap.Get(ClipboardFormats.Bitmap),         Is.Null);
        Assert.That(snap.Formats,                              Does.Contain(ClipboardFormats.SiloItems));
    }

    [Test]
    public void A_plain_paste_after_a_cut_moves_and_after_a_copy_duplicates()
    {
        var w = new ClipboardWriter(ClipboardOperation.Copy);

        IClipboardData cut  = new ClipboardSnapshot(ClipboardOperation.Cut,  [], w);
        IClipboardData copy = new ClipboardSnapshot(ClipboardOperation.Copy, [], w);

        Assert.That(cut.DefaultMode,  Is.EqualTo(PasteMode.Move));
        Assert.That(copy.DefaultMode, Is.EqualTo(PasteMode.Duplicate));
    }

    [Test]
    public void WithOperation_is_the_same_copy_seen_differently()
    {
        var item = new Item("a");
        var r    = ClipboardCollector.Collect(ClipboardOperation.Copy, [item]);
        var snap = new ClipboardSnapshot(ClipboardOperation.Copy, r.Sources, r.Writer);
        var cut  = snap.WithOperation(ClipboardOperation.Cut);

        Assert.That(cut.Token,                                   Is.EqualTo(snap.Token));
        Assert.That(cut.Operation,                               Is.EqualTo(ClipboardOperation.Cut));
        Assert.That(cut.Sources,                                 Is.EqualTo(snap.Sources));
        Assert.That(snap.WithOperation(ClipboardOperation.Copy), Is.SameAs(snap));
    }

    // ---- pasting -------------------------------------------------------------------------------

    [Test]
    public void The_default_can_paste_wants_in_app_objects_or_a_known_format_and_refuses_moving_a_copy()
    {
        var       f       = new DefaultFolder();
        var       foreign = new Foreign(ClipboardFormats.Csv);
        var       other   = new Foreign(ClipboardFormats.Bitmap);
        IPastable p       = f;

        Assert.That(p.CanPaste(foreign, PasteMode.Duplicate), Is.True);
        Assert.That(p.CanPaste(other,   PasteMode.Duplicate), Is.False);
        Assert.That(p.CanPaste(foreign, PasteMode.Move),      Is.False);
    }

    [Test]
    public void A_paste_goes_to_the_first_container_that_takes_it()
    {
        var inner = new Folder("inner");
        var outer = new Folder("outer");
        var o     = ClipboardPaster.Paste(new Foreign(ClipboardFormats.Text), PasteMode.Duplicate, [inner, outer]);

        Assert.That(o.Target,     Is.SameAs(inner));
        Assert.That(outer.Pastes, Is.Empty);
    }

    [Test]
    public void A_container_that_declines_passes_it_on_to_the_one_around_it()
    {
        var inner = new Folder("inner", PasteEffect.None);
        var outer = new Folder("outer");
        var o     = ClipboardPaster.Paste(new Foreign(ClipboardFormats.Text), PasteMode.Duplicate, [inner, outer]);

        Assert.That(o.Target,     Is.SameAs(outer));
        Assert.That(inner.Pastes, Has.Count.EqualTo(1));
    }

    [Test]
    public void A_container_that_refuses_or_throws_is_skipped()
    {
        var refuses = new Folder("a") { Refuse = true };
        var throws  = new Folder("b") { Throw  = true };
        var ok      = new Folder("c");
        var o       = ClipboardPaster.Paste(new Foreign(ClipboardFormats.Text), PasteMode.Duplicate, [refuses, throws, ok], Report);

        Assert.That(o.Target,                                                                                       Is.SameAs(ok));
        Assert.That(refuses.Pastes,                                                                                 Is.Empty);
        Assert.That(Reported,                                                                                       Has.Count.EqualTo(1));
        Assert.That(ClipboardPaster.Paste(new Foreign(ClipboardFormats.Bitmap), PasteMode.Duplicate, [ok]).Handled, Is.False);
    }

    [Test]
    public void An_item_is_never_pasted_into_itself()
    {
        var session = new ClipboardSession();
        var node    = new Both("node");
        var begin   = session.Begin(ClipboardOperation.Copy, [node]);
        var inner   = new Folder("inner");
        var o       = session.Paste(begin.Snapshot!, null, [node, inner]);

        Assert.That(o.Target,                                        Is.SameAs(inner));
        Assert.That(session.CanPaste(begin.Snapshot!, null, [node]), Is.False);
    }

    // ---- the session ---------------------------------------------------------------------------

    [Test]
    public void Copy_then_paste_duplicates_and_leaves_the_original_alone()
    {
        var session = new ClipboardSession();
        var item    = new CutItem("a");
        var folder  = new Folder("f");
        var begin   = session.Begin(ClipboardOperation.Copy, [item]);

        Assert.That(session.HasPendingCut, Is.False);
        var outcome = session.Paste(begin.Snapshot!, null, [folder]);

        Assert.That(outcome.Handled,        Is.True);
        Assert.That(folder.Pastes[0].Item2, Is.EqualTo(PasteMode.Duplicate));
        Assert.That(item.Completed,         Is.Empty);
        Assert.That(session.Current,        Is.SameAs(begin.Snapshot), "a copy can be pasted again");
    }

    [Test]
    public void Cut_then_paste_moves_and_completes_the_cut_once()
    {
        var session = new ClipboardSession();
        var item    = new CutItem("a");
        var folder  = new Folder("f", PasteEffect.Moved);
        var begin   = session.Begin(ClipboardOperation.Cut, [item]);

        Assert.That(session.HasPendingCut, Is.True);
        Assert.That(item.Completed,        Is.Empty, "nothing happens at the moment of the cut");

        var outcome = session.Paste(begin.Snapshot!, null, [folder]);

        Assert.That(folder.Pastes[0].Item2,   Is.EqualTo(PasteMode.Move));
        Assert.That(item.Completed,           Has.Count.EqualTo(1));
        Assert.That(item.Completed[0].Effect, Is.EqualTo(PasteEffect.Moved));
        Assert.That(session.HasPendingCut,    Is.False);
        Assert.That(session.Current,          Is.Null);
        Assert.That(outcome.Handled,          Is.True);
    }

    [Test]
    public void A_cut_pasted_as_copies_tells_the_original_to_remove_itself()
    {
        var session = new ClipboardSession();
        var item    = new CutItem("a");
        var begin   = session.Begin(ClipboardOperation.Cut, [item]);

        session.Paste(begin.Snapshot!, null, [new Folder("f")]);

        Assert.That(item.Completed.Single().Effect, Is.EqualTo(PasteEffect.Copied));
    }

    [Test]
    public void A_cut_that_nobody_took_stays_pending()
    {
        var session = new ClipboardSession();
        var item    = new CutItem("a");
        var begin   = session.Begin(ClipboardOperation.Cut, [item]);

        Assert.That(session.Paste(begin.Snapshot!, null, [new Folder("f", PasteEffect.None)]).Handled, Is.False);
        Assert.That(session.HasPendingCut,                                                             Is.True);
        Assert.That(item.Completed,                                                                    Is.Empty);
    }

    [Test]
    public void Pasting_a_cut_as_a_duplicate_does_not_complete_it()
    {
        var session = new ClipboardSession();
        var item    = new CutItem("a");
        var begin   = session.Begin(ClipboardOperation.Cut, [item]);

        session.Paste(begin.Snapshot!, PasteMode.Duplicate, [new Folder("f")]);

        Assert.That(item.Completed,        Is.Empty);
        Assert.That(session.HasPendingCut, Is.True);
    }

    [Test]
    public void A_new_copy_or_cut_cancels_the_cut_before_it()
    {
        var session = new ClipboardSession();
        var first   = new CutItem("a");
        session.Begin(ClipboardOperation.Cut, [first]);

        session.Begin(ClipboardOperation.Copy, [new Item("b")]);

        Assert.That(first.Cancelled,       Is.EqualTo(1));
        Assert.That(session.HasPendingCut, Is.False);
    }

    [Test]
    public void A_copy_that_cannot_be_made_leaves_a_pending_cut_alone()
    {
        var session = new ClipboardSession();
        var first   = new CutItem("a");
        session.Begin(ClipboardOperation.Cut, [first]);

        Assert.That(session.Begin(ClipboardOperation.Copy, []).Success, Is.False);
        Assert.That(first.Cancelled,                                    Is.EqualTo(0));
        Assert.That(session.HasPendingCut,                              Is.True);
    }

    [Test]
    public void Abandon_cancels_the_cut_and_forgets_the_copy()
    {
        var session = new ClipboardSession();
        var item    = new CutItem("a");
        session.Begin(ClipboardOperation.Cut, [item]);

        session.Abandon();

        Assert.That(item.Cancelled,  Is.EqualTo(1));
        Assert.That(session.Current, Is.Null);
        session.Abandon();
        Assert.That(item.Cancelled, Is.EqualTo(1), "only once");
    }

    [Test]
    public void An_object_that_runs_the_operation_itself_leaves_nothing_for_the_host()
    {
        var session = new ClipboardSession();
        var result  = session.Begin(ClipboardOperation.Copy, [new Item("a") { TakeOver = true }]);

        Assert.That(result.Success,        Is.True);
        Assert.That(result.Snapshot,       Is.Null);
        Assert.That(result.HandledByItems, Is.EqualTo(1));
        Assert.That(session.Current,       Is.Null);
    }

    [Test]
    public void A_cut_of_something_that_cannot_be_cut_does_nothing()
    {
        var session = new ClipboardSession();

        Assert.That(session.Begin(ClipboardOperation.Cut, [new Item("a")]).Success, Is.False);
        Assert.That(session.HasPendingCut,                                          Is.False);
    }

    // ---- dropping ------------------------------------------------------------------------------

    [Test]
    public void A_drop_that_moves_completes_the_dragged_objects_and_leaves_the_clipboard_alone()
    {
        var session = new ClipboardSession();
        var kept    = new Item("kept");
        session.Begin(ClipboardOperation.Copy, [kept]);

        var dragged = new CutItem("d");
        var drag    = new ClipboardSnapshot(ClipboardOperation.Copy, [dragged], new ClipboardWriter(ClipboardOperation.Copy));
        var folder  = new Folder("f", PasteEffect.Moved);

        var outcome = session.Drop(drag, PasteMode.Move, [folder]);

        Assert.That(outcome.Handled,                   Is.True);
        Assert.That(folder.Pastes[0].Item1.Operation,  Is.EqualTo(ClipboardOperation.Cut));
        Assert.That(dragged.Completed.Single().Effect, Is.EqualTo(PasteEffect.Moved));
        Assert.That(session.Current,                   Is.Not.Null);
    }

    [Test]
    public void A_drop_that_duplicates_leaves_the_dragged_objects_alone()
    {
        var session = new ClipboardSession();
        var dragged = new CutItem("d");
        var drag    = new ClipboardSnapshot(ClipboardOperation.Copy, [dragged], new ClipboardWriter(ClipboardOperation.Copy));
        var folder  = new Folder("f");

        session.Drop(drag, PasteMode.Duplicate, [folder]);

        Assert.That(folder.Pastes[0].Item1.Operation, Is.EqualTo(ClipboardOperation.Copy));
        Assert.That(dragged.Completed,                Is.Empty);
    }

    // ---- context -------------------------------------------------------------------------------

    [Test]
    public void A_context_joins_with_the_one_around_it()
    {
        var a  = new Item("a");
        var b  = new Item("b");
        var f1 = new Folder("1");
        var f2 = new Folder("2");

        var inner = new ClipboardContext([a], [f1]);
        var outer = new ClipboardContext([b], [f1, f2]);

        var joined = inner.Then(outer);
        Assert.That(joined.Selection,                             Is.EqualTo(new[] { a }));
        Assert.That(joined.Targets,                               Is.EqualTo(new IPastable[] { f1, f2 }));
        Assert.That(ClipboardContext.Empty.Then(outer).Selection, Is.EqualTo(new[] { b }));
    }
    // ---- fakes ---------------------------------------------------------------------------------

    private class Item(string name) : ICopyable
    {
        public bool   Allowed     { get; set; } = true;
        public string Name        { get; }      = name;
        public bool   TakeOver    { get; set; }
        public bool   ThrowOnCopy { get; set; }

        public bool CanCopy => Allowed;

        public void Copy(IClipboardWriter clipboard)
        {
            if (ThrowOnCopy) throw new InvalidOperationException("boom");

            clipboard.Set(ClipboardFormats.Text, Name);
            clipboard.SetLazy(ClipboardFormats.Json, () => $"{{\"name\":\"{Name}\"}}");
        }

        public bool TryHandleCopy(ClipboardRequest request)
        {
            return TakeOver;
        }
    }

    private sealed class CutItem(string name) : Item(name), ICuttable
    {
        public int               Cancelled  { get; private set; }
        public List<PasteResult> Completed  { get; }      = [];
        public bool              CutAllowed { get; set; } = true;
        public string?           CutText    { get; set; }

        public bool CanCut => CutAllowed;

        public void Cut(IClipboardWriter clipboard)
        {
            clipboard.Set(ClipboardFormats.Text, CutText ?? "cut " + Name);
        }

        public bool TryHandleCut(ClipboardRequest request)
        {
            return TakeOver;
        }

        public void CutCompleted(PasteResult result)
        {
            Completed.Add(result);
        }

        public void CutCancelled()
        {
            Cancelled++;
        }
    }

    private sealed class Folder(string name, PasteEffect effect = PasteEffect.Copied) : IPastable
    {
        public string                            Name   { get; } = name;
        public List<(IClipboardData, PasteMode)> Pastes { get; } = [];
        public bool                              Refuse { get; set; }
        public bool                              Throw  { get; set; }

        public IReadOnlyList<string> PasteFormats => [ClipboardFormats.Text];

        private static bool DefaultRule(IClipboardData data, PasteMode mode)
        {
            if (mode == PasteMode.Move && data.Operation != ClipboardOperation.Cut) return false;

            return data.Sources.Count > 0 || data.Contains(ClipboardFormats.Text);
        }

        public bool CanPaste(IClipboardData data, PasteMode mode)
        {
            if (Throw) throw new InvalidOperationException("boom");

            return !Refuse && DefaultRule(data, mode);
        }

        public PasteResult Paste(IClipboardData data, PasteMode mode)
        {
            Pastes.Add((data, mode));
            return effect == PasteEffect.None  ? PasteResult.None :
                   effect == PasteEffect.Moved ? PasteResult.Moved(data.Sources.Cast<object>().ToArray()) :
                                                 PasteResult.Copied(data.Sources.Cast<object>().ToArray());
        }
    }

    /// <summary>Uses the interface's own default <c>CanPaste</c>.</summary>
    private sealed class DefaultFolder : IPastable
    {
        public int Count { get; private set; }

        public IReadOnlyList<string> PasteFormats => [ClipboardFormats.Csv];

        public PasteResult Paste(IClipboardData data, PasteMode mode)
        {
            Count++;
            return PasteResult.Copied();
        }
    }

    private sealed class Both(string name) : ICopyable, IPastable
    {
        public string Name { get; } = name;

        public PasteResult Paste(IClipboardData data, PasteMode mode)
        {
            return PasteResult.Copied();
        }
    }

    private sealed class Silent : ICopyable;

    private sealed class Foreign(params string[] formats) : IClipboardData
    {
        public IReadOnlyCollection<string> Formats   => formats;
        public ClipboardOperation          Operation => ClipboardOperation.Copy;
        public IReadOnlyList<ICopyable>    Sources   => [];

        public bool Contains(string format)
        {
            return formats.Contains(format);
        }

        public object? Get(string format)
        {
            return Contains(format) ? "x" : null;
        }
    }
}