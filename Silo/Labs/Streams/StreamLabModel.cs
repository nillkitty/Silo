using System.Text;
using Newtonsoft.Json;

namespace Silo.Labs.Streams;

// The Stream Lab as a model (see LabModel.cs):
//   subject  a reel (StreamSetup)          parts   the source (with the chain of adapters nested under it), the post-processing stages, and the cuts
//   aspects  Properties, Adapters, Text, Cuts, Log (panes); Source, Adapter, Post and Cut for the active part
//   tools    Adapt (put an adapter in the chain) and Cutter (cut pieces out of a strip)
public sealed partial class StreamLabSession
{
    private const string SubjectId = "reel";

    private static string SpecJson(AdapterDef def, AdapterSide side = AdapterSide.Both) => JsonConvert.SerializeObject(new AdapterSpec { Key = def.Key, Arg = def.ArgDefault, Side = side });

    private static string Capability(AdapterDef def) => def.CanRead && def.CanWrite ? "Both ways" : def.CanRead ? "Reads only" : "Writes only";

    private static readonly LabPartType SourceType = new("source", "Source", "Reel", "Where the stream comes from");

    private static readonly LabPartType AdapterType = new("adapter", "Adapter", "Puzzle", "A stage of the chain the data goes through",
                                                          StreamAdapters.All.Select(a => new LabPrefab(a.Key, a.Title, SpecJson(a), a.Description, "Puzzle", Capability(a))).ToList(),
                                                          [
                                                              new LabTemplate("adapter", "Adapter with an argument", "{{key}}|{{arg}}|{{side}}",
                                                                              [new("key", "Which adapter", StreamAdapters.All[0].Key, StreamAdapters.All.Select(a => a.Key).ToList()), new("arg", "Its argument", ""), new("side", "On what is read, written or both", "both", ["both", "read", "write"])],
                                                                              "Any adapter, with the argument and side you give")
                                                          ]);

    private static readonly LabPartType PostType = new("post", "Post-processing", "Puzzle", "A stage applied to what was recorded (the Processed strip); the stream is not touched",
                                                       StreamAdapters.All.Where(a => a.CanRead).Select(a => new LabPrefab(a.Key, a.Title, SpecJson(a, AdapterSide.Read), a.Description, "Puzzle", Capability(a))).ToList(),
                                                       [
                                                           new LabTemplate("post", "Stage with an argument", "{{key}}|{{arg}}|read",
                                                                           [new("key", "Which adapter", StreamAdapters.All[0].Key, StreamAdapters.All.Where(a => a.CanRead).Select(a => a.Key).ToList()), new("arg", "Its argument", "")],
                                                                           "Any adapter that reads, with the argument you give")
                                                       ]);

    private static readonly LabPartType CutType = new("cut", "Cut", "TbCut", "A piece cut out of a strip",
                                                     null,
                                                     [
                                                         new LabTemplate("typed-cut", "Cut from typed bytes", "{{label}}|{{bytes}}", [new("label", "What it is called", "Typed"), new("bytes", "The bytes: text, hex:48 65 or b64:SGk=", "hex:48 65 6C 6C 6F")],
                                                                         "A cut made from bytes you type, not taken from a strip")
                                                     ]);

    protected override string SubjectNoun => "reel";

    // ---- subject: prefabs and templates ---------------------------------------------------------------

    protected override IEnumerable<LabSubjectType> DefineSubjectTypes()
    {
        var prefabs = StreamLibrary.Entries.Select(e =>
                                                   {
                                                       var setup = e.Make();
                                                       string group = setup.Source.Kind switch { StreamKind.Memory => "In memory", StreamKind.Tcp or StreamKind.Tls => "Connections", _ => "Files" };
                                                       return new LabPrefab(Slug(e.Name), e.Name, JsonConvert.SerializeObject(setup), e.Description, "Reel", group);
                                                   }).ToList();
        var templates = new List<LabTemplate>
                        {
                            new("memory", "Memory with text", "{{name}}|mem|{{text}}", [new("name", "What the reel is called", "Scratch"), new("text", "What it starts with (\\n and \\xHH allowed)", "Hello, reel!\\n")], "A memory stream holding some text", "Reel"),
                            new("connection", "Connection", "{{name}}|{{scheme}}|{{host}}|{{port}}",
                                [new("name", "What the reel is called", "Connection"), new("scheme", "Plain or with TLS", "tcp", ["tcp", "tls"]), new("host", "Host to connect to", "example.com"), new("port", "Port", "80")],
                                "A connection to a server, opened when you say so", "Reel"),
                            new("file", "File", "{{name}}|file|{{path}}", [new("name", "What the reel is called", "File"), new("path", "The file", "")], "A file read from the start", "Reel")
                        };
        yield return new LabSubjectType(SubjectId, "Reel", "Reel", "A stream with the adapters on it, what was read and written, and the pieces cut out", prefabs, templates);
    }

