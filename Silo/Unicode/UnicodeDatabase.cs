using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace Silo.Unicode;

/// <summary>
///     The Unicode character database the Unicode Explorer browses: names, aliases, categories, blocks, scripts, ages,
///     emoji tags and so on for every
///     assigned code point. It is read from a compact file generated from the Unicode Character Database and the
///     CLDR-based emoji data and embedded
///     in the program, so it needs no network and no files besides the program.
/// </summary>
/// <remarks>
///     Code points whose names follow a rule (the CJK ideographs, Hangul syllables, Tangut...) are stored as one run, so
///     the file holds about 48 000 rows
///     for about 170 000 code points. Everything is read-only after it has been loaded, so the database can be shared
///     between threads.
/// </remarks>
public sealed partial class UnicodeDatabase
{
    /// <summary>The name the data file has among the program's embedded resources.</summary>
    public const string ResourceName = "silo-ucd.tsv.gz";

    private static readonly object           Gate = new();
    private static          UnicodeDatabase? _default;
    private readonly        UnicodeBlock[]   _blocks;

    private readonly Record[]                          _records;
    private readonly Dictionary<string, UnicodeScript> _scripts;
    private readonly int[]                             _starts;

    /// <summary>The Unicode versions that assigned code points (<c>1.1</c>, <c>2.0</c>... <c>18.0</c>), oldest first.</summary>
    public IReadOnlyList<string> Ages { get; }

    /// <summary>How many characters are assigned (not counting private-use code points, surrogates or noncharacters).</summary>
    public int AssignedCount { get; }

    /// <summary>The blocks of the code space, in order.</summary>
    public IReadOnlyList<UnicodeBlock> Blocks { get; }

    /// <summary>
    ///     The database that ships with the program, loaded the first time it is asked for (about a tenth of a second).
    /// </summary>
    /// <exception cref="InvalidOperationException">The data file is missing from the program: a build problem.</exception>
    public static UnicodeDatabase Default
    {
        get
        {
            if (_default is not null) return _default;
            lock (Gate)
            {
                return _default ??= LoadEmbedded();
            }
        }
    }

    /// <summary>The scripts that appear in the data, by name.</summary>
    public IReadOnlyList<UnicodeScript> Scripts { get; }

    /// <summary>The Unicode version of the data: <c>18.0.0</c>.</summary>
    public string Version { get; }

    internal IReadOnlyList<Record> RecordList => _records;

    // ---- loading -----------------------------------------------------------------------------------

    private static UnicodeDatabase LoadEmbedded()
    {
        using var stream = typeof(UnicodeDatabase).Assembly.GetManifestResourceStream(ResourceName) ??
                           throw new InvalidOperationException($"The Unicode data ({ResourceName}) is not embedded in the program.");
        return Load(stream);
    }

