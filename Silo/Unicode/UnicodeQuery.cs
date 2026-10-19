using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Silo.Unicode;

/// <summary>
///     What to look for in the Unicode database: free text with optional filters. The filters can also be written inside the text, so a person can type
///     <c>arrow cat:Sm block:arrows age:&gt;=6.0 is:mirrored</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Free text.</b> Every word has to appear in the name, an alias, a tag or an HTML entity name of the character (case, accents of punctuation
///         and hyphens do not matter): <c>green circle</c>. A phrase in quotes has to appear as written. <c>U+1F7E2</c>, <c>0x1F7E2</c>, <c>\u{1F7E2}</c> and
///         <c>&amp;#128994;</c> look a code point up, <c>U+1F600-1F64F</c> a range, and a bare run of hex digits (<c>1F7E2</c>) is a code point as well as a word.
///         Any word with a character outside ASCII (<c>🟢</c>, <c>é</c>) looks up that character.
///     </para>
///     <para>
///         <b>Qualifiers</b> (<c>name:value</c>): <c>cat:Lu</c> (or <c>cat:L</c>), <c>block:arrows</c>, <c>script:latn</c>, <c>age:6.0</c> (also
///         <c>&gt;=</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&lt;</c>), <c>plane:1</c>, <c>bidi:AL</c>, <c>is:emoji</c>, <c>is:assigned</c>, <c>is:mirrored</c>,
///         <c>is:combining</c>, <c>is:ascii</c>. A qualifier that names nothing that exists (<c>block:nonsense</c>) makes the result empty.
///     </para>
/// </remarks>
public sealed record UnicodeQuery
{
    /// <summary>The free text, with qualifiers if wanted.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Two-letter categories (<c>Lu</c>) or one-letter groups (<c>L</c>); a character has to be in one of them. Empty: any.</summary>
    public IReadOnlyCollection<string> Categories { get; init; } = [];

    /// <summary>The name of a block, as <see cref="UnicodeDatabase.Blocks" /> has it; null: any.</summary>
    public string? Block { get; init; }

    /// <summary>The four-letter code of a script; null: any.</summary>
    public string? Script { get; init; }

    /// <summary>The Unicode version that assigned the character, <c>6.0</c>, or a comparison such as <c>&gt;=6.0</c>; null: any.</summary>
    public string? Age { get; init; }

    /// <summary>Only characters that have the Emoji property.</summary>
    public bool EmojiOnly { get; init; }

    /// <summary>
    ///     Also list the code points that are not assigned. It has an effect only when a block (or a plane) narrows the search and there are no words, so
    ///     a block can be browsed with its gaps.
    /// </summary>
    public bool IncludeUnassigned { get; init; }

    /// <summary>Whether the query asks for nothing in particular (it lists every assigned character).</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && Categories.Count == 0 && Block is null && Script is null && Age is null && !EmojiOnly;
}

/// <summary>A comparison of Unicode versions, <c>&gt;=6.0</c>.</summary>
internal readonly record struct AgeCondition(string Op, int Version)
{
    public static bool TryParse(string? text, out AgeCondition condition)
    {
        condition = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string t  = text.Trim();
        string op = "=";
        foreach (string candidate in new[] { ">=", "<=", ">", "<", "=" })
            if (t.StartsWith(candidate, StringComparison.Ordinal))
            {
                op = candidate;
                t  = t[candidate.Length..].Trim();
                break;
            }

        int key = UnicodeDatabase.AgeKey(t);
        if (key < 0) return false;

        condition = new AgeCondition(op, key);
        return true;
    }

    public bool Matches(int ageKey)
    {
        if (ageKey < 0) return false;

        return Op switch
               {
                   ">=" => ageKey >= Version,
                   "<=" => ageKey <= Version,
                   ">"  => ageKey > Version,
                   "<"  => ageKey < Version,
                   // "6" means the whole of 6.x, "6.0" exactly that version
                   _ => Version % 100 == 0 ? ageKey / 100 == Version / 100 : ageKey == Version
               };
    }
}

