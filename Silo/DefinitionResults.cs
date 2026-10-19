using System.Globalization;

namespace Silo;

/// <summary>
///     A value captured by a <see cref="Capture" /> (or a group made by a named nested <see cref="DataDefinition" />).
/// </summary>
public sealed class CaptureNode
{
    internal CaptureNode(string name, object? value, int offset, int length, bool isText, IReadOnlyList<CaptureNode> children)
    {
        Name     = name;
        Value    = value;
        Offset   = offset;
        Length   = length;
        IsText   = isText;
        Children = children;
    }

    /// <summary>
    ///     The captures made inside this one, in order. Names may repeat.
    /// </summary>
    public IReadOnlyList<CaptureNode> Children { get; }

    /// <summary>
    ///     Whether <see cref="Offset" /> and <see cref="Length" /> count characters of decoded text (else bytes).
    /// </summary>
    public bool IsText { get; }

    /// <summary>
    ///     The length of the match, in the same units as <see cref="Offset" />.
    /// </summary>
    public int Length { get; }

    /// <summary>
    ///     The capture's name, after <see cref="InstanceNum" /> numbers are filled in.
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Where the match starts: a byte offset into the data, or (<see cref="IsText" />) a character offset into the text
    ///     decoded by the nearest enclosing <see cref="TextDefinition" />. Inside an unquoted/unescaped element the offset is
    ///     approximate.
    /// </summary>
    public int Offset { get; }

    /// <summary>
    ///     The text of a text capture (unquoted and unescaped when inside a qualified element), or null.
    /// </summary>
    public string? Text => Value as string;

    /// <summary>
    ///     The captured value: a <see cref="string" /> for text, a <see cref="long" /> for an <see cref="Int" />, a
    ///     <see cref="byte" /> array for other binary data, or null for a group.
    /// </summary>
    public object? Value { get; }

    /// <summary>
    ///     The first child with the given name, or null.
    /// </summary>
    public CaptureNode? this[string name] => Children.FirstOrDefault(c => c.Name == name);

    /// <summary>
    ///     Every child with the given name.
    /// </summary>
    public IEnumerable<CaptureNode> All(string name)
    {
        return Children.Where(c => c.Name == name);
    }

    /// <summary>
    ///     The children as a dictionary: a child with children of its own becomes a nested dictionary, others their
    ///     <see cref="Value" />; a name that repeats maps to a list of those.
    /// </summary>
    public Dictionary<string, object?> ToDictionary()
    {
        var counts = Children.GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var d      = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var c in Children)
        {
            object? v = c.Children.Count > 0 ? c.ToDictionary() : c.Value;
            if (counts[c.Name] == 1)
                d[c.Name] = v;
            else if (d.TryGetValue(c.Name, out var list))
                ((List<object?>)list!).Add(v);
            else
                d[c.Name] = new List<object?> { v };
        }

        return d;
    }

    public override string ToString()
    {
        var v = Value switch
                {
                    null        => Children.Count + " children",
                    byte[] b    => Convert.ToHexString(b),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _           => Value.ToString()
                };
        return $"{Name} = {v}";
    }
}

/// <summary>
///     Where a definition matched, and what it captured.
/// </summary>
public sealed class DefinitionMatch
{
    internal DefinitionMatch(int offset, int length, Encoding? encoding, CaptureNode root)
    {
        Offset   = offset;
        Length   = length;
        Encoding = encoding;
        Root     = root;
    }

    /// <summary>
    ///     The encoding of the first text decoded on the way, if any.
    /// </summary>
    public Encoding? Encoding { get; }

    /// <summary>
    ///     The length of the match in bytes (characters when matching a string).
    /// </summary>
    public int Length { get; }

    /// <summary>
    ///     Where the match starts, in bytes (characters when matching a string).
    /// </summary>
    public int Offset { get; }

    /// <summary>
    ///     The captures, under one node named after the definition.
    /// </summary>
    public CaptureNode Root { get; }
}