    private static string Slug(string name) => new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    protected override async Task OnNewSubjectAsync(LabSubjectType type, string content)
    {
        StreamSetup setup;
        if (content.TrimStart().StartsWith('{'))
        {
            setup = JsonConvert.DeserializeObject<StreamSetup>(content) ?? throw new InvalidOperationException("That is not a reel.");
        }
        else
        {
            var    f  = content.Split('|');
            string At(int i) => i < f.Length ? f[i].Trim() : string.Empty;

            StreamSource source = At(1).ToLowerInvariant() switch
                                  {
                                      "mem"          => StreamSource.ForMemory(Encoding.UTF8.GetBytes(StreamPayload.Unescape(f.Length > 2 ? string.Join('|', f.Skip(2)) : string.Empty))),
                                      "tcp" or "tls" => StreamSource.ForTcp(At(2), int.TryParse(At(3), out int port) ? port : 80, At(1).Equals("tls", StringComparison.OrdinalIgnoreCase)),
                                      "file"         => StreamSource.ForFile(At(2)),
                                      _              => StreamSource.TryParse(At(1), out var parsed) && parsed is not null ? parsed : throw new InvalidOperationException($"'{At(1)}' is not a way of naming a stream.")
                                  };
            setup = new StreamSetup { Name = At(0), Source = source, Step = Lab_.DefaultStep };
        }

        if (setup.Step < 1) setup.Step = Lab_.DefaultStep;
        await OpenLibrarySetup(setup, null);
    }

    // ---- parts: the source with its adapters, the post-processing stages, the cuts -------------------------------------

    protected override IEnumerable<LabPartType> DefinePartTypes() => [SourceType, AdapterType, PostType, CutType];

    protected override IEnumerable<LabPart> BuildParts()
    {
        string state = State switch { ReelState.Open => "open", ReelState.Opening => "opening", ReelState.Failed => "failed", _ => "closed" };
        var    source = new LabPart(SourceType, "source", _setup.Source.Label, _setup.Source, $"{_setup.Source}  ({state})");
        for (int i = 0; i < _setup.Adapters.Count; i++) source.Add(StagePart(AdapterType, "a:" + i, _setup.Adapters[i], i));
        yield return source;

        for (int i = 0; i < _setup.Post.Count; i++) yield return StagePart(PostType, "p:" + i, _setup.Post[i], i);
        for (int i = 0; i < _cuts.Count; i++) yield return new LabPart(CutType, "c:" + i, _cuts[i].Label, _cuts[i], $"{_cuts[i].Data.Length:N0} bytes");
    }

    private static LabPart StagePart(LabPartType type, string key, AdapterSpec spec, int index)
    {
        var    def    = StreamAdapters.Find(spec.Key);
        string detail = string.Join("  ", new[] { spec.Arg, spec.Side == AdapterSide.Both ? null : spec.Side == AdapterSide.Read ? "reads" : "writes", spec.Enabled ? null : "off" }.Where(s => !string.IsNullOrEmpty(s)));
        return new LabPart(type, key, $"{index + 1}. {def?.Title ?? spec.Key}", spec, detail.Length == 0 ? null : detail);
    }

    /// <summary>The person chose a stage or a cut (in a pane, on the deck): the active part follows.</summary>
    internal void SelectStage(object? stage)
    {
        string? key = null;
        switch (stage)
        {
            case StreamSource:
                key = "source";
                break;
            case AdapterSpec spec:
                int a = _setup.Adapters.IndexOf(spec);
                int p = _setup.Post.IndexOf(spec);
                key = a >= 0 ? "a:" + a : p >= 0 ? "p:" + p : null;
                break;
            case StreamCut cut:
                int c = _cuts.IndexOf(cut);
                key = c >= 0 ? "c:" + c : null;
                break;
        }

        if (key is not null) SetActivePart(key);
    }

