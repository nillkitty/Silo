using Telefrag.DI;

namespace Silo.Labs.Qr;

public static class QrLabParameters
{
    public const string Ecc   = "ecc";
    public const string Scale = "scale";
    public const string Border = "border";
    public const string Check = "check";
}

/// <summary>
///     Makes QR codes and takes them apart: what a code says (text, or a Wi-Fi network, a contact, a one-time password and the like), how it is made (version, level of correction, mask, mode), how it
///     is drawn, and what a reader makes of it, even when part of it has been spoiled.
/// </summary>
[Singleton(typeof(ILab))]
public class QrLab : LabBase<QrDoc>
{
    public override string Description => "Make QR codes from text or from what phones know what to do with (Wi-Fi, contacts, addresses, one-time passwords...), choose the version, the level of correction and the mask, " +
                                          "see how the code is built, spoil it to see what a reader still makes of it, and save it as a picture or in the Silo.";

    public override string IconKey => "Modules";

    public override string Id => "qr";

    public override string Name => "QR Code Lab";

    public override int Order => 70;

    protected override IEnumerable<LabParameter> DefineParameters()
    {
        yield return new LabParameter(QrLabParameters.Ecc, "Level of correction of a new code", LabParameterKind.Choice, nameof(QrEcc.Medium),
                                      "How much of a code can be wrong and still be read: Low about 7%, Medium 15%, Quartile 25%, High 30%. More takes a bigger code.", "New codes",
                                      [nameof(QrEcc.Low), nameof(QrEcc.Medium), nameof(QrEcc.Quartile), nameof(QrEcc.High)]);
        yield return new LabParameter(QrLabParameters.Scale, "Pixels to a module", LabParameterKind.Text, "8", "How big a module is on the screen and in a PNG file (1 to 64).", "New codes", hint: "8");
        yield return new LabParameter(QrLabParameters.Border, "Margin (modules)", LabParameterKind.Text, "4", "The empty margin around a code; readers want 4.", "New codes", hint: "4");
        yield return new LabParameter(QrLabParameters.Check, "Read each code back", LabParameterKind.Bool, true,
                                      "After a code is made, read it again with the decoder in this Lab and say whether it says what it should.", "Checking");
    }

    public override LabSession<QrDoc> CreateSession()
    {
        return new QrLabSession(this);
    }

    internal bool CheckEachCode => Parameters[QrLabParameters.Check].Bool;

    /// <summary>A new code with the Lab's own defaults.</summary>
    public QrDoc Blank(string text = "")
    {
        var doc = QrDoc.ForText(text);
        doc.Ecc    = Enum.TryParse<QrEcc>(Parameters[QrLabParameters.Ecc].Text, out var ecc) ? ecc : QrEcc.Medium;
        doc.Scale  = int.TryParse(Parameters[QrLabParameters.Scale].Text, out int scale) ? Math.Clamp(scale, 1, 64) : 8;
        doc.Border = int.TryParse(Parameters[QrLabParameters.Border].Text, out int border) ? Math.Clamp(border, 0, 20) : 4;
        return doc;
    }

    private static readonly string[] Prefixes = ["WIFI:", "BEGIN:VCARD", "BEGIN:VCALENDAR", "otpauth://", "geo:", "mailto:", "tel:", "smsto:"];

    protected override bool TryConvert(object? candidate, out QrDoc? value)
    {
        switch (candidate)
        {
            case QrDoc doc:
                value = doc;
                return true;
            // asked of every node when a menu is built: addresses and the texts that plainly are one of the kinds of payload are taken, other text is not
            case Uri uri when uri.Scheme is "http" or "https" && uri.OriginalString.Length <= 2000:
                value = QrDoc.FromPayload(uri.OriginalString);
                return true;
            case string text when text.Length is > 0 and <= 2000 && Prefixes.Any(p => text.StartsWith(p, StringComparison.OrdinalIgnoreCase)):
                value = QrDoc.FromPayload(text);
                return true;
            case string text when text.Length is > 8 and <= 2000 && (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                                  && !text.Any(char.IsWhiteSpace):
                value = QrDoc.FromPayload(text);
                return true;
            default:
                value = null;
                return false;
        }
    }

    public override bool TryParse(string text, out QrDoc? value)
    {
        if (string.IsNullOrEmpty(text))
        {
            value = null;
            return false;
        }

        value = QrDoc.FromPayload(text);
        var blank = Blank();
        value.Ecc    = blank.Ecc;
        value.Scale  = blank.Scale;
        value.Border = blank.Border;
        return true;
    }
}
