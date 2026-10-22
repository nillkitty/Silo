using System.Text;
using Newtonsoft.Json;

namespace Silo.Labs.Qr;

/// <summary>A code as the person sets it up: what it says, and how it is made and drawn. This is what the Silo keeps.</summary>
public sealed class QrDoc
{
    public static readonly string[] Modes = ["Auto", "Numeric", "Alphanumeric", "Byte"];

    /// <summary>What the code is called in the Silo.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What the code says: plain text, or one of the kinds a phone knows what to do with.</summary>
    public QrPayloadKind Kind { get; set; } = QrPayloadKind.Text;

    /// <summary>The things filled in for the kind (see <see cref="QrPayloads.FieldsOf" />).</summary>
    public Dictionary<string, string> Fields { get; set; } = new();

    public QrEcc Ecc { get; set; } = QrEcc.Medium;

    /// <summary>The smallest and the biggest version (1 to 40, 21 to 177 modules across) the code may be.</summary>
    public int MinVersion { get; set; } = 1;

    public int MaxVersion { get; set; } = 40;

    /// <summary>The mask (0 to 7) or -1 for the one with the lowest penalty.</summary>
    public int Mask { get; set; } = -1;

    /// <summary>Use a higher level of correction when the code has room for it anyway.</summary>
    public bool Boost { get; set; } = true;

    /// <summary>How the characters are packed: one of <see cref="Modes" />.</summary>
    public string Mode { get; set; } = "Auto";

    /// <summary>Mark the bytes as UTF-8 (an ECI header), for readers that would otherwise guess.</summary>
    public bool Eci { get; set; }

    /// <summary>The empty margin, in modules (readers want 4).</summary>
    public int Border { get; set; } = 4;

    /// <summary>The pixels to a module on the screen and in a PNG.</summary>
    public int Scale { get; set; } = 8;

    public string Dark { get; set; } = "#000000";

    public string Light { get; set; } = "#FFFFFF";

    [JsonIgnore]
    public string Text
    {
        get => Fields.TryGetValue("text", out var t) ? t : string.Empty;
        set => Fields["text"] = value;
    }

    public QrDoc Clone()
    {
        var copy = (QrDoc)MemberwiseClone();
        copy.Fields = new Dictionary<string, string>(Fields);
        return copy;
    }

    public static QrDoc ForText(string text, string name = "")
    {
        var doc = new QrDoc { Name = name };
        doc.Text = text;
        return doc;
    }

    /// <summary>A doc that says the text, as the kind it is recognized to be.</summary>
    public static QrDoc FromPayload(string text, string name = "")
    {
        var guess = QrPayloads.Detect(text);
        var doc   = new QrDoc { Name = name, Kind = guess.Kind, Fields = new Dictionary<string, string>(guess.Fields) };
        return doc;
    }

    /// <summary>The fields as they are for the kind, with those that are missing at their defaults.</summary>
    public void Normalize()
    {
        foreach (var f in QrPayloads.FieldsOf(Kind))
            if (!Fields.ContainsKey(f.Key))
                Fields[f.Key] = f.Default;

        MinVersion = Math.Clamp(MinVersion, QrCode.MinVersion, QrCode.MaxVersion);
        MaxVersion = Math.Clamp(MaxVersion, MinVersion, QrCode.MaxVersion);
        Mask       = Math.Clamp(Mask, -1, 7);
        Border     = Math.Clamp(Border, 0, 20);
        Scale      = Math.Clamp(Scale, 1, 64);
        if (!Modes.Contains(Mode)) Mode = "Auto";
    }

    public QrColors Colors => QrColors.From(Dark, Light);
}

/// <summary>What making the code for a doc came to.</summary>
/// <param name="Payload">What the code says, as text.</param>
/// <param name="Code">The code, or null when it could not be made (see <paramref name="Problem" />).</param>
/// <param name="Warnings">Things that will make the code hard to read.</param>
/// <param name="Check">The code read again by the decoder here, which is how it is known to say what it should.</param>
public sealed record QrBuild(string Payload, QrCode? Code, string? Problem, IReadOnlyList<QrSegment> Segments, IReadOnlyList<string> Warnings, QrDecodeResult? Check)
{
    public bool IsBuilt => Code is not null;

    /// <summary>The share of the code's data room that is used (0 to 1).</summary>
    public double Fill => Code is null ? 0 : (double)Code.DataBits / Code.CapacityBits;

    public int PayloadBytes => Encoding.UTF8.GetByteCount(Payload);

    public bool CheckPassed => Check is { IsOk: true } && Check.Value!.Text == Payload;

