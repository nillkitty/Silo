using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace Silo.Labs.Qr;

/// <summary>What reading a code (the one being made, a spoiled copy of it, a grid typed in or a picture) came to.</summary>
/// <param name="Source">What was read, in words.</param>
/// <param name="Note">What is worth saying about it (how much was spoiled, and whether the code was within its limits).</param>
public sealed record QrReading(string Source, QrDecodeResult Result, string? Note);

/// <summary>One QR Code Lab window: the code as it is set up, what making it came to, and what reading it (or a spoiled copy of it) came to.</summary>
public sealed partial class QrLabSession : LabSession<QrDoc>, ILabLive
{
    private static readonly LabSaved<QrDoc> Saved = new("qr");
    private static readonly Random          Chance = new();

    private readonly Debouncer          _rebuild;
    private readonly List<LabInspector> _inspectors;
    private          QrDoc              _doc   = new();
    private          QrBuild            _build = new(string.Empty, null, null, [], [], null);
    private          bool[,]?           _spoiled;
    private          bool               _dirty;
    private          bool               _started;

    public QrLabSession(QrLab lab) : base(lab)
    {
        _rebuild    = new Debouncer(TimeSpan.FromMilliseconds(150), Rebuild);
        _inspectors = [new QrOptionsInspector(this).OwnedBy(LabOwner.OfAspect("options")), new QrAnatomyInspector(this).OwnedBy(LabOwner.OfAspect("anatomy")), new QrReadInspector(this).OwnedBy(LabOwner.OfAspect("read")), new QrTextInspector(this).OwnedBy(LabOwner.OfAspect("text"))];
    }

    internal static LabSaved<QrDoc> Kept => Saved;

    /// <summary>The code as it is set up.</summary>
    public QrDoc Doc => _doc;

    /// <summary>What making the code came to.</summary>
    public QrBuild Build => _build;

    /// <summary>The last reading, or null.</summary>
    public QrReading? Reading { get; private set; }

    /// <summary>The modules of the spoiled copy that was read, when there is one (the preview shows it in place of the code).</summary>
    public bool[,]? Spoiled => _spoiled;

    public bool IsDirty => _dirty;

    public event EventHandler? Changed;

    /// <summary>A different code was opened: the boxes show it afresh.</summary>
    public event EventHandler? Loaded;

    public override QrDoc? CurrentValue => _doc.Clone();

    public override string ValueText => _build.Payload;

    public override string Title => $"{LabOf.Name} - {(string.IsNullOrWhiteSpace(_doc.Name) ? Shorten(_build.Payload) : _doc.Name)}{(_dirty ? " *" : string.Empty)}";

    /// <summary>3: the panes belong to aspects of the code now (the model); windows saved with the old arrangement start afresh.</summary>
    public override int LayoutVersion => 3;

    public override IReadOnlyList<LabInspector> Inspectors => _inspectors;

    private QrLab Lab_ => (QrLab)LabOf;

    public void Say(string text) => Status = text;

    private static string Shorten(string text) => text.Length == 0 ? "new code" : LabUi.Shorten(text, 30);

    // ---- opening ---------------------------------------------------------------------------------

    protected override Task OpenValueAsync(QrDoc value)
    {
        Replace(value.Clone(), false);
        return Task.CompletedTask;
    }

    protected override Task OpenDefaultAsync()
    {
        if (!_started) Replace(Lab_.Blank(), false);
        return Task.CompletedTask;
    }

    protected override void OnAttached()
    {
        if (!_started) _ = OpenDefaultAsync();
    }

    protected override void OnDispose() => _rebuild.Cancel();