    protected override void OnPartActivated(LabPart? part)
    {
        string? pane = part?.Type.Id switch { "source" => "properties", "adapter" => "adapters", "cut" => "cuts", _ => null };
        if (pane is not null) Host?.ShowInspector(pane);
    }

    protected override Task OnAddPartAsync(LabPartType type, LabPart? parent, string content)
    {
        switch (type.Id)
        {
            case "adapter":
                AddAdapter(ReadSpec(content, AdapterSide.Both));
                break;
            case "post":
                AddPost(ReadSpec(content, AdapterSide.Read));
                break;
            case "cut":
                var f = content.Split('|', 2);
                var data = StreamPayload.FromTyped(f.Length > 1 ? f[1] : f[0]);
                var cut = new StreamCut { Label = f.Length > 1 && f[0].Trim().Length > 0 ? f[0].Trim() : $"Cut {_cuts.Count + 1}", Origin = "typed", Data = data };
                _cuts.Add(cut);
                Note($"cut {data.Length:N0} byte(s) typed in");
                Touch();
                break;
            default:
                throw new InvalidOperationException($"A {type.Name.ToLowerInvariant()} cannot be added this way.");
        }

        return Task.CompletedTask;
    }

    /// <summary>An adapter from JSON, or from "key|argument|side".</summary>
    private static AdapterSpec ReadSpec(string content, AdapterSide defaultSide)
    {
        AdapterSpec spec;
        if (content.TrimStart().StartsWith('{'))
        {
            spec = JsonConvert.DeserializeObject<AdapterSpec>(content) ?? new AdapterSpec();
        }
        else
        {
            var    f  = content.Split('|');
            string At(int i) => i < f.Length ? f[i].Trim() : string.Empty;

            spec = new AdapterSpec { Key = At(0), Arg = At(1), Side = Enum.TryParse<AdapterSide>(At(2), true, out var side) ? side : defaultSide };
        }

        if (StreamAdapters.Find(spec.Key) is not { } def) throw new InvalidOperationException($"There is no adapter called '{spec.Key}'.");

        spec.Key = def.Key;
        if (StreamLabSession.ProblemOf(spec) is { } problem) throw new InvalidOperationException(problem);

        return spec;
    }

    // ---- aspects, tools and commands ------------------------------------------------------------------------

    private AdapterSpec? ActiveStage => ActivePart?.Tag as AdapterSpec;

    private bool IsPost(AdapterSpec spec) => _setup.Post.Contains(spec);

    private void ChangeStage(AdapterSpec spec, Action<AdapterSpec> change)
    {
        if (IsPost(spec)) ChangePost(spec, change);
        else ChangeAdapter(spec, change);
    }

    private void MoveStage(AdapterSpec spec, int delta)
    {
        if (IsPost(spec)) MovePost(spec, delta);
        else MoveAdapter(spec, delta);

        SelectStage(spec);
    }

    private void RemoveStage(AdapterSpec spec)
    {
        if (IsPost(spec)) RemovePost(spec);
        else RemoveAdapter(spec);

        SetActivePart(null);
    }

    private void SetStageArgument()
    {
        if (ActiveStage is not { } spec) return;

        var    def    = StreamAdapters.Find(spec.Key);
        string? answer = LabUi.Ask("Argument", def is { ArgHint.Length: > 0 } ? $"{def.Title} takes: {def.ArgHint}" : "The argument of the adapter:", spec.Arg);
        if (answer is not null) ChangeStage(spec, a => a.Arg = answer.Trim());
    }

    private ProvidedCommand SideCommand(string title, AdapterSide side) =>
        ProvidedCommand.From("stage-side-" + side.ToString().ToLowerInvariant(), title, () => { if (ActiveStage is { } s) ChangeStage(s, a => a.Side = side); }, null, () => ActiveStage is not null, "Which direction the stage works on") with
        {
            IsChecked = () => ActiveStage?.Side == side
        };

