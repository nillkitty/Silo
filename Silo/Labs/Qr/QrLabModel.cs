using Newtonsoft.Json;

namespace Silo.Labs.Qr;

// The QR Lab as a model (see LabModel.cs):
//   subject  a code (QrDoc)                     parts   the fields of what the code says (grouped, for a contact), for the kind it says
//   aspects  Options, Anatomy, Read, Text (panes); Field (of the active field)
//   tools    Spoiler (damage the code and see whether it still reads)
public sealed partial class QrLabSession
{
    private const string SubjectId = "qr";

    private static readonly LabPartType FieldType = new("field", "Field", "Property", "One thing filled in for the kind of payload");

    private static readonly LabPartType SectionType = new("section", "Section", "Folder17", "A few fields that belong together");

    /// <summary>The window's form: a box of the field has the keyboard.</summary>
    internal event Action<string>? FieldFocusRequested;

    protected override string SubjectNoun => "code";

    // ---- subject: prefabs and templates ---------------------------------------------------------------

    protected override IEnumerable<LabSubjectType> DefineSubjectTypes()
    {
        var prefabs = Examples.Select(e => new LabPrefab(Slug(e.Title), e.Title, JsonConvert.SerializeObject(QrDoc.FromPayload(e.Payload)), LabUi.Shorten(e.Payload, 80), "QrCode", "Examples")).ToList();
        var templates = new List<LabTemplate>
                        {
                            new("web", "Web address", "https://{{address}}", [new("address", "The address", "example.com/page")], "A link a phone opens", "QrCode"),
                            new("wifi", "Wi-Fi network", "WIFI:T:{{security}};S:{{ssid}};P:{{password}};;", [new("ssid", "Network name", "Guest"), new("password", "Password", ""), new("security", "Security", "WPA", ["WPA", "WEP", "nopass"])], "Joins a network", "QrCode"),
                            new("phone", "Phone number", "tel:{{number}}", [new("number", "Number", "+15551234567")], "Starts a call", "QrCode"),
                            new("place", "Place", "geo:{{lat}},{{lon}}", [new("lat", "Latitude", "51.5007"), new("lon", "Longitude", "-0.1246")], "Opens a map", "QrCode"),
                            new("totp", "One-time password", "otpauth://totp/{{account}}?secret={{secret}}&issuer={{issuer}}", [new("account", "Account", "name@example.com"), new("secret", "Secret (base32)", "JBSWY3DPEHPK3PXP"), new("issuer", "Issuer", "Example")], "Adds an account to an authenticator", "QrCode"),
                            new("text", "Plain text", "{{text}}", [new("text", "What it says", "Hello")], "Text, as it is", "QrCode")
                        };
        yield return new LabSubjectType(SubjectId, "QR code", "QrCode", "A code and what it says, with how it is made and drawn", prefabs, templates);
    }

    private static string Slug(string name) => new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    protected override Task OnNewSubjectAsync(LabSubjectType type, string content)
    {
        QrDoc? doc = null;
        if (content.TrimStart().StartsWith('{'))
        {
            try
            {
                doc = JsonConvert.DeserializeObject<QrDoc>(content);
            }
            catch (JsonException)
            {
                doc = null;
            }
        }

        Replace(doc ?? QrDoc.FromPayload(content), true);
        return Task.CompletedTask;
    }

    // ---- parts: the fields of the payload ----------------------------------------------------------------

    protected override IEnumerable<LabPartType> DefinePartTypes() => [FieldType, SectionType];

    protected override IEnumerable<LabPart> BuildParts()
    {
        var fields = QrPayloads.FieldsOf(_doc.Kind).ToDictionary(f => f.Key);
        if (_doc.Kind == QrPayloadKind.Contact)
        {
            (string Id, string Title, string[] Keys)[] groups =
            [
                ("name", "Name", ["first", "last"]), ("work", "Work", ["org", "title"]), ("reach", "Reach", ["phone", "email", "url"]), ("address", "Address", ["street", "city", "region", "postal", "country"])
            ];
            foreach (var (id, title, keys) in groups)
            {
                var section = new LabPart(SectionType, "g:" + id, title);
                foreach (string key in keys)
                    if (fields.Remove(key, out var f))
                        section.Add(FieldPart(f));
                yield return section;
            }
        }

        foreach (var field in fields.Values) yield return FieldPart(field);
    }

    private LabPart FieldPart(QrField field)
    {
        string value = _doc.Fields.TryGetValue(field.Key, out var v) ? v : field.Default;
        return new LabPart(FieldType, "f:" + field.Key, field.Title + (field.Required ? " *" : string.Empty), field, value.Length == 0 ? "(empty)" : LabUi.Shorten(value.Replace('\n', ' '), 30));
    }

    /// <summary>A box of the field has the keyboard: it is the active part.</summary>
    internal void NoteField(string key) => SetActivePart("f:" + key);

    protected override void OnPartActivated(LabPart? part)
    {
        if (part?.Tag is QrField field) FieldFocusRequested?.Invoke(field.Key);
    }

    // ---- aspects, tools and commands ------------------------------------------------------------------------