    public void Replace(QrDoc doc, bool dirty)
    {
        _started = true;
        _doc     = doc;
        _doc.Normalize();
        _dirty   = dirty;
        _spoiled = null;
        Reading  = null;
        Rebuild();
        Loaded?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    // ---- editing ---------------------------------------------------------------------------------

    /// <summary>Changes the code; it is made again a moment after the last change (typing goes on without waiting for it).</summary>
    public void Edit(Action<QrDoc> change)
    {
        change(_doc);
        _dirty = true;
        _rebuild.Poke();
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    public void EditNow(Action<QrDoc> change)
    {
        _rebuild.Cancel();
        change(_doc);
        _dirty = true;
        Rebuild();
        RaiseValueChanged();
        RaiseActionsChanged();
    }

    public void SetField(string key, string value) => Edit(d => d.Fields[key] = value);

    /// <summary>Changes what kind of thing the code says. What it said is kept as far as that can be: text carries over as the text, and a payload of the new kind is read from it when it is one.</summary>
    public void SetKind(QrPayloadKind kind)
    {
        if (kind == _doc.Kind) return;

        string payload = _build.Payload;
        EditNow(d =>
                {
                    var guess = payload.Length > 0 ? QrPayloads.Detect(payload) : null;
                    d.Kind   = kind;
                    d.Fields = kind == QrPayloadKind.Text ? new Dictionary<string, string> { ["text"] = payload }
                               : guess is not null && guess.Kind == kind ? new Dictionary<string, string>(guess.Fields)
                               : QrPayloads.Defaults(kind);
                });
        Loaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes a text as what the code says, and works out what kind of payload it is.</summary>
    public void UsePayload(string text)
    {
        var guess = QrPayloads.Detect(text);
        EditNow(d =>
                {
                    d.Kind   = guess.Kind;
                    d.Fields = new Dictionary<string, string>(guess.Fields);
                });
        Loaded?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        _rebuild.Cancel();
        _build   = QrBuilder.Build(_doc, Lab_.CheckEachCode);
        _spoiled = null;
        Reading  = null;
        Status   = Summary();
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseActionsChanged();
    }

    /// <summary>The code in a line, or what is wrong.</summary>
    public string Summary()
    {
        if (!_build.IsBuilt) return _build.Problem ?? "No code yet.";

        var c = _build.Code!;
        string check = _build.Check is null ? string.Empty : _build.CheckPassed ? "; it reads back" : "; it does NOT read back (" + (_build.Check.Error ?? "different text") + ")";
        return $"Version {c.Version} ({c.Size} x {c.Size}), level {c.Ecc}, mask {c.Mask}; {_build.ModeText}; {_build.PayloadBytes:N0} byte(s), {_build.Fill:P0} of the room{check}";
    }

    // ---- reading ---------------------------------------------------------------------------------

    private void Read(string source, QrDecodeResult result, string? note)
    {
        Reading = new QrReading(source, result, note);
        Status  = result.IsOk ? $"Read: {LabUi.Shorten(result.Value!.Text, 60)}" : "Could not read: " + result.Error;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the code as it is, without damage.</summary>
    public void ReadOwn()
    {
        if (!_build.IsBuilt) { Status = "There is no code to read."; return; }

        _spoiled = null;
        Read("the code as it is", QrDecoder.Decode(_build.Code!.ToMatrix()), null);
    }

    /// <summary>Turns over modules in some codewords of the code and reads the result: how much a reader can put right.</summary>
    public void Spoil(int codewords)
    {
        if (!_build.IsBuilt) { Status = "There is no code to spoil."; return; }

        var code = _build.Code!;
        var (modules, hit) = QrDamage.Damage(code, codewords, Chance);
        _spoiled = modules;
        var blocks   = QrDamage.BlockOfCodewords(code.Version, code.Ecc);
        var perBlock = hit.GroupBy(h => blocks[h]).ToDictionary(g => g.Key, g => g.Count());
        int worst    = perBlock.Count == 0 ? 0 : perBlock.Values.Max();
        int limit    = QrCode.EccCodewordsPerBlock(code.Version, code.Ecc) / 2;
        string note  = $"{hit.Count} codeword(s) spoiled; the most in one block is {worst}, and a block can put right {limit} " +
                       (worst <= limit ? "(within what the code can take)." : "(more than the code can take, so it may fail).");
        Read($"a copy with {hit.Count} codeword(s) spoiled", QrDecoder.Decode(modules), note);
    }

    public void ClearSpoiled()
    {
        _spoiled = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads a grid of modules typed or pasted (1s and 0s, # and spaces, or the block characters).</summary>
    public void ReadMatrixText(string text)
    {
        if (!QrRender.TryParseMatrix(text, out var modules, out var problem))
        {
            Read("a typed grid", new QrDecodeResult(null, problem), null);
            return;
        }

        _spoiled = null;
        Read($"a typed grid, {modules.GetLength(0)} modules across", QrDecoder.Decode(modules), null);
    }

    public void ReadImageFile(string path)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Read(Path.GetFileName(path), ReadBitmap(decoder.Frames[0]), "A picture has to be straight on and sharp: there is no search for the code in a photo.");
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or FormatException or InvalidOperationException)
        {
            Status = "That picture could not be read: " + ex.Message;
        }
    }

    public void ReadClipboardImage()
    {
        BitmapSource? image = null;
        try
        {
            if (Clipboard.ContainsImage()) image = Clipboard.GetImage();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // the clipboard is in use by another program
        }

        if (image is null) { Status = "There is no picture on the clipboard."; return; }

        Read("the picture on the clipboard", ReadBitmap(image), "A picture has to be straight on and sharp: there is no search for the code in a photo.");
    }

    private static QrDecodeResult ReadBitmap(BitmapSource source)
    {
        var gray   = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Gray8, null, 0);
        int width  = gray.PixelWidth, height = gray.PixelHeight;
        var pixels = new byte[width * height];
        gray.CopyPixels(pixels, width, 0);
        return QrDecoder.DecodeImage(pixels, width, height);
    }

    /// <summary>Makes the last reading (the text of it) what the code says.</summary>
    public void UseReading()
    {
        if (Reading?.Result.Value is not { } value) { Status = "Nothing was read."; return; }

        UsePayload(value.Text);
        Status = "The code now says what was read.";
    }

    // ---- exports ---------------------------------------------------------------------------------

    private QrCode? Need()
    {
        if (_build.Code is { } code) return code;

        Status = _build.Problem ?? "There is no code yet.";
        return null;
    }

    public string? Svg() => Need() is { } c ? QrRender.Svg(c, _doc.Border, _doc.Colors) : null;

    public byte[]? Png() => Need() is { } c ? QrRender.Png(c, _doc.Scale, _doc.Border, _doc.Colors) : null;

    private void CopyText(string? text, string what)
    {
        if (text is null) return;

        LabUi.Copy(text);
        Status = $"Copied the {what}";
    }

    private void SaveSvg()
    {
        if (Svg() is not { } svg) return;

        var dialog = new SaveFileDialog { Title = "Save the code as a picture", FileName = FileName("svg"), Filter = "SVG picture (*.svg)|*.svg" };
        if (dialog.ShowDialog() != true) return;

        File.WriteAllText(dialog.FileName, svg, new UTF8Encoding(false));
        Status = $"Saved {Path.GetFileName(dialog.FileName)}";
    }

    private void SavePng()
    {
        if (Png() is not { } png) return;

        var dialog = new SaveFileDialog { Title = "Save the code as a picture", FileName = FileName("png"), Filter = "PNG picture (*.png)|*.png" };
        if (dialog.ShowDialog() != true) return;

        File.WriteAllBytes(dialog.FileName, png);
        Status = $"Saved {Path.GetFileName(dialog.FileName)}";
    }

    private string FileName(string extension)
    {
        string name = string.IsNullOrWhiteSpace(_doc.Name) ? "qrcode" : _doc.Name;
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name + "." + extension;
    }

    private void CopyImage()
    {
        if (Need() is not { } code) return;

        try
        {
            Clipboard.SetImage(QrPicture.Bitmap(code.ToMatrix(), _doc.Scale, _doc.Border, _doc.Colors));
            Status = "Copied the picture";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status = "The clipboard is in use by another program.";
        }
    }

    public void ChooseImage()
    {
        var dialog = new OpenFileDialog { Title = "Read a picture of a code", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" };
        if (dialog.ShowDialog() == true) ReadImageFile(dialog.FileName);
    }

    // ---- saved -----------------------------------------------------------------------------------

    public void OpenSaved(string name)
    {
        if (Saved.Load(name) is not { } doc)
        {
            Status = $"There is no code called {name} in the Silo.";
            return;
        }

        if (_dirty && !LabUi.Confirm("This code has changes that are not kept in the Silo. Open another anyway?", "QR Code Lab")) return;

        if (string.IsNullOrWhiteSpace(doc.Name)) doc.Name = name;
        Replace(doc, false);
        RememberRecent(name);
    }

    // ---- library and recent (the panes every Lab window has) ---------------------------------------

    private static readonly (string Title, string Payload)[] Examples =
    [
        ("A web address", "https://example.com/"),
        ("A Wi-Fi network", "WIFI:T:WPA;S:Guest network;P:welcome home;;"),
        ("A contact", "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Lovelace;Ada;;;\r\nFN:Ada Lovelace\r\nORG:Analytical Engines\r\nTEL:+441234567890\r\nEMAIL:ada@example.com\r\nEND:VCARD"),
        ("A one-time password", "otpauth://totp/Example:ada@example.com?secret=JBSWY3DPEHPK3PXP&issuer=Example"),
        ("A calendar event", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nSUMMARY:Launch\r\nDTSTART:20261031T193000\r\nDTEND:20261031T210000\r\nLOCATION:The hall\r\nEND:VEVENT\r\nEND:VCALENDAR"),
        ("A place", "geo:51.5007,-0.1246?q=Big%20Ben"),
        ("Digits only (the smallest code)", "0123456789012345678901234567890123456789")
    ];

    protected override bool RecentOnOpen => false;

    public override bool LibraryOpensWithWindow => true;

    public override Task OpenRecentAsync(string text)
    {
        OpenSaved(text);
        return Task.CompletedTask;
    }

    public override IReadOnlyList<LabLibrarySection> LibrarySections(string filter)
    {
        bool Wanted(string text) => filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var kept = Saved.Names().Where(Wanted)
                        .Select(name => new LabLibraryEntry(name, "QR code", null, "QrCode", () => OpenSaved(name), name == _doc.Name, ToolTip: "Double-click to open", Delete: () => Saved.Delete(name)))
                        .ToList();
        // the examples are the subject type's prefabs and templates: the Library shows them
        return [new("In the Silo", "Database5", kept, Note: Saved.IsAvailable ? null : "No Silo is open")];
    }

    private void SaveCode()
    {
        string? name = _doc.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = LabUi.Ask("Save code", "What is the code called?", string.IsNullOrWhiteSpace(_build.Payload) ? string.Empty : LabUi.Shorten(_build.Payload, 24));
            if (string.IsNullOrWhiteSpace(name)) return;
        }

        try
        {
            _doc.Name = name.Trim();
            Saved.Save(_doc.Name, _doc);
            _dirty = false;
            Status = $"Kept {_doc.Name} in the Silo";
            Changed?.Invoke(this, EventArgs.Empty);
            RaiseValueChanged();
            RaiseActionsChanged();
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
    }

    private void DeleteSaved()
    {
        if (string.IsNullOrWhiteSpace(_doc.Name)) return;

        Status = Saved.Delete(_doc.Name) ? $"Removed {_doc.Name} from the Silo" : $"{_doc.Name} was not kept";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NewCode()
    {
        if (_dirty && !LabUi.Confirm("This code has changes that are not kept in the Silo. Start another anyway?", "QR Code Lab")) return;

        Replace(Lab_.Blank(), false);
    }

    private void PastePayload()
    {
        string? text = null;
        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // the clipboard is in use by another program
        }

        if (string.IsNullOrEmpty(text)) { Status = "There is no text on the clipboard."; return; }

        UsePayload(text);
        Status = $"The code now says what was on the clipboard (as {QrPayloads.TitleOf(_doc.Kind).ToLowerInvariant()}).";
    }

    // ---- actions ---------------------------------------------------------------------------------

    public override IEnumerable<ProvidedCommand> GlobalActions
    {
        get
        {
            yield return ProvidedCommand.From("new-code", "_New Code", NewCode, "TbNew", null, "Start with an empty code");
            yield return ProvidedCommand.From("paste-payload", "_Paste What It Says", PastePayload, "TbPaste", null, "Take the text on the clipboard as what the code says (an address, a Wi-Fi network... are recognized)");
            yield return ProvidedCommand.From("show-saved", "Show _Saved Codes", () => Host?.ShowInspector("saved"), "Folder17", null, "Show the codes kept in the Silo");
            yield return ProvidedCommand.From("settings", "Open Se_ttings", () => Host?.ShowInspector(LabParametersInspector.InspectorId), "Settings", null, "Show the Lab's parameters");
        }
    }

    public override IEnumerable<ProvidedCommand> ContextActions
    {
        get
        {
            yield return ProvidedCommand.From("make", "_Make Again", () => EditNow(_ => { }), "Refresh", null, "Make the code again from what is set up") with { Gesture = "F5" };
            yield return ProvidedCommand.From("read-own", "_Read the Code", ReadOwn, "DataSearching", () => _build.IsBuilt, "Read the code with the decoder here");
            yield return ProvidedCommand.From("spoil", "S_poil and Read", () => Spoil(3), "TbDelete", () => _build.IsBuilt, "Turn over modules in three codewords at random and read what is left");
            yield return ProvidedCommand.From("read-image", "Read a _Picture...", ChooseImage, "TbOpen", null, "Read a picture file of a code (straight on and sharp)");
            yield return ProvidedCommand.From("read-clipboard", "Read the Picture on the C_lipboard", ReadClipboardImage, "TbPaste", null, "Read the picture on the clipboard (a screenshot of a code, say)");
            yield return ProvidedCommand.From("use-reading", "_Use What Was Read", UseReading, "Check", () => Reading?.Result.IsOk == true, "Make the text that was read what the code says");
        }
    }

    public override IEnumerable<ProvidedCommand> ValueActions
    {
        get
        {
            yield return ProvidedCommand.From("save", "_Save Code", SaveCode, "TbSave", () => Saved.IsAvailable, "Keep the code, as it is set up, in the Silo") with { Gesture = "Ctrl+S" };
            yield return ProvidedCommand.From("save-png", "Save as _PNG...", SavePng, "TbExport", () => _build.IsBuilt, "Write the code to a PNG file");
            yield return ProvidedCommand.From("save-svg", "Save as S_VG...", SaveSvg, "TbExport", () => _build.IsBuilt, "Write the code to an SVG file");
            yield return ProvidedCommand.From("copy-image", "Copy _Picture", CopyImage, "TbCopy", () => _build.IsBuilt, "Put the picture of the code on the clipboard");
            yield return ProvidedCommand.From("copy-payload", "Copy What It _Says", () => CopyText(_build.Payload, "text"), "TbCopy", () => _build.IsBuilt, "Put the text the code says on the clipboard");
            yield return ProvidedCommand.From("copy-svg", "Copy S_VG Text", () => CopyText(Svg(), "SVG"), "TbCopy", () => _build.IsBuilt, "Put the SVG of the code on the clipboard as text");
            yield return ProvidedCommand.From("copy-blocks", "Copy as _Text Blocks", () => CopyText(Need() is { } c ? QrRender.Blocks(c) : null, "text picture"), "TbCopy", () => _build.IsBuilt,
                                              "Put the code on the clipboard as text, two rows to a line");
            yield return ProvidedCommand.From("delete", "_Delete Saved Code", DeleteSaved, "TbDelete", () => Saved.IsAvailable && !string.IsNullOrWhiteSpace(_doc.Name) && Saved.Exists(_doc.Name), "Remove the code from the Silo");
        }
    }

    // ---- terminal --------------------------------------------------------------------------------

    private static Task<string?> Done(string? text) => Task.FromResult(text);

    public override IReadOnlyList<LabTerminalCommand> TerminalCommands =>
    [
        new("text", "text <payload>          say this (an address, a Wi-Fi: text, a vCard... are recognized)", call => Done(TextCommand(call.Rest))),
        new("kind", "kind [name]             show the kinds of thing a code can say, or choose one", call => Done(KindCommand(call.Rest))),
        new("field", "field <key> <value>     fill in a field of the kind (no arguments: list them)", call => Done(FieldCommand(call.Rest))),
        new("ecc", "ecc <l|m|q|h>           the level of correction", call => Done(EccCommand(call.Rest))),
        new("version", "version <min> [max]    the smallest and the biggest version allowed (1 to 40)", call => Done(VersionCommand(call.Args))),
        new("mask", "mask <0-7|auto>        the mask", call => Done(MaskCommand(call.Rest))),
        new("mode", "mode <auto|numeric|alphanumeric|byte>", call => Done(ModeCommand(call.Rest))),
        new("margin", "margin <modules>        the empty margin", call => Done(NumberCommand(call.Rest, "margin", 0, 20, (d, n) => d.Border = n))),
        new("scale", "scale <pixels>         pixels to a module", call => Done(NumberCommand(call.Rest, "scale", 1, 64, (d, n) => d.Scale = n))),
        new("colors", "colors <dark> <light>   as #RGB or #RRGGBB", call => Done(ColorsCommand(call.Args))),
        new("info", "info                    how the code came out", _ => Done(Summary() + (_build.Warnings.Count > 0 ? "\n" + string.Join('\n', _build.Warnings.Select(w => "! " + w)) : string.Empty))),
        new("show", "show                    the code as text blocks", _ => Done(_build.IsBuilt ? QrRender.Blocks(_build.Code!) : _build.Problem)),
        new("matrix", "matrix                  the code as lines of 1 and 0", _ => Done(_build.IsBuilt ? QrRender.Matrix(_build.Code!) : _build.Problem)),
        new("svg", "svg                     the code as SVG", _ => Done(Svg() ?? _build.Problem)),
        new("read", "read                    read the code with the decoder", _ => Done(ReadCommand())),
        new("spoil", "spoil [codewords]       spoil some codewords (3 when not said) and read what is left", call => Done(SpoilCommand(call.Rest))),
        new("capacity", "capacity [version]     how much text fits each level of correction", call => Done(CapacityCommand(call.Rest))),
        new("save", "save [name]             keep the code in the Silo", call => Done(SaveCommand(call.Rest))),
        new("list", "list                    the codes kept in the Silo", _ => Done(Saved.IsAvailable ? string.Join('\n', Saved.Names()) : "No Silo is open.")),
        new("open", "open <name>             open a code kept in the Silo", call => Done(OpenCommand(call.Rest))),
        new("json", "json                    the code as it is set up, as JSON", _ => Done(JsonConvert.SerializeObject(_doc, Formatting.Indented, new Newtonsoft.Json.Converters.StringEnumConverter())))
    ];

    private string TextCommand(string rest)
    {
        if (string.IsNullOrEmpty(rest)) return _build.Payload;

        UsePayload(rest.Replace("\\n", "\n"));
        return Summary();
    }

    private string KindCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
            return string.Join('\n', Enum.GetValues<QrPayloadKind>().Select(k => $"{(k == _doc.Kind ? "*" : " ")} {k.ToString().ToLowerInvariant(),-8} {QrPayloads.TitleOf(k)}"));

        if (!Enum.TryParse<QrPayloadKind>(rest.Trim(), true, out var kind)) throw new InvalidOperationException("There is no kind called " + rest.Trim() + ". Type kind to see them.");

        SetKind(kind);
        return $"The code says {QrPayloads.TitleOf(kind).ToLowerInvariant()}. Fields: {string.Join(", ", QrPayloads.FieldsOf(kind).Select(f => f.Key))}";
    }

    private string FieldCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
            return string.Join('\n', QrPayloads.FieldsOf(_doc.Kind).Select(f => $"{f.Key,-10} {(_doc.Fields.TryGetValue(f.Key, out var v) ? v : string.Empty)}"));

        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var field = QrPayloads.FieldsOf(_doc.Kind).FirstOrDefault(f => f.Key.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (field is null) throw new InvalidOperationException($"{QrPayloads.TitleOf(_doc.Kind)} has no field called {parts[0]}. Type field to see them.");

        EditNow(d => d.Fields[field.Key] = parts.Length > 1 ? parts[1].Replace("\\n", "\n") : string.Empty);
        Loaded?.Invoke(this, EventArgs.Empty);
        return Summary();
    }

    private string EccCommand(string rest)
    {
        QrEcc? ecc = rest.Trim().ToUpperInvariant() switch { "L" or "LOW" => QrEcc.Low, "M" or "MEDIUM" => QrEcc.Medium, "Q" or "QUARTILE" => QrEcc.Quartile, "H" or "HIGH" => QrEcc.High, _ => null };
        if (ecc is null) return $"The level is {_doc.Ecc}. Say l, m, q or h to change it.";

        EditNow(d => d.Ecc = ecc.Value);
        return Summary();
    }

    private string VersionCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return $"Versions {_doc.MinVersion} to {_doc.MaxVersion} are allowed.";
        if (!int.TryParse(args[0], out int min) || min is < 1 or > 40) throw new InvalidOperationException("A version is from 1 to 40.");

        int max = args.Count > 1 && int.TryParse(args[1], out int m) ? m : Math.Max(min, _doc.MaxVersion);
        if (max is < 1 or > 40 || max < min) throw new InvalidOperationException("The biggest version is from the smallest to 40.");

        EditNow(d => (d.MinVersion, d.MaxVersion) = (min, max));
        return Summary();
    }

    private string MaskCommand(string rest)
    {
        int mask = rest.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) ? -1 : int.TryParse(rest, out int n) ? n : -2;
        if (mask is < -1 or > 7) throw new InvalidOperationException("The mask is 0 to 7, or auto.");

        EditNow(d => d.Mask = mask);
        return Summary();
    }

    private string ModeCommand(string rest)
    {
        var mode = QrDoc.Modes.FirstOrDefault(m => m.Equals(rest.Trim(), StringComparison.OrdinalIgnoreCase));
        if (mode is null) throw new InvalidOperationException("The mode is one of: " + string.Join(", ", QrDoc.Modes.Select(m => m.ToLowerInvariant())));

        EditNow(d => d.Mode = mode);
        return Summary();
    }

    private string NumberCommand(string rest, string what, int min, int max, Action<QrDoc, int> set)
    {
        if (!int.TryParse(rest.Trim(), out int n) || n < min || n > max) throw new InvalidOperationException($"The {what} is a number from {min} to {max}.");

        EditNow(d => set(d, n));
        return Summary();
    }

    private string ColorsCommand(IReadOnlyList<string> args)
    {
        if (args.Count != 2 || !QrColors.TryParseHex(args[0], out _) || !QrColors.TryParseHex(args[1], out _)) throw new InvalidOperationException("Give two colors, dark and light, as #RGB or #RRGGBB.");

        EditNow(d => (d.Dark, d.Light) = (args[0].StartsWith('#') ? args[0] : "#" + args[0], args[1].StartsWith('#') ? args[1] : "#" + args[1]));
        return Summary() + (_build.Warnings.Count > 0 ? "\n" + string.Join('\n', _build.Warnings.Select(w => "! " + w)) : string.Empty);
    }

    private string ReadCommand()
    {
        ReadOwn();
        return Reading is null ? "Nothing to read." : Describe(Reading);
    }

    private string SpoilCommand(string rest)
    {
        int n = int.TryParse(rest.Trim(), out int count) ? count : 3;
        Spoil(n);
        return Reading is null ? "Nothing to spoil." : Describe(Reading);
    }

    public static string Describe(QrReading reading)
    {
        var sb = new StringBuilder("Read " + reading.Source + ": ");
        if (reading.Result.Value is not { } v) return sb.Append("could not be read. ").Append(reading.Result.Error).ToString();

        sb.AppendLine($"version {v.Version}, level {v.Ecc}, mask {v.Mask}");
        sb.AppendLine($"{v.Corrected} codeword(s) put right ({string.Join(", ", v.BlockCorrections)} per block), {v.FormatErrors} of the 15 format bits");
        foreach (var s in v.Segments) sb.AppendLine($"  {s.Mode,-12} {s.Characters} character(s)");
        sb.Append("Text: ").Append(LabUi.Shorten(v.Text, 200));
        if (reading.Note is not null) sb.AppendLine().Append(reading.Note);
        return sb.ToString();
    }

    private string CapacityCommand(string rest)
    {
        int version = int.TryParse(rest.Trim(), out int v) ? v : _build.Code?.Version ?? 1;
        if (version is < 1 or > 40) throw new InvalidOperationException("A version is from 1 to 40.");

        var sb = new StringBuilder($"Version {version} ({version * 4 + 17} x {version * 4 + 17}):\n");
        sb.AppendLine("level      digits   letters    bytes");
        foreach (var (ecc, numeric, alpha, bytes) in QrBuilder.Capacity(version)) sb.AppendLine($"{ecc,-9} {numeric,7} {alpha,9} {bytes,8}");
        return sb.ToString().TrimEnd();
    }

    private string SaveCommand(string rest)
    {
        if (!string.IsNullOrWhiteSpace(rest)) _doc.Name = rest.Trim();
        if (string.IsNullOrWhiteSpace(_doc.Name)) throw new InvalidOperationException("Give it a name: save <name>.");

        try
        {
            Saved.Save(_doc.Name, _doc);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }

        _dirty = false;
        RaiseValueChanged();
        return $"Kept {_doc.Name} in the Silo.";
    }

    private string OpenCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) throw new InvalidOperationException("Open which? Type list to see them.");

        OpenSaved(rest.Trim());
        return Status;
    }

    public override UIElement BuildMainView()
    {
        return new QrView(this);
    }
}
