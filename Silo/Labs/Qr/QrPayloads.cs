using System.Globalization;
using System.Text;

namespace Silo.Labs.Qr;

/// <summary>The kinds of thing phones know to do with a code, each written in a form they recognize.</summary>
public enum QrPayloadKind
{
    Text,
    Url,
    Wifi,
    Email,
    Phone,
    Sms,
    Geo,
    Contact,
    Otp,
    Event
}

/// <summary>One thing to fill in for a kind of payload.</summary>
/// <param name="Key">What it is called in the fields.</param>
/// <param name="Choices">The values to pick from, if there are only a few.</param>
public sealed record QrField(string Key, string Title, string Hint = "", string Default = "", IReadOnlyList<string>? Choices = null, bool Multiline = false, bool Required = false);

/// <summary>A payload made, or why it could not be.</summary>
public sealed record QrPayloadResult(string? Text, string? Problem)
{
    public bool IsOk => Text is not null;
    public static QrPayloadResult Ok(string text) => new(text, null);
    public static QrPayloadResult Fail(string problem) => new(null, problem);
}

/// <summary>What a payload was found to be, and its fields.</summary>
public sealed record QrPayloadGuess(QrPayloadKind Kind, IReadOnlyDictionary<string, string> Fields);

/// <summary>Writing and reading the texts that phones treat as something other than text.</summary>
public static class QrPayloads
{
    public static string TitleOf(QrPayloadKind kind) => kind switch
    {
        QrPayloadKind.Text    => "Plain text",
        QrPayloadKind.Url     => "Web address",
        QrPayloadKind.Wifi    => "Wi-Fi network",
        QrPayloadKind.Email   => "Email",
        QrPayloadKind.Phone   => "Phone call",
        QrPayloadKind.Sms     => "Text message",
        QrPayloadKind.Geo     => "Location",
        QrPayloadKind.Contact => "Contact (vCard)",
        QrPayloadKind.Otp     => "One-time password",
        QrPayloadKind.Event   => "Calendar event",
        _                     => kind.ToString()
    };

    public static IReadOnlyList<QrField> FieldsOf(QrPayloadKind kind) => kind switch
    {
        QrPayloadKind.Text => [new("text", "Text", "anything", Multiline: true)],
        QrPayloadKind.Url  => [new("url", "Address", "https://example.com/page", Required: true)],
        QrPayloadKind.Wifi =>
        [
            new("ssid", "Network name", "", Required: true),
            new("password", "Password"),
            new("security", "Security", "", "WPA", ["WPA", "WEP", "nopass"]),
            new("hidden", "Hidden network", "", "false", ["false", "true"])
        ],
        QrPayloadKind.Email =>
        [
            new("to", "To", "someone@example.com", Required: true),
            new("subject", "Subject"),
            new("body", "Message", Multiline: true)
        ],
        QrPayloadKind.Phone => [new("number", "Number", "+15551234567", Required: true)],
        QrPayloadKind.Sms =>
        [
            new("number", "Number", "+15551234567", Required: true),
            new("message", "Message", Multiline: true)
        ],
        QrPayloadKind.Geo =>
        [
            new("lat", "Latitude", "-90 to 90", Required: true),
            new("lon", "Longitude", "-180 to 180", Required: true),
            new("query", "Label", "shown by some maps")
        ],
        QrPayloadKind.Contact =>
        [
            new("first", "First name"),
            new("last", "Last name"),
            new("org", "Organization"),
            new("title", "Title"),
            new("phone", "Phone"),
            new("email", "Email"),
            new("url", "Web address"),
            new("street", "Street"),
            new("city", "City"),
            new("region", "State or region"),
            new("postal", "Postal code"),
            new("country", "Country"),
            new("note", "Note", Multiline: true)
        ],
        QrPayloadKind.Otp =>
        [
            new("type", "Kind", "", "totp", ["totp", "hotp"]),
            new("label", "Account", "name@example.com", Required: true),
            new("issuer", "Issuer", "who it is for"),
            new("secret", "Secret", "base32, as the service shows it", Required: true),
            new("algorithm", "Algorithm", "", "SHA1", ["SHA1", "SHA256", "SHA512"]),
            new("digits", "Digits", "", "6", ["6", "7", "8"]),
            new("period", "Seconds a code lasts", "totp only", "30"),
            new("counter", "Counter", "hotp only", "0")
        ],
        QrPayloadKind.Event =>
        [
            new("summary", "Title", Required: true),
            new("start", "Starts", "2026-10-31 19:30, or 2026-10-31 for a day", Required: true),
            new("end", "Ends", "the same way; empty for the same time"),
            new("location", "Place"),
            new("description", "Details", Multiline: true)
        ],
        _ => []
    };