    private IReadOnlyList<ProvidedCommand> StageCommands() =>
    [
        ProvidedCommand.From("stage-enabled", "_On", () => { if (ActiveStage is { } s) ChangeStage(s, a => a.Enabled = !a.Enabled); }, null, () => ActiveStage is not null, "An adapter that is off stays in the chain but is left out") with { IsChecked = () => ActiveStage?.Enabled == true },
        ProvidedCommand.From("stage-argument", "Set _Argument...", SetStageArgument, null, () => ActiveStage is not null, "What the adapter is told (a level, a size, a key...)"),
        ProvidedCommand.Submenu("stage-side", "_Side", [SideCommand("_Both", AdapterSide.Both), SideCommand("_Read", AdapterSide.Read), SideCommand("_Write", AdapterSide.Write)], null, null, "Which direction the stage works on"),
        ProvidedCommand.From("stage-earlier", "Move _Earlier", () => { if (ActiveStage is { } s) MoveStage(s, -1); }, null, () => ActiveStage is not null, "Earlier in the order"),
        ProvidedCommand.From("stage-later", "Move _Later", () => { if (ActiveStage is { } s) MoveStage(s, 1); }, null, () => ActiveStage is not null, "Later in the order"),
        ProvidedCommand.From("stage-remove", "_Remove", () => { if (ActiveStage is { } s) RemoveStage(s); }, "TbDelete", () => ActiveStage is not null, "Take the stage away") with { IsDestructive = true }
    ];

    private StreamCut? ActiveCut => ActivePart?.Tag as StreamCut;

    protected override IEnumerable<LabAspect> DefineAspects()
    {
        var reel = LabOwner.OfSubjectType(SubjectId);
        yield return new LabAspect("properties", "Properties", reel) { IconKey = "DataModel", Description = "The stream's properties as they are now, and the ones that can be changed" };
        yield return new LabAspect("adapters", "Adapters", reel)
                     {
                         IconKey     = "Puzzle",
                         Description = "The chain of adapters the data goes through",
                         Commands =
                         [
                             ProvidedCommand.From("adapters-on", "Turn _All On", () => SetAllAdapters(true), null, () => _setup.Adapters.Any(a => !a.Enabled), "Put every adapter back in the chain"),
                             ProvidedCommand.From("adapters-off", "Turn All _Off", () => SetAllAdapters(false), null, () => _setup.Adapters.Any(a => a.Enabled), "Leave every adapter out, to see the data as it is"),
                             ProvidedCommand.From("adapters-clear", "_Clear Chain", ClearAdapters, "TbDelete", () => _setup.Adapters.Count > 0, "Take every adapter off") with { IsDestructive = true }
                         ]
                     };
        yield return new LabAspect("text", "Text", reel) { IconKey = "Document", Description = "The strip (or what is marked of it) as text in an encoding" };
        yield return new LabAspect("cuts", "Cuts", reel)
                     {
                         IconKey     = "TbCopy",
                         Description = "The pieces cut from the strips: put in order, joined, saved or written",
                         Commands =
                         [
                             ProvidedCommand.From("cuts-splice", "_Splice to New Reel", () => SpliceToNewReel(), "Reel", () => _cuts.Count > 0, "Join the cuts into a reel of their own, in a new window"),
                             ProvidedCommand.From("cuts-write", "_Write Cuts to Stream", () => _ = WriteAsync(Spliced(), "the cuts"), "Transfer1", () => _cuts.Count > 0 && CanWrite && !_busy, "Write the joined cuts through the adapters, at the position of the stream"),
                             ProvidedCommand.From("cuts-clear", "_Clear Cuts", () => { _cuts.Clear(); SetActivePart(null); Touch(); }, "TbDelete", () => _cuts.Count > 0, "Throw every cut away") with { IsDestructive = true }
                         ]
                     };
        yield return new LabAspect("log", "Log", reel)
                     {
                         IconKey     = "Document",
                         Description = "What was done, a line to a step",
                         Commands    = [ProvidedCommand.From("log-clear", "_Clear Log", () => { _log.Clear(); Touch(); }, null, () => _log.Count > 0, "Forget the lines of the log")]
                     };

        yield return new LabAspect("stage-source", "Source", LabOwner.OfPartType(SourceType.Id))
                     {
                         IconKey     = "Reel",
                         Description = "Where the stream comes from, and how much a read takes",
                         Commands =
                         [
                             ProvidedCommand.From("source-set", "Set _Source...", SetSource, "TbOpen", null, "Type a file, tcp://host:port, tls://host:port or mem:hex:..."),
                             ProvidedCommand.From("source-step", "Set S_tep...", SetStep, null, null, "How many bytes a read takes")
                         ]
                     };
        yield return new LabAspect("stage-adapter", "Adapter", LabOwner.OfPartType(AdapterType.Id)) { IconKey = "Puzzle", Description = "The active adapter of the chain", Commands = StageCommands() };
        yield return new LabAspect("stage-post", "Post-processing", LabOwner.OfPartType(PostType.Id)) { IconKey = "Puzzle", Description = "The active post-processing stage", Commands = StageCommands() };
        yield return new LabAspect("stage-cut", "Cut", LabOwner.OfPartType(CutType.Id))
                     {
                         IconKey     = "TbCut",
                         Description = "The active cut",
                         Commands =
                         [
                             ProvidedCommand.From("cut-rename", "Re_name...", RenameCut, null, () => ActiveCut is not null, "What the cut is called"),
                             ProvidedCommand.From("cut-earlier", "Move _Earlier", () => MoveActiveCut(-1), null, () => ActiveCut is not null, "Earlier in the order"),
                             ProvidedCommand.From("cut-later", "Move _Later", () => MoveActiveCut(1), null, () => ActiveCut is not null, "Later in the order"),
                             ProvidedCommand.From("cut-write", "_Write to Stream", () => { if (ActiveCut is { } c) _ = WriteAsync(c.Data, c.Label); }, "Transfer1", () => ActiveCut is not null && CanWrite && !_busy, "Write the cut through the adapters"),
                             ProvidedCommand.From("cut-new-reel", "Open as _New Reel", () => { if (ActiveCut is { } c) SpliceToNewReel([c]); }, "Reel", () => ActiveCut is not null, "Open the cut as a reel in a new window"),
                             ProvidedCommand.From("cut-copy-hex", "_Copy as Hex Dump", () => { if (ActiveCut is { } c) LabUi.Copy(HexDump.Format(c.Data)); }, "TbCopy", () => ActiveCut is not null, "Put the cut on the clipboard as a hex dump"),
                             ProvidedCommand.From("cut-remove", "_Remove", () => { if (ActiveCut is { } c) { RemoveCut(c); SetActivePart(null); } }, "TbDelete", () => ActiveCut is not null, "Throw the cut away") with { IsDestructive = true }
                         ]
                     };
    }