    private QrField? ActiveField => ActivePart?.Tag as QrField;

    private string ValueOf(QrField field) => _doc.Fields.TryGetValue(field.Key, out var v) ? v : field.Default;

    private void SetFieldAndShow(QrField field, string value)
    {
        EditNow(d => d.Fields[field.Key] = value);
        Loaded?.Invoke(this, EventArgs.Empty);
    }

    protected override IEnumerable<LabAspect> DefineAspects()
    {
        var code = LabOwner.OfSubjectType(SubjectId);
        yield return new LabAspect("options", "Options", code) { IconKey = "Settings", Description = "How the code is made and how it is drawn: correction, sizes, mask, mode, colors" };
        yield return new LabAspect("anatomy", "Anatomy", code) { IconKey = "Data1", Description = "What each module of the code is for" };
        yield return new LabAspect("read", "Read", code) { IconKey = "DataSearching", Description = "The code read by the decoder here, damaged first if you like" };
        yield return new LabAspect("text", "Text", code) { IconKey = "Document", Description = "The code as text: modules, blocks, the payload" };
        yield return new LabAspect("field", "Field", LabOwner.OfPartType(FieldType.Id))
                     {
                         IconKey     = "Property",
                         Description = "The active field of what the code says",
                         Commands =
                         [
                             ProvidedCommand.From("field-clear", "_Clear", () => { if (ActiveField is { } f) SetFieldAndShow(f, string.Empty); }, "TbDelete", () => ActiveField is { } f && ValueOf(f).Length > 0, "Empty the field"),
                             ProvidedCommand.From("field-default", "_Reset to Default", () => { if (ActiveField is { } f) SetFieldAndShow(f, f.Default); }, null, () => ActiveField is { } f && ValueOf(f) != f.Default, "Put back what the field starts with"),
                             ProvidedCommand.From("field-copy", "Copy _Value", () => { if (ActiveField is { } f) CopyText(ValueOf(f), "value"); }, "TbCopy", () => ActiveField is not null, "Put the value of the field on the clipboard"),
                             ProvidedCommand.From("field-paste", "_Paste Value", PasteIntoField, "TbPaste", () => ActiveField is { Choices: null }, "Take the text on the clipboard as the value of the field")
                         ]
                     };
    }

    private void PasteIntoField()
    {
        if (ActiveField is not { } field) return;

        string? text = null;
        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // the clipboard is in use by another program
        }

        if (string.IsNullOrEmpty(text))
        {
            Status = "There is no text on the clipboard.";
            return;
        }

        SetFieldAndShow(field, field.Multiline ? text : text.Replace("\r", string.Empty).Replace("\n", " "));
    }

    protected override IEnumerable<LabTool> DefineTools()
    {
        yield return new LabTool("spoiler", "_Spoiler", LabOwner.OfSubjectType(SubjectId))
                     {
                         IconKey     = "TbDelete",
                         Description = "Turn over modules in codewords at random and see whether the code still reads",
                         Parameters  = LabParameterSet.For(LabOf.Id, "tool:spoiler", "Spoiler", [new LabParameter("codewords", "Codewords", LabParameterKind.Text, "3", "How many codewords to damage (1 to 40).", hint: "3")]),
                         Commands =
                         [
                             ProvidedCommand.From("spoiler-spoil", "_Spoil and Read", SpoilFromTool, "TbDelete", () => _build.IsBuilt, "Damage that many codewords and read what is left"),
                             ProvidedCommand.From("spoiler-mend", "_Mend", () => { ClearSpoiled(); ReadOwn(); }, "Refresh", () => _spoiled is not null, "Put the modules back and read the code as it is")
                         ]
                     };
    }

    private void SpoilFromTool()
    {
        string text = Tools.FirstOrDefault(t => t.Id == "spoiler")?.Parameters?["codewords"].Text ?? "3";
        if (!int.TryParse(text.Trim(), out int count) || count is < 1 or > 40)
        {
            Status = "The tool's Codewords is a number from 1 to 40.";
            return;
        }

        Spoil(count);
    }

    protected override IEnumerable<LabCommandEntry> DefineModelCommands()
    {
        var kinds = Enum.GetValues<QrPayloadKind>().Select(k => ProvidedCommand.From("kind-" + k.ToString().ToLowerInvariant(), QrPayloads.TitleOf(k).Replace("_", "__"), () => SetKind(k), null, null, "What the code says is " + QrPayloads.TitleOf(k).ToLowerInvariant()) with { IsChecked = () => _doc.Kind == k }).ToList();
        yield return new LabCommandEntry(LabOwner.Subject, ProvidedCommand.Submenu("qr-kind", "_Kind of Payload", kinds, null, "Template", "What a phone is to do with the code"));
        yield return new LabCommandEntry(LabOwner.Subject, ProvidedCommand.From("qr-detect", "_Recognize What It Says", () => UsePayload(_build.Payload), "DataSearching", () => _build.Payload.Length > 0, "Work out what kind of payload the text is (an address, a Wi-Fi network...) and fill the fields"));
    }
}
