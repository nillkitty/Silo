namespace Silo.Unicode;

/// <summary>A run of code points, both ends included.</summary>
public readonly record struct CodeRange(int First, int Last)
{
    /// <summary>How many code points are in it.</summary>
    public int Count => Last - First + 1;
}

/// <summary>A list of code points written the way people write them: <c>U+0041-U+005A, 1F600..1F64F, 0x2600, a-z</c>.</summary>
public sealed class CodePointRanges
{
    private CodePointRanges(IReadOnlyList<CodeRange> ranges, IReadOnlyList<string> problems)
    {
        Ranges   = ranges;
        Problems = problems;
    }

    /// <summary>The runs, in order, without overlaps (neighbouring runs are joined).</summary>
    public IReadOnlyList<CodeRange> Ranges { get; }

    /// <summary>The parts of the text that could not be read.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>How many code points there are in all.</summary>
    public long Count => Ranges.Sum(r => (long)r.Count);

    /// <summary>The code points, in order.</summary>
    public IEnumerable<int> Enumerate()
    {
        foreach (var r in Ranges)
            for (int cp = r.First; cp <= r.Last; cp++)
                yield return cp;
    }

    /// <summary>
    ///     Reads the text: parts are separated by commas, semicolons, spaces or lines; a part is a code point in any of the forms
    ///     <see cref="CodePoint.TryParse" /> reads, or two of them with <c>-</c> or <c>..</c> between (either end first).
    /// </summary>
    public static CodePointRanges Parse(string? text)
    {
        var found    = new List<CodeRange>();
        var problems = new List<string>();

        foreach (string part in (text ?? string.Empty).Split(new[] { ' ', '\t', '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryPart(part, out var range)) found.Add(range);
            else problems.Add(part);
        }

        return new CodePointRanges(Merge(found), problems);
    }

    private static bool TryPart(string part, out CodeRange range)
    {
        range = default;

        int dots = part.IndexOf("..", StringComparison.Ordinal);
        int cut  = dots >= 0 ? dots : part.IndexOf('-', 1);
        int len  = dots >= 0 ? 2 : 1;
        if (cut > 0 && cut + len < part.Length)
        {
            if (!CodePoint.TryParse(part[..cut], out var from) || !CodePoint.TryParse(part[(cut + len)..], out var to)) return false;
            range = new CodeRange(Math.Min(from.Value, to.Value), Math.Max(from.Value, to.Value));
            return true;
        }

        if (!CodePoint.TryParse(part, out var one)) return false;
        range = new CodeRange(one.Value, one.Value);
        return true;
    }

    private static List<CodeRange> Merge(List<CodeRange> ranges)
    {
        var merged = new List<CodeRange>();
        foreach (var r in ranges.OrderBy(r => r.First))
        {
            if (merged.Count > 0 && r.First <= merged[^1].Last + 1)
                merged[^1] = merged[^1] with { Last = Math.Max(merged[^1].Last, r.Last) };
            else merged.Add(r);
        }

        return merged;
    }
}