    protected override IEnumerable<LabTool> DefineTools()
    {
        var reel = LabOwner.OfSubjectType(SubjectId);

        var adapt = new List<ProvidedCommand>();
        foreach (var def in StreamAdapters.All)
        {
            var captured = def;
            adapt.Add(ProvidedCommand.From("adapt-" + def.Key, def.Title, () => AddFromTool(captured), null, null, def.Description, Capability(def)));
        }

        yield return new LabTool("adapt", "_Adapt", reel)
                     {
                         IconKey     = "Puzzle",
                         Description = "Put an adapter in the chain (or in post-processing)",
                         Parameters  = LabParameterSet.For(LabOf.Id, "tool:adapt", "Adapt", [new LabParameter("post", "As post-processing", LabParameterKind.Bool, false, "Apply the adapter to what was recorded (the Processed strip) instead of putting it on the stream.")]),
                         Commands    = adapt
                     };

        yield return new LabTool("cutter", "Cu_tter", reel)
                     {
                         IconKey     = "TbCut",
                         Description = "Cut pieces out of a strip",
                         Parameters  = LabParameterSet.For(LabOf.Id, "tool:cutter", "Cutter", [new LabParameter("side", "Strip", LabParameterKind.Choice, "Output", "Which strip the cut is taken from.", null, ["Output", "Input", "Written", "Processed"])]),
                         Commands =
                         [
                             ProvidedCommand.From("cutter-marked", "Cut _Marked", () => CutFrom(null), "TbCut", () => HasMarks, "Cut the stretch between In and Out"),
                             ProvidedCommand.From("cutter-all", "Cut _All", () => CutFrom((0, long.MaxValue)), null, null, "Cut the whole strip"),
                             ProvidedCommand.From("cutter-first", "Cut _First Bytes...", () => CutBytes(true), null, null, "Cut the first n bytes"),
                             ProvidedCommand.From("cutter-last", "Cut _Last Bytes...", () => CutBytes(false), null, null, "Cut the last n bytes")
                         ]
                     };
    }