    /// <summary>The modes the segments are in, such as "Byte" or "Eci + Byte".</summary>
    public string ModeText => string.Join(" + ", Segments.Select(s => s.Mode.ToString()));
}

public static class QrBuilder
{
    /// <summary>Makes the code a doc describes. This does not throw: what is wrong is in <see cref="QrBuild.Problem" />.</summary>
    public static QrBuild Build(QrDoc doc, bool check = true)
    {
        doc.Normalize();
        var made = QrPayloads.Build(doc.Kind, doc.Fields);
        if (!made.IsOk) return new QrBuild(string.Empty, null, made.Problem, [], [], null);

        var payload = made.Text!;
        if (payload.Length == 0) return new QrBuild(payload, null, "There is nothing to put in the code yet.", [], [], null);

        QrMode? mode = doc.Mode switch
                       {
                           "Numeric"      => QrMode.Numeric,
                           "Alphanumeric" => QrMode.Alphanumeric,
                           "Byte"         => QrMode.Byte,
                           _              => null
                       };
        if (mode == QrMode.Numeric && !QrSegment.IsNumeric(payload)) return new QrBuild(payload, null, "Numeric mode takes digits only.", [], [], null);
        if (mode == QrMode.Alphanumeric && !QrSegment.IsAlphanumeric(payload))
            return new QrBuild(payload, null, "Alphanumeric mode takes only 0-9, A-Z (capitals), space and $ % * + - . / :", [], [], null);

        IReadOnlyList<QrSegment> segments;
        QrCode code;
        try
        {
            segments = QrSegment.ForText(payload, mode, doc.Eci);
            code     = QrCode.Encode(segments, doc.Ecc, doc.MinVersion, doc.MaxVersion, doc.Mask, doc.Boost);
        }
        catch (QrCapacityException ex)
        {
            return new QrBuild(payload, null, ex.Message, [], [], null);
        }
        catch (ArgumentException ex)
        {
            return new QrBuild(payload, null, ex.Message, [], [], null);
        }

        var warnings = new List<string>();
        var colors   = doc.Colors;
        if (colors.Contrast < 0.4) warnings.Add($"The two colors are too alike ({colors.Contrast:P0} apart in brightness); readers want at least 40%.");
        if (colors.IsInverted) warnings.Add("The modules are lighter than the background; some readers cannot read a code like that.");
        if (doc.Border < 4) warnings.Add($"The margin is {doc.Border} modules; readers want 4.");
        if (code.Version >= 25) warnings.Add($"Version {code.Version} is {code.Size} modules across: it needs a large print or a close camera.");
        if (doc.Eci && payload.All(c => c < 128)) warnings.Add("The text is all ASCII, so the UTF-8 marker is not needed.");
        if (doc.Ecc != code.Ecc) warnings.Add($"The level of correction was raised from {doc.Ecc} to {code.Ecc}: there was room.");

        QrDecodeResult? read = check ? QrDecoder.Decode(code.ToMatrix()) : null;
        return new QrBuild(payload, code, null, segments, warnings, read);
    }

    /// <summary>
    ///     How much text the code can hold at each level of correction and in each mode, for a version: (level, digits, letters and the few signs, bytes). Computed from the table of
    ///     codewords rather than kept as a second table.
    /// </summary>
    public static IEnumerable<(QrEcc Ecc, int Numeric, int Alphanumeric, int Bytes)> Capacity(int version)
    {
        foreach (var ecc in new[] { QrEcc.Low, QrEcc.Medium, QrEcc.Quartile, QrEcc.High })
        {
            long bits = QrCode.DataCodewords(version, ecc) * 8L;
            int Fit(QrMode mode)
            {
                int cc = QrSegment.CountBits(mode, version);
                long room = bits - 4 - cc;
                if (room < 0) return 0;
                int n = mode switch
                        {
                            QrMode.Numeric      => (int)(room / 10 * 3 + (room % 10 >= 7 ? 2 : room % 10 >= 4 ? 1 : 0)),
                            QrMode.Alphanumeric => (int)(room / 11 * 2 + (room % 11 >= 6 ? 1 : 0)),
                            _                   => (int)(room / 8)
                        };
                return Math.Min(n, (1 << cc) - 1);
            }

            yield return (ecc, Fit(QrMode.Numeric), Fit(QrMode.Alphanumeric), Fit(QrMode.Byte));
        }
    }

    /// <summary>The smallest version that holds the text at the level of correction given, or 0 when none does.</summary>
    public static int SmallestVersion(string text, QrEcc ecc)
    {
        try
        {
            return QrCode.EncodeText(text, ecc, boost: false).Version;
        }
        catch (QrCapacityException)
        {
            return 0;
        }
    }
}