    /// <summary>Fills the fields of a kind with their defaults.</summary>
    public static Dictionary<string, string> Defaults(QrPayloadKind kind) => FieldsOf(kind).ToDictionary(f => f.Key, f => f.Default);

    static string Get(IReadOnlyDictionary<string, string> fields, string key) => fields.TryGetValue(key, out var v) ? v.Trim() : "";

    // ---------------------------------------------------------------- writing

    public static QrPayloadResult Build(QrPayloadKind kind, IReadOnlyDictionary<string, string> fields)
    {
        foreach (var f in FieldsOf(kind))
            if (f.Required && Get(fields, f.Key).Length == 0)
                return QrPayloadResult.Fail($"{f.Title} is needed.");

        switch (kind)
        {
            case QrPayloadKind.Text:
                return QrPayloadResult.Ok(fields.TryGetValue("text", out var t) ? t : "");

            case QrPayloadKind.Url:
            {
                var url = Get(fields, "url");
                if (!url.Contains("://") && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
                if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return QrPayloadResult.Fail("That is not an address.");
                return QrPayloadResult.Ok(url);
            }

            case QrPayloadKind.Wifi:
            {
                var security = Get(fields, "security");
                if (security.Length == 0) security = "WPA";
                var sb = new StringBuilder("WIFI:");
                sb.Append("T:").Append(security == "nopass" ? "nopass" : security).Append(';');
                sb.Append("S:").Append(WifiEscape(Get(fields, "ssid"))).Append(';');
                if (security != "nopass" && Get(fields, "password").Length > 0) sb.Append("P:").Append(WifiEscape(fields["password"].Trim())).Append(';');
                if (Get(fields, "hidden") == "true") sb.Append("H:true;");
                sb.Append(';');
                return QrPayloadResult.Ok(sb.ToString());
            }

            case QrPayloadKind.Email:
            {
                var sb = new StringBuilder("mailto:").Append(Get(fields, "to"));
                var query = new List<string>();
                if (Get(fields, "subject").Length > 0) query.Add("subject=" + Uri.EscapeDataString(fields["subject"].Trim()));
                if (Get(fields, "body").Length > 0) query.Add("body=" + Uri.EscapeDataString(fields["body"].Trim()));
                if (query.Count > 0) sb.Append('?').Append(string.Join('&', query));
                return QrPayloadResult.Ok(sb.ToString());
            }

            case QrPayloadKind.Phone:
                return QrPayloadResult.Ok("tel:" + Get(fields, "number").Replace(" ", ""));

            case QrPayloadKind.Sms:
                return QrPayloadResult.Ok($"SMSTO:{Get(fields, "number").Replace(" ", "")}:{Get(fields, "message")}");

            case QrPayloadKind.Geo:
            {
                if (!double.TryParse(Get(fields, "lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) || lat is < -90 or > 90) return QrPayloadResult.Fail("Latitude is a number from -90 to 90.");
                if (!double.TryParse(Get(fields, "lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) || lon is < -180 or > 180) return QrPayloadResult.Fail("Longitude is a number from -180 to 180.");
                var text = $"geo:{lat.ToString("0.######", CultureInfo.InvariantCulture)},{lon.ToString("0.######", CultureInfo.InvariantCulture)}";
                if (Get(fields, "query").Length > 0) text += "?q=" + Uri.EscapeDataString(fields["query"].Trim());
                return QrPayloadResult.Ok(text);
            }

            case QrPayloadKind.Contact:
                return BuildContact(fields);

            case QrPayloadKind.Otp:
                return BuildOtp(fields);

            case QrPayloadKind.Event:
                return BuildEvent(fields);

            default:
                return QrPayloadResult.Fail("Nothing is known of that kind.");
        }
    }

    static string WifiEscape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c is '\\' or ';' or ',' or ':' or '"') sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    static string VEscape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");

    static QrPayloadResult BuildContact(IReadOnlyDictionary<string, string> f)
    {
        string first = Get(f, "first"), last = Get(f, "last"), org = Get(f, "org");
        if (first.Length == 0 && last.Length == 0 && org.Length == 0) return QrPayloadResult.Fail("A contact needs a name or an organization.");
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCARD\r\nVERSION:3.0\r\n");
        sb.Append($"N:{VEscape(last)};{VEscape(first)};;;\r\n");
        sb.Append($"FN:{VEscape((first + " " + last).Trim().Length > 0 ? (first + " " + last).Trim() : org)}\r\n");
        if (org.Length > 0) sb.Append($"ORG:{VEscape(org)}\r\n");
        if (Get(f, "title").Length > 0) sb.Append($"TITLE:{VEscape(Get(f, "title"))}\r\n");
        if (Get(f, "phone").Length > 0) sb.Append($"TEL:{VEscape(Get(f, "phone"))}\r\n");
        if (Get(f, "email").Length > 0) sb.Append($"EMAIL:{VEscape(Get(f, "email"))}\r\n");
        if (Get(f, "url").Length > 0) sb.Append($"URL:{Get(f, "url")}\r\n");
        if (new[] { "street", "city", "region", "postal", "country" }.Any(k => Get(f, k).Length > 0))
            sb.Append($"ADR:;;{VEscape(Get(f, "street"))};{VEscape(Get(f, "city"))};{VEscape(Get(f, "region"))};{VEscape(Get(f, "postal"))};{VEscape(Get(f, "country"))}\r\n");
        if (Get(f, "note").Length > 0) sb.Append($"NOTE:{VEscape(Get(f, "note"))}\r\n");
        sb.Append("END:VCARD");
        return QrPayloadResult.Ok(sb.ToString());
    }

    static readonly char[] Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".ToCharArray();

    /// <summary>Whether text is base32 (spaces, dashes and case are ignored) and what it is without them.</summary>
    public static bool TryCleanBase32(string text, out string clean)
    {
        clean = new string(text.Where(c => c is not (' ' or '-' or '=')).Select(char.ToUpperInvariant).ToArray());
        return clean.Length > 0 && clean.All(Base32.Contains);
    }

    static QrPayloadResult BuildOtp(IReadOnlyDictionary<string, string> f)
    {
        if (!TryCleanBase32(Get(f, "secret"), out var secret)) return QrPayloadResult.Fail("The secret is letters A to Z and digits 2 to 7.");
        var type = Get(f, "type") is "hotp" ? "hotp" : "totp";
        var label = Get(f, "label");
        var issuer = Get(f, "issuer");
        var path = issuer.Length > 0 ? Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(label) : Uri.EscapeDataString(label);
        var sb = new StringBuilder($"otpauth://{type}/{path}?secret={secret}");
        if (issuer.Length > 0) sb.Append("&issuer=").Append(Uri.EscapeDataString(issuer));
        var algorithm = Get(f, "algorithm");
        if (algorithm.Length > 0 && algorithm != "SHA1") sb.Append("&algorithm=").Append(algorithm);
        var digits = Get(f, "digits");
        if (digits.Length > 0 && digits != "6")
        {
            if (!int.TryParse(digits, out var d) || d is < 6 or > 8) return QrPayloadResult.Fail("Digits is 6, 7 or 8.");
            sb.Append("&digits=").Append(d);
        }

        if (type == "totp")
        {
            var period = Get(f, "period");
            if (period.Length > 0 && period != "30")
            {
                if (!int.TryParse(period, out var p) || p <= 0) return QrPayloadResult.Fail("A code lasts a whole number of seconds.");
                sb.Append("&period=").Append(p);
            }
        }
        else
        {
            var counter = Get(f, "counter");
            if (!long.TryParse(counter.Length == 0 ? "0" : counter, out var c) || c < 0) return QrPayloadResult.Fail("The counter is a whole number, not below 0.");
            sb.Append("&counter=").Append(c);
        }

        return QrPayloadResult.Ok(sb.ToString());
    }

    static readonly string[] DateFormats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"];

    static bool TryDate(string text, out DateTime date, out bool dateOnly)
    {
        dateOnly = false;
        date     = default;
        text     = text.Trim();
        if (!DateTime.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return false;
        dateOnly = text.Length <= 10;
        return true;
    }

    static QrPayloadResult BuildEvent(IReadOnlyDictionary<string, string> f)
    {
        if (!TryDate(Get(f, "start"), out var start, out var startDay)) return QrPayloadResult.Fail("The start is a date like 2026-10-31 or a date and time like 2026-10-31 19:30.");
        DateTime end = start;
        bool endDay  = startDay;
        if (Get(f, "end").Length > 0)
        {
            if (!TryDate(Get(f, "end"), out end, out endDay)) return QrPayloadResult.Fail("The end is written like the start.");
            if (endDay != startDay) return QrPayloadResult.Fail("The start and the end are both days or both times.");
            if (end < start) return QrPayloadResult.Fail("The event ends before it starts.");
        }

        string Stamp(DateTime d, bool day) => day ? d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : d.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\n");
        sb.Append($"SUMMARY:{VEscape(Get(f, "summary"))}\r\n");
        if (startDay)
        {
            // a day's event ends the day after it (the end of a day-long event is not included)
            var last = endDay ? end.AddDays(1) : start.AddDays(1);
            sb.Append($"DTSTART;VALUE=DATE:{Stamp(start, true)}\r\nDTEND;VALUE=DATE:{Stamp(last, true)}\r\n");
        }
        else
            sb.Append($"DTSTART:{Stamp(start, false)}\r\nDTEND:{Stamp(end, false)}\r\n");

        if (Get(f, "location").Length > 0) sb.Append($"LOCATION:{VEscape(Get(f, "location"))}\r\n");
        if (Get(f, "description").Length > 0) sb.Append($"DESCRIPTION:{VEscape(Get(f, "description"))}\r\n");
        sb.Append("END:VEVENT\r\nEND:VCALENDAR");
        return QrPayloadResult.Ok(sb.ToString());
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Works out what a text is meant to be. Anything that is not recognized is plain text.</summary>
    public static QrPayloadGuess Detect(string text)
    {
        var t = text.Trim();
        QrPayloadGuess Plain() => new(QrPayloadKind.Text, new Dictionary<string, string> { ["text"] = text });
        if (t.Length == 0) return Plain();

        if (t.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase)) return DetectWifi(t[5..]) ?? Plain();
        if (t.StartsWith("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)) return DetectContact(t) ?? Plain();
        if (t.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) || t.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) return DetectEvent(t) ?? Plain();
        if (t.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase)) return DetectOtp(t) ?? Plain();
        if (t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return DetectEmail(t[7..]);
        if (t.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return new(QrPayloadKind.Phone, new Dictionary<string, string> { ["number"] = t[4..] });
        if (t.StartsWith("smsto:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("sms:", StringComparison.OrdinalIgnoreCase)) return DetectSms(t) ?? Plain();
        if (t.StartsWith("geo:", StringComparison.OrdinalIgnoreCase)) return DetectGeo(t[4..]) ?? Plain();
        if ((t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) && !t.Any(char.IsWhiteSpace)
            && Uri.TryCreate(t, UriKind.Absolute, out _))
            return new(QrPayloadKind.Url, new Dictionary<string, string> { ["url"] = t });
        return Plain();
    }

    /// <summary>Splits at a character that is not escaped with a backslash, and takes the backslashes out of the pieces.</summary>
    static List<string> SplitEscaped(string text, char separator)
    {
        var parts = new List<string>();
        var sb    = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length) { sb.Append(text[++i]); continue; }
            if (c == separator) { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }

        parts.Add(sb.ToString());
        return parts;
    }

    static QrPayloadGuess? DetectWifi(string body)
    {
        var fields = Defaults(QrPayloadKind.Wifi);
        bool any   = false;
        foreach (var part in SplitEscaped(body, ';'))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            var value = part[(colon + 1)..];
            switch (part[..colon].ToUpperInvariant())
            {
                case "S": fields["ssid"] = value; any = true; break;
                case "P": fields["password"] = value; break;
                case "T": fields["security"] = value.Length == 0 ? "nopass" : value; break;
                case "H": fields["hidden"] = value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false"; break;
            }
        }

        return any ? new(QrPayloadKind.Wifi, fields) : null;
    }

    static QrPayloadGuess DetectEmail(string rest)
    {
        var fields = Defaults(QrPayloadKind.Email);
        int q      = rest.IndexOf('?');
        fields["to"] = Uri.UnescapeDataString(q < 0 ? rest : rest[..q]);
        if (q >= 0)
            foreach (var pair in rest[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = pair[..eq].ToLowerInvariant();
                if (key is "subject" or "body") fields[key] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }

        return new(QrPayloadKind.Email, fields);
    }

    static QrPayloadGuess? DetectSms(string t)
    {
        var rest   = t[(t.IndexOf(':') + 1)..];
        var fields = Defaults(QrPayloadKind.Sms);
        int q      = rest.IndexOf('?');
        int colon  = rest.IndexOf(':');
        if (t.StartsWith("smsto:", StringComparison.OrdinalIgnoreCase) && colon >= 0)
        {
            fields["number"]  = rest[..colon];
            fields["message"] = rest[(colon + 1)..];
        }
        else if (q >= 0)
        {
            fields["number"] = rest[..q];
            foreach (var pair in rest[(q + 1)..].Split('&'))
                if (pair.StartsWith("body=", StringComparison.OrdinalIgnoreCase)) fields["message"] = Uri.UnescapeDataString(pair[5..]);
        }
        else
            fields["number"] = rest;

        return new(QrPayloadKind.Sms, fields);
    }

    static QrPayloadGuess? DetectGeo(string rest)
    {
        var fields = Defaults(QrPayloadKind.Geo);
        int q      = rest.IndexOf('?');
        var coords = (q < 0 ? rest : rest[..q]).Split(',');
        if (coords.Length < 2) return null;
        if (!double.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _) || !double.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return null;
        fields["lat"] = coords[0];
        fields["lon"] = coords[1];
        if (q >= 0)
            foreach (var pair in rest[(q + 1)..].Split('&'))
                if (pair.StartsWith("q=", StringComparison.OrdinalIgnoreCase)) fields["query"] = Uri.UnescapeDataString(pair[2..]);
        return new(QrPayloadKind.Geo, fields);
    }

    /// <summary>The lines of a vCard or an event with folded lines put back together, as (name without its parameters, value).</summary>
    static List<(string Name, string Value)> ContentLines(string text)
    {
        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var lines = new List<string>();
        foreach (var l in raw)
        {
            if ((l.StartsWith(' ') || l.StartsWith('\t')) && lines.Count > 0) lines[^1] += l[1..];
            else lines.Add(l);
        }

        var result = new List<(string, string)>();
        foreach (var l in lines)
        {
            int colon = l.IndexOf(':');
            if (colon <= 0) continue;
            var name = l[..colon];
            int semi = name.IndexOf(';');
            if (semi >= 0) name = name[..semi];
            result.Add((name.ToUpperInvariant(), l[(colon + 1)..]));
        }

        return result;
    }

    static string VUnescape(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                sb.Append(n is 'n' or 'N' ? '\n' : n);
            }
            else
                sb.Append(s[i]);
        }

        return sb.ToString();
    }

    static QrPayloadGuess? DetectContact(string text)
    {
        var fields = Defaults(QrPayloadKind.Contact);
        bool any   = false;
        foreach (var (name, value) in ContentLines(text))
        {
            switch (name)
            {
                case "N":
                {
                    var parts = SplitEscaped(value, ';');
                    if (parts.Count > 0) fields["last"] = parts[0];
                    if (parts.Count > 1) fields["first"] = parts[1];
                    any = true;
                    break;
                }
                case "FN" when fields["first"].Length == 0 && fields["last"].Length == 0:
                {
                    var parts = VUnescape(value).Split(' ', 2);
                    fields["first"] = parts[0];
                    if (parts.Length > 1) fields["last"] = parts[1];
                    any = true;
                    break;
                }
                case "ORG": fields["org"] = SplitEscaped(value, ';')[0]; any = true; break;
                case "TITLE": fields["title"] = VUnescape(value); break;
                case "TEL" when fields["phone"].Length == 0: fields["phone"] = VUnescape(value); break;
                case "EMAIL" when fields["email"].Length == 0: fields["email"] = VUnescape(value); break;
                case "URL": fields["url"] = value; break;
                case "NOTE": fields["note"] = VUnescape(value); break;
                case "ADR":
                {
                    var p = SplitEscaped(value, ';');
                    string At(int i) => i < p.Count ? p[i] : "";
                    fields["street"] = At(2); fields["city"] = At(3); fields["region"] = At(4); fields["postal"] = At(5); fields["country"] = At(6);
                    break;
                }
            }
        }

        return any ? new(QrPayloadKind.Contact, fields) : null;
    }

    static QrPayloadGuess? DetectEvent(string text)
    {
        var fields = Defaults(QrPayloadKind.Event);
        bool any   = false;

        string Back(string value)
        {
            bool day = value.Length == 8;
            if (!DateTime.TryParseExact(value.TrimEnd('Z'), day ? "yyyyMMdd" : "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return value;
            return day ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        foreach (var (name, value) in ContentLines(text))
        {
            switch (name)
            {
                case "SUMMARY": fields["summary"] = VUnescape(value); any = true; break;
                case "DTSTART": fields["start"] = Back(value); any = true; break;
                case "DTEND": fields["end"] = Back(value); break;
                case "LOCATION": fields["location"] = VUnescape(value); break;
                case "DESCRIPTION": fields["description"] = VUnescape(value); break;
            }
        }

        // a day's end is the day after the last day; show the last day
        if (fields["start"].Length == 10 && fields["end"].Length == 10
            && DateTime.TryParseExact(fields["end"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e))
        {
            var last = e.AddDays(-1);
            fields["end"] = DateTime.TryParseExact(fields["start"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var s) && last <= s ? "" : last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return any ? new(QrPayloadKind.Event, fields) : null;
    }

    static QrPayloadGuess? DetectOtp(string t)
    {
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)) return null;
        var type = uri.Host.ToLowerInvariant();
        if (type is not ("totp" or "hotp")) return null;
        var fields = Defaults(QrPayloadKind.Otp);
        fields["type"] = type;
        var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        int colon = path.IndexOf(':');
        if (colon >= 0) { fields["issuer"] = path[..colon].Trim(); path = path[(colon + 1)..].Trim(); }
        fields["label"] = path;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            switch (pair[..eq].ToLowerInvariant())
            {
                case "secret": fields["secret"] = value; break;
                case "issuer": fields["issuer"] = value; break;
                case "algorithm": fields["algorithm"] = value.ToUpperInvariant(); break;
                case "digits": fields["digits"] = value; break;
                case "period": fields["period"] = value; break;
                case "counter": fields["counter"] = value; break;
            }
        }

        return fields["secret"].Length > 0 ? new(QrPayloadKind.Otp, fields) : null;
    }
}