    protected override IEnumerable<LabCommandEntry> DefineModelCommands()
    {
        yield return new LabCommandEntry(LabOwner.Subject, ProvidedCommand.From("reel-copy", "Open a _Copy in a New Window", () => LabHost.Open(LabOf, _setup.Clone()), "TbCopy", null, "Start another window with the same source and adapters"));
        yield return new LabCommandEntry(LabOwner.Part, ProvidedCommand.From("part-copy-json", "Copy Part _JSON", CopyPartJson, "TbCopy", () => ActivePart is not null, "Put the active part on the clipboard as JSON"));
    }

    private void CopyPartJson()
    {
        object? tag = ActivePart?.Tag;
        if (tag is null) return;

        object value = tag is StreamCut cut ? new { cut.Label, cut.Origin, Hex = Convert.ToHexString(cut.Data) } : tag;
        LabUi.Copy(JsonConvert.SerializeObject(value, Formatting.Indented));
        Status = "Copied the part as JSON";
    }

    private void SetAllAdapters(bool on)
    {
        foreach (var spec in _setup.Adapters) spec.Enabled = on;
        AdaptersEdited(on ? "every adapter turned on" : "every adapter turned off");
    }

    private void ClearAdapters()
    {
        _setup.Adapters.Clear();
        SetActivePart(null);
        AdaptersEdited("adapters cleared");
    }

    private void AddFromTool(AdapterDef def)
    {
        bool post = Tools.FirstOrDefault(t => t.Id == "adapt")?.Parameters?["post"].Bool == true;
        if (post && !def.CanRead)
        {
            Status = $"{def.Title} cannot be applied to what was recorded.";
            return;
        }

        var spec = new AdapterSpec { Key = def.Key, Arg = def.ArgDefault, Side = post ? AdapterSide.Read : AdapterSide.Both };
        if (post) AddPost(spec);
        else AddAdapter(spec);
        SelectStage(spec);
    }

    private void CutFrom(Nullable<(long From, long To)> range)
    {
        string side = Tools.FirstOrDefault(t => t.Id == "cutter")?.Parameters?["side"].Text ?? "Output";
        if (Enum.TryParse<StripSide>(side, true, out var strip)) ShowStrip(strip);

        if (range is { } r) SetMarks(r.From, Math.Min(r.To, Strip.Length));
        if (Cut() is { } cut) SelectStage(cut);
    }

    private void CutBytes(bool first)
    {
        string? answer = LabUi.Ask("Cutter", first ? "How many bytes from the start?" : "How many bytes from the end?", "16");
        if (!TryNumber(answer ?? string.Empty, out long n) || n < 1)
        {
            if (answer is not null) Status = "Give a number of bytes.";
            return;
        }

        string side = Tools.FirstOrDefault(t => t.Id == "cutter")?.Parameters?["side"].Text ?? "Output";
        if (Enum.TryParse<StripSide>(side, true, out var strip)) ShowStrip(strip);

        long length = Strip.Length;
        n = Math.Min(n, length);
        CutFrom(first ? (0, n) : (length - n, length));
    }

    private void SetSource()
    {
        string? answer = LabUi.Ask("Source", "File, tcp://host:port, tls://host:port or mem:hex:...", _setup.Source.ToString());
        if (string.IsNullOrWhiteSpace(answer)) return;

        if (!StreamSource.TryParse(answer, out var source) || source is null)
        {
            Status = "That is not a way of naming a stream.";
            return;
        }

        EditSetup(s => s.Source = source);
        Status = "The source is changed: open the reel to use it";
    }

    private void SetStep()
    {
        string? answer = LabUi.Ask("Step", "How many bytes a read takes:", _setup.Step.ToString());
        if (answer is null) return;

        if (!int.TryParse(answer.Trim(), out int step) || step < 1)
        {
            Status = "Give a number of bytes, 1 or more.";
            return;
        }

        EditSetup(s => s.Step = step);
    }

    private void RenameCut()
    {
        if (ActiveCut is not { } cut) return;

        string? name = LabUi.Ask("Cut", "What is it called?", cut.Label);
        if (string.IsNullOrWhiteSpace(name)) return;

        cut.Label = name.Trim();
        Touch();
    }

    private void MoveActiveCut(int delta)
    {
        if (ActiveCut is not { } cut) return;

        MoveCut(cut, delta);
        SelectStage(cut);
    }
}