public sealed partial class UnicodeDatabase
{
    private static readonly Regex Tokens = new("([A-Za-z]+:)?\"([^\"]*)\"|(\\S+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The characters that match the query, as code points in tiers: names that equal the text, then whole-word matches, then the rest, each in code point order.</summary>
    /// <param name="query">What to look for.</param>
    /// <param name="cancel">Stops a search that is no longer wanted (it throws <see cref="OperationCanceledException" />).</param>
    public int[] Search(UnicodeQuery query, CancellationToken cancel = default)
    {
        var plan = new SearchPlan(this, query);
        if (plan.Impossible) return [];

        var seen   = new BitArray(CodePoint.MaxValue + 1);
        var tiers  = new List<int>[] { new(), new(), new() };
        int budget = 0;

        void Add(int tier, int cp)
        {
            if (seen[cp]) return;
            seen[cp] = true;
            tiers[tier].Add(cp);
        }

        // explicit code points first
        foreach (var (lo, hi) in plan.CodeRanges)
            AddRange(lo, hi, 0, null);

        if (plan.Words.Count > 0 || plan.CodeRanges.Count == 0)
        {
            foreach (var r in _records)
            {
                if ((++budget & 0x3FF) == 0) cancel.ThrowIfCancellationRequested();
                if (!plan.PassesRecord(r)) continue;

                int tier = plan.Words.Count == 0 ? 1 : plan.WordTier(this, r);
                if (tier < 0) continue;

                AddRange(r.Start, r.End, tier, r);
            }
        }

        if (plan.Words.Count == 0 && query.IncludeUnassigned && plan.HasRange)
            foreach (var (lo, hi) in plan.ClipRanges)
                for (int cp = lo; cp <= hi; cp++)
                {
                    if ((++budget & 0x3FFF) == 0) cancel.ThrowIfCancellationRequested();
                    if (FindRecord(cp) is null && plan.PassesGap()) Add(2, cp);
                }

        // a block browsed with its gaps reads best in code point order
        if (plan.Words.Count == 0 && query.IncludeUnassigned && plan.HasRange)
        {
            var all = tiers.SelectMany(t => t).ToList();
            all.Sort();
            return all.ToArray();
        }

        var result = new List<int>(tiers.Sum(t => t.Count));
        foreach (var t in tiers)
        {
            t.Sort();
            result.AddRange(t);
        }

        return result.ToArray();

        void AddRange(int lo, int hi, int tier, Record? known)
        {
            foreach (var (clipLo, clipHi) in plan.ClipTo(lo, hi))
                for (int cp = clipLo; cp <= clipHi; cp++)
                {
                    if ((++budget & 0x3FFF) == 0) cancel.ThrowIfCancellationRequested();

                    var r = known ?? FindRecord(cp);
                    if (r is null)
                    {
                        if (query.IncludeUnassigned && plan.PassesGap()) Add(tier, cp);
                        continue;
                    }

                    if (known is null && !plan.PassesRecord(r)) continue;
                    Add(tier, cp);
                }
        }
    }

    /// <summary>What a query means once it is read: the words, code point ranges and filters.</summary>
    private sealed class SearchPlan
    {
        private readonly HashSet<string>[]        _categoryGroups;
        private readonly HashSet<string>[]        _scriptGroups;
        private readonly AgeCondition[]           _ages;
        private readonly List<(int Lo, int Hi)>?  _blockRanges;
        private readonly string[]                 _bidi;
        private readonly bool                     _emoji;
        private readonly bool                     _assigned;
        private readonly bool                     _mirrored;
        private readonly bool                     _combining;
        private readonly bool                     _ascii;
        private readonly int?                     _plane;
        private readonly bool                     _allowNonCharacters;

        public SearchPlan(UnicodeDatabase db, UnicodeQuery q)
        {
            var categories = new List<HashSet<string>>();
            var scripts    = new List<HashSet<string>>();
            var ages       = new List<AgeCondition>();
            var bidi       = new List<string>();
            var blockSets  = new List<List<(int, int)>>();
            bool emoji = q.EmojiOnly, assigned = false, mirrored = false, combining = false, ascii = false;
            int? plane = null;

            if (q.Categories.Count > 0) categories.Add(q.Categories.ToHashSet(StringComparer.Ordinal));
            if (!string.IsNullOrEmpty(q.Script)) scripts.Add([q.Script]);
            if (!string.IsNullOrEmpty(q.Age))
            {
                if (AgeCondition.TryParse(q.Age, out var a)) ages.Add(a);
                else Impossible = true;
            }

            if (!string.IsNullOrEmpty(q.Block))
            {
                var found = db._blocks.Where(b => b.Name.Equals(q.Block, StringComparison.OrdinalIgnoreCase)).ToList();
                if (found.Count == 0) Impossible = true;
                else blockSets.Add(found.Select(b => (b.Start, b.End)).ToList());
            }

            foreach (Match m in Tokens.Matches(q.Text ?? string.Empty))
            {
                if (m.Groups[2].Success)
                {
                    // a quoted phrase, or a qualifier with a quoted value: block:"Basic Latin"
                    if (m.Groups[1].Success && TryQualifier(db, m.Groups[1].Value.TrimEnd(':').ToLowerInvariant(), m.Groups[2].Value)) continue;
                    AddWord(m.Groups[1].Value + m.Groups[2].Value);
                    continue;
                }

                string token = m.Groups[3].Value;
                int    colon = token.IndexOf(':');
                if (colon > 0 && colon < token.Length - 1 && TryQualifier(db, token[..colon].ToLowerInvariant(), token[(colon + 1)..]))
                    continue;

                if (TryCodePoints(token)) continue;
                AddWord(token);
            }

            _categoryGroups = categories.ToArray();
            _scriptGroups   = scripts.ToArray();
            _ages           = ages.ToArray();
            _bidi           = bidi.ToArray();
            _emoji          = emoji;
            _assigned       = assigned;
            _mirrored       = mirrored;
            _combining      = combining;
            _ascii          = ascii;
            _plane          = plane;

            if (plane is { } p) blockSets.Add([(p << 16, (p << 16) | 0xFFFF)]);

            // private-use code points, surrogates and noncharacters are listed only when asked for: by a category, a block, a plane, or by their number
            _allowNonCharacters = q.IncludeUnassigned || blockSets.Count > 0 || CodeRanges.Count > 0
                                  || categories.Any(g => g.Overlaps(new[] { "C", "Co", "Cs", "Cn" }));
            if (blockSets.Count > 0)
            {
                var clip = blockSets[0];
                for (int i = 1; i < blockSets.Count; i++) clip = Intersect(clip, blockSets[i]);
                _blockRanges = clip;
                if (clip.Count == 0) Impossible = true;
            }

            bool TryQualifier(UnicodeDatabase db2, string key, string value)
            {
                switch (key)
                {
                    case "cat" or "gc" or "category":
                    {
                        var set = new HashSet<string>(StringComparer.Ordinal);
                        foreach (string v in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            string code = v.Length == 1 ? v.ToUpperInvariant() : char.ToUpperInvariant(v[0]) + v[1..].ToLowerInvariant();
                            set.Add(code);
                        }

                        categories.Add(set);
                        return true;
                    }
                    case "block" or "blk":
                    {
                        string norm = UnicodeNames.Normalize(value);
                        var    exact = db2._blocks.Where(b => UnicodeNames.Normalize(b.Name) == norm).ToList();
                        var    found = exact.Count > 0 ? exact : db2._blocks.Where(b => UnicodeNames.Normalize(b.Name).Contains(norm.Trim(), StringComparison.Ordinal)).ToList();
                        if (found.Count == 0) Impossible = true;
                        else blockSets.Add(found.Select(b => (b.Start, b.End)).ToList());
                        return true;
                    }
                    case "script" or "sc":
                    {
                        string norm  = UnicodeNames.Normalize(value).Trim();
                        var    codes = db2._scripts.Values
                                          .Where(s => s.Code.Equals(value, StringComparison.OrdinalIgnoreCase) || UnicodeNames.Normalize(s.Name).Trim() == norm)
                                          .Select(s => s.Code)
                                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        if (codes.Count == 0) Impossible = true;
                        scripts.Add(codes);
                        return true;
                    }
                    case "age":
                        if (AgeCondition.TryParse(value, out var a)) ages.Add(a);
                        else Impossible = true;
                        return true;
                    case "plane":
                        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int p) && p is >= 0 and <= 16) plane = p;
                        else Impossible = true;
                        return true;
                    case "bidi":
                        bidi.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.ToUpperInvariant()));
                        return true;
                    case "is":
                        switch (value.ToLowerInvariant())
                        {
                            case "emoji":     emoji     = true; return true;
                            case "assigned":  assigned  = true; return true;
                            case "mirrored":  mirrored  = true; return true;
                            case "combining": combining = true; return true;
                            case "ascii":     ascii     = true; return true;
                            default:          return false;
                        }
                    default:
                        return false;
                }
            }

            bool TryCodePoints(string token)
            {
                // a range: U+1F600-1F64F, U+1F600..1F64F, 0x41-0x5A
                int sep = token.IndexOf("..", StringComparison.Ordinal);
                int sepLen = 2;
                if (sep < 0)
                {
                    sep    = token.IndexOf('-', 1);
                    sepLen = 1;
                }

                if (sep > 0 && CodePoint.TryParseExplicit(token[..sep], out var from))
                {
                    string rest = token[(sep + sepLen)..];
                    if (CodePoint.TryParseExplicit(rest, out var to) || CodePoint.TryParse("U+" + rest, out to))
                    {
                        CodeRanges.Add((Math.Min(from.Value, to.Value), Math.Max(from.Value, to.Value)));
                        return true;
                    }
                }

                if (CodePoint.TryParseExplicit(token, out var cp))
                {
                    CodeRanges.Add((cp.Value, cp.Value));
                    return true;
                }

                // a word with something outside ASCII is a character lookup
                if (token.Any(c => c > 127))
                {
                    foreach (var one in CodePoint.Enumerate(token)) CodeRanges.Add((one.Value, one.Value));
                    return true;
                }

                return false;
            }

            void AddWord(string text)
            {
                string norm = UnicodeNames.Normalize(text);
                if (norm.Trim().Length == 0) return;

                int? hex = null;
                string t = text.Trim();
                if (t.Length is >= 2 and <= 6 && t.All(Uri.IsHexDigit) && int.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v) && v <= CodePoint.MaxValue)
                    hex = v;
                Words.Add(new Word(norm, norm.Trim(), hex));
            }
        }

        public bool Impossible { get; private set; }

        public List<Word> Words { get; } = [];

        public List<(int Lo, int Hi)> CodeRanges { get; } = [];

        /// <summary>Whether a block or plane narrows the search.</summary>
        public bool HasRange => _blockRanges is not null;

        /// <summary>The ranges the search is narrowed to (the whole code space when nothing narrows it).</summary>
        public IReadOnlyList<(int Lo, int Hi)> ClipRanges => _blockRanges ?? [(0, CodePoint.MaxValue)];

        /// <summary>The parts of lo..hi inside the block / plane limits.</summary>
        public IEnumerable<(int Lo, int Hi)> ClipTo(int lo, int hi)
        {
            if (_blockRanges is null)
            {
                yield return (lo, hi);
                yield break;
            }

            foreach (var (bl, bh) in _blockRanges)
            {
                int l = Math.Max(lo, bl), h = Math.Min(hi, bh);
                if (l <= h) yield return (l, h);
            }
        }

        /// <summary>Whether a code point with no record (unassigned) passes the filters.</summary>
        public bool PassesGap()
        {
            return _categoryGroups.All(g => g.Contains("Cn") || g.Contains("C"))
                   && _scriptGroups.Length == 0 && _ages.Length == 0 && _bidi.Length == 0
                   && !_emoji && !_assigned && !_mirrored && !_combining;
        }

        public bool PassesRecord(Record r)
        {
            foreach (var g in _categoryGroups)
                if (!g.Contains(r.Category) && !g.Contains(r.Category[..1]))
                    return false;

            foreach (var g in _scriptGroups)
                if (!g.Contains(r.ScriptCode))
                    return false;

            foreach (var a in _ages)
                if (!a.Matches(r.AgeValue))
                    return false;

            if (_bidi.Length > 0 && !_bidi.Contains(r.Bidi)) return false;
            if (_emoji && !r.Flags.HasFlag(UnicodeFlags.Emoji)) return false;
            if ((_assigned || !_allowNonCharacters) && r.Category is "Cn" or "Co" or "Cs") return false;
            if (_mirrored && !r.Flags.HasFlag(UnicodeFlags.Mirrored)) return false;
            if (_combining && r.Ccc == 0 && !(r.Category is "Mn" or "Me")) return false;
            if (_ascii && r.Start > 0x7F) return false;
            return true;
        }

        /// <summary>0 for a name that equals the words, 1 when every word is a whole word of the record's text, 2 when every word is somewhere in it, -1 for no match.</summary>
        public int WordTier(UnicodeDatabase db, Record r)
        {
            string text = r.SearchText ??= BuildSearchText(db, r);

            bool whole = true;
            foreach (var w in Words)
            {
                if (text.Contains(w.Padded, StringComparison.Ordinal)) continue;
                if (w.Hex is { } h && h >= r.Start && h <= r.End) continue;
                whole = false;
                if (!text.Contains(w.Bare, StringComparison.Ordinal))
                    return -1;
            }

            if (!whole) return 2;

            // the words are the whole name: the best match
            string joined = " " + string.Join(' ', Words.Select(w => w.Bare)) + " ";
            string name   = r.NormName ??= UnicodeNames.Normalize(r.Rule == NameRule.Literal ? r.Name : r.Name + r.Start.ToString("X4", CultureInfo.InvariantCulture));
            if (name == joined || r.Aliases.Any(a => UnicodeNames.Normalize(a) == joined)) return 0;
            return 1;
        }

        private static string BuildSearchText(UnicodeDatabase db, Record r)
        {
            var parts = new List<string>
                        {
                            r.Rule switch
                            {
                                NameRule.Hangul       => "hangul syllable",
                                NameRule.PrivateUse   => "private use",
                                NameRule.Surrogate    => "surrogate",
                                NameRule.Noncharacter => "noncharacter",
                                _                     => r.Name
                            }
                        };
            parts.AddRange(r.Aliases);
            parts.AddRange(r.Keywords);
            parts.AddRange(r.Entities);
            parts.Add(r.EmojiGroup);
            parts.Add(UnicodeNames.Category(r.Category)?.Name ?? string.Empty);
            parts.Add(db.ScriptName(r.ScriptCode));
            if (db.BlockOf(r.Start) is { } b) parts.Add(b.Name);
            return UnicodeNames.Normalize(string.Join(' ', parts));
        }

        private static List<(int, int)> Intersect(List<(int, int)> a, List<(int, int)> b)
        {
            var result = new List<(int, int)>();
            foreach (var (al, ah) in a)
            foreach (var (bl, bh) in b)
            {
                int l = Math.Max(al, bl), h = Math.Min(ah, bh);
                if (l <= h) result.Add((l, h));
            }

            return result;
        }
    }

    private readonly record struct Word(string Padded, string Bare, int? Hex);
}