    /// <summary>Reads the database from the gzip-compressed data file.</summary>
    public static UnicodeDatabase Load(Stream compressed)
    {
        using var gz     = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, new UTF8Encoding(false));
        return Parse(reader);
    }

    /// <summary>Reads the database from the data file's text.</summary>
    /// <exception cref="FormatException">The text is not the data file.</exception>
    public static UnicodeDatabase Parse(TextReader reader)
    {
        string version = string.Empty;
        var    blocks  = new List<UnicodeBlock>();
        var    scripts = new Dictionary<string, UnicodeScript>(StringComparer.OrdinalIgnoreCase);
        var    records = new List<Record>(50000);
        var    pool    = new Dictionary<string, string>(StringComparer.Ordinal);

        string Pool(string s)
        {
            if (s.Length == 0) return string.Empty;
            if (pool.TryGetValue(s, out string? held)) return held;
            pool[s] = s;
            return s;
        }

        string[] Split(string s, char sep)
        {
            return s.Length == 0 ? [] : s.Split(sep).Select(Pool).ToArray();
        }

        int lineNo = 0;
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            lineNo++;
            if (line.Length == 0) continue;

            var f = line.Split('\t');
            if (line[0] == '#')
            {
                switch (f[0])
                {
                    case "#UCD" when f.Length >= 2:
                        version = f[1];
                        break;
                    case "#B" when f.Length >= 4:
                        blocks.Add(new UnicodeBlock(Hex(f[1], lineNo), Hex(f[2], lineNo), f[3]));
                        break;
                    case "#S" when f.Length >= 3:
                        scripts[f[1]] = new UnicodeScript(f[1], f[2]);
                        break;
                }

                continue;
            }

            if (f.Length < 15) throw new FormatException($"Line {lineNo} of the Unicode data has {f.Length} columns, not 15.");

            int dash  = f[0].IndexOf('-');
            int start = Hex(dash < 0 ? f[0] : f[0][..dash], lineNo);
            int end   = dash < 0 ? start : Hex(f[0][(dash + 1)..], lineNo);

            var flags = UnicodeFlags.None;
            foreach (char c in f[6])
                flags |= c switch
                         {
                             'M' => UnicodeFlags.Mirrored,
                             'E' => UnicodeFlags.Emoji,
                             'P' => UnicodeFlags.EmojiPresentation,
                             'T' => UnicodeFlags.EmojiModifier,
                             'X' => UnicodeFlags.Pictographic,
                             _   => UnicodeFlags.None
                         };

            string nameField = f[1];
            var    rule      = NameRule.Literal;
            string name      = nameField;
            if (nameField.StartsWith("=@", StringComparison.Ordinal))
            {
                rule = nameField switch
                       {
                           "=@HANGUL" => NameRule.Hangul,
                           "=@PUA" => NameRule.PrivateUse,
                           "=@SURR" => NameRule.Surrogate,
                           "=@NONCHAR" => NameRule.Noncharacter,
                           _ => throw new FormatException($"Line {lineNo} of the Unicode data has the unknown name rule {nameField}.")
                       };
                name = string.Empty;
            }
            else if (nameField.StartsWith('='))
            {
                rule = NameRule.Prefix;
                name = nameField[1..];
            }

            records.Add(new Record
                        {
                            Start         = start,
                            End           = end,
                            Name          = name,
                            Rule          = rule,
                            Category      = Pool(f[2]),
                            Ccc           = byte.Parse(f[3], CultureInfo.InvariantCulture),
                            Bidi          = Pool(f[4]),
                            Eaw           = Pool(f[5]),
                            Flags         = flags,
                            ScriptCode    = Pool(f[7]),
                            Age           = Pool(f[8]),
                            Numeric       = f[9],
                            Decomposition = f[10],
                            Aliases       = Split(f[11], '|'),
                            Keywords      = Split(f[12], ','),
                            Entities      = Split(f[13], '|'),
                            EmojiGroup    = Pool(f[14])
                        });
        }

        if (version.Length == 0 || records.Count == 0) throw new FormatException("This is not the Unicode data file.");

        records.Sort((a, b) => a.Start.CompareTo(b.Start));
        blocks.Sort((a,  b) => a.Start.CompareTo(b.Start));
        return new UnicodeDatabase(version, records.ToArray(), blocks.ToArray(), scripts);
    }

    private static int Hex(string s, int line)
    {
        if (!int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v))
            throw new FormatException($"Line {line} of the Unicode data has \"{s}\" where a code point is expected.");
        return v;
    }

    // ---- lookup ------------------------------------------------------------------------------------

    /// <summary>
    ///     What the database knows about a code point (an unassigned one comes back with
    ///     <see cref="UnicodeCharacter.IsKnown" /> false).
    /// </summary>
    public UnicodeCharacter Get(int codePoint)
    {
        if ((uint)codePoint > CodePoint.MaxValue) throw new ArgumentOutOfRangeException(nameof(codePoint));
        return new UnicodeCharacter(this, codePoint, FindRecord(codePoint));
    }

    /// <inheritdoc cref="Get(int)" />
    public UnicodeCharacter Get(CodePoint codePoint)
    {
        return Get(codePoint.Value);
    }

    /// <summary>The block a code point lies in; null when it lies in none.</summary>
    public UnicodeBlock? BlockOf(int codePoint)
    {
        int lo = 0, hi = _blocks.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            var b   = _blocks[mid];
            if (codePoint      < b.Start) hi = mid - 1;
            else if (codePoint > b.End) lo   = mid + 1;
            else return b;
        }

        return null;
    }

    /// <summary>The name of a script from its code; the code itself when the database does not know it.</summary>
    public string ScriptName(string code)
    {
        return _scripts.TryGetValue(code, out var s) ? s.Name : code;
    }

    /// <summary>
    ///     Looks a character up by its name or alias (case and punctuation do not matter): <c>"large green circle"</c>,
    ///     <c>"nbsp"</c>.
    /// </summary>
    public UnicodeCharacter? FindByName(string? name)
    {
        string wanted = UnicodeNames.Normalize(name);
        if (wanted.Trim().Length == 0) return null;

        foreach (var r in _records)
            if (r.Rule == NameRule.Literal)
            {
                if (UnicodeNames.Normalize(r.Name) == wanted || r.Aliases.Any(a => UnicodeNames.Normalize(a) == wanted))
                    return new UnicodeCharacter(this, r.Start, r);
            }
            else if (r.Rule == NameRule.Prefix && wanted.StartsWith(UnicodeNames.Normalize(r.Name)[..^1], StringComparison.Ordinal))
            {
                // CJK UNIFIED IDEOGRAPH-4E00: the part after the prefix is the code point
                string tail = wanted[(UnicodeNames.Normalize(r.Name).Length - 1)..].Trim();
                if (int.TryParse(tail, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v) && v >= r.Start &&
                    v                                                                                                <= r.End)
                    return new UnicodeCharacter(this, v, r);
            }
            else if (r.Rule == NameRule.Hangul)
            {
                for (int cp = r.Start; cp <= r.End; cp++)
                    if (UnicodeNames.Normalize(UnicodeNames.HangulSyllableName(cp)) == wanted)
                        return new UnicodeCharacter(this, cp, r);
            }

        return null;
    }

    /// <summary>
    ///     Every code point the database has a record for (assigned characters, and private-use, surrogate and
    ///     noncharacter code points), in order.
    /// </summary>
    public IEnumerable<int> KnownCodePoints()
    {
        foreach (var r in _records)
            for (int cp = r.Start; cp <= r.End; cp++)
                yield return cp;
    }

    internal Record? FindRecord(int codePoint)
    {
        int idx          = Array.BinarySearch(_starts, codePoint);
        if (idx < 0) idx = ~idx - 1;
        if (idx < 0) return null;

        var r = _records[idx];
        return codePoint <= r.End ? r : null;
    }

    internal static int AgeKey(string? age)
    {
        if (string.IsNullOrEmpty(age)) return -1;

        string[] p = age.Split('.');
        if (!int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major)) return -1;
        int minor = p.Length > 1 && int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out int m) ? m : 0;
        return major * 100 + minor;
    }

    private UnicodeDatabase(string version, Record[] records, UnicodeBlock[] blocks, Dictionary<string, UnicodeScript> scripts)
    {
        Version  = version;
        _records = records;
        _starts  = records.Select(r => r.Start).ToArray();
        _blocks  = blocks;
        _scripts = scripts;

        Blocks        = blocks;
        Scripts       = scripts.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Ages          = records.Select(r => r.Age).Where(a => a.Length > 0).Distinct().OrderBy(a => AgeKey(a)).ToList();
        AssignedCount = records.Where(r => r.Category is not ("Cn" or "Co" or "Cs")).Sum(r => r.End - r.Start + 1);
    }

    // ---- rows ----------------------------------------------------------------------------------------

    internal enum NameRule
    {
        Literal,
        Prefix,
        Hangul,
        PrivateUse,
        Surrogate,
        Noncharacter
    }

    [Flags]
    internal enum UnicodeFlags
    {
        None              = 0,
        Mirrored          = 1,
        Emoji             = 2,
        EmojiPresentation = 4,
        EmojiModifier     = 8,
        Pictographic      = 16
    }

    internal sealed class Record
    {
        internal string? NormName;

        internal string?      SearchText;
        private  int?         _ageValue;
        public   string       Age           { get; init; } = string.Empty;
        public   string[]     Aliases       { get; init; } = [];
        public   string       Bidi          { get; init; } = "L";
        public   string       Category      { get; init; } = "Cn";
        public   byte         Ccc           { get; init; }
        public   string       Decomposition { get; init; } = string.Empty;
        public   string       Eaw           { get; init; } = "N";
        public   string       EmojiGroup    { get; init; } = string.Empty;
        public   int          End           { get; init; }
        public   string[]     Entities      { get; init; } = [];
        public   UnicodeFlags Flags         { get; init; }
        public   string[]     Keywords      { get; init; } = [];
        public   string       Name          { get; init; } = string.Empty;
        public   string       Numeric       { get; init; } = string.Empty;
        public   NameRule     Rule          { get; init; }
        public   string       ScriptCode    { get; init; } = "Zzzz";
        public   int          Start         { get; init; }
        internal int          AgeValue      => _ageValue ??= AgeKey(Age);

        public string NameOf(int cp)
        {
            return Rule switch
                   {
                       NameRule.Literal      => Name,
                       NameRule.Prefix       => Name + cp.ToString("X4", CultureInfo.InvariantCulture),
                       NameRule.Hangul       => UnicodeNames.HangulSyllableName(cp),
                       NameRule.PrivateUse   => UnicodeNames.Label("private-use",  cp),
                       NameRule.Surrogate    => UnicodeNames.Label("surrogate",    cp),
                       NameRule.Noncharacter => UnicodeNames.Label("noncharacter", cp),
                       _                     => Name
                   };
        }
    }
}