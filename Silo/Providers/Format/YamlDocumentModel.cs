using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Silo.Providers.Format;

/// <summary>
///     Which kind of YAML node a <see cref="YamlDocNode" /> is, without needing a type check/cast.
/// </summary>
public enum YamlNodeKind
{
    Scalar,
    Mapping,
    Sequence,
    Alias
}

/// <summary>
///     Base type for every node in a parsed YAML document tree (see <see cref="YamlFormat" />).
/// </summary>
/// <remarks>
///     Unlike YamlDotNet's own <c>YamlDotNet.RepresentationModel</c> DOM (<c>YamlNode</c> et al.
///     -- deliberately not used here, see <see cref="YamlFormat" />), this tree is built directly
///     off the low-level <see cref="YamlDotNet.Core.Parser" />/<see cref="YamlDotNet.Core.Scanner" />
///     event stream (by <see cref="YamlDocumentParser" />) specifically so it can retain
///     information that representation discards: comments (<see cref="LeadingComments" />/
///     <see cref="InlineComment" />), the explicit-vs-implicit distinction for tags -- custom
///     types like <c>!blah</c>, explicit overrides like <c>!!int</c> -- (<see cref="Tag" />/
///     <see cref="TagIsExplicit" />), and source position (<see cref="Start" />/<see cref="End" />).
/// </remarks>
public abstract class YamlDocNode
{
    /// <summary>Which concrete node type this is.</summary>
    public abstract YamlNodeKind Kind { get; }

    /// <summary>
    ///     The node's anchor name -- the <c>&amp;name</c> an <see cref="YamlAliasNode" />
    ///     elsewhere in the same document can refer back to -- or <c>null</c> if the node has none.
    /// </summary>
    public string? Anchor { get; internal set; }

    /// <summary>Where this node ends in the source file.</summary>
    public Origin End { get; internal set; }

    /// <summary>
    ///     A comment that appeared trailing this node on the same source line, or <c>null</c> if
    ///     there wasn't one. Unlike <see cref="LeadingComments" />, this is only known once
    ///     whatever follows the node has been looked at, so (see <see cref="YamlDocumentParser" />)
    ///     it's attached after the node is otherwise fully built -- hence a settable property
    ///     rather than one fixed at construction.
    /// </summary>
    public string? InlineComment { get; set; }

    /// <summary>
    ///     True if <see cref="Tag" /> is set and is not one of YAML's core-schema tags -- i.e. a
    ///     custom type such as <c>!blah</c> or <c>!my.custom/tag</c>. See <see cref="LocalTagName" />.
    /// </summary>
    public bool IsCustomTag => Tag is not null && !IsStandardTag;

    /// <summary>
    ///     True if <see cref="Tag" /> is one of YAML's own <c>tag:yaml.org,2002:...</c>
    ///     core-schema tags (str/int/float/bool/null/map/seq/etc.) -- i.e. NOT a custom type like
    ///     <c>!blah</c>.
    /// </summary>
    public bool IsStandardTag => Tag is { } t && t.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal);

    /// <summary>
    ///     Comment lines that appeared on their own, immediately before this node, in the source.
    /// </summary>
    public IReadOnlyList<string> LeadingComments { get; internal set; } = [];

    /// <summary>
    ///     The short, local form of a custom tag -- <c>"blah"</c> for <c>!blah</c> -- or
    ///     <c>null</c> if there's no tag, or it's one of YAML's own standard tags (see
    ///     <see cref="IsStandardTag" />).
    /// </summary>
    public string? LocalTagName => IsCustomTag ? Tag!.TrimStart('!') : null;

    /// <summary>Where this node starts in the source file.</summary>
    public Origin Start { get; internal set; }

    /// <summary>
    ///     The node's resolved tag, e.g. <c>tag:yaml.org,2002:str</c> for a plain string,
    ///     <c>!blah</c> for a custom type, or <c>tag:yaml.org,2002:int</c> when explicitly
    ///     overridden with <c>!!int</c>. <c>null</c> only if the underlying event carried no tag
    ///     at all.
    /// </summary>
    public string? Tag { get; internal set; }

    /// <summary>
    ///     True if <see cref="Tag" /> was written explicitly in the source (e.g. <c>!blah</c>,
    ///     <c>!!int</c>), as opposed to being implicitly resolved by the YAML parser from the
    ///     scalar's content and style.
    /// </summary>
    public bool TagIsExplicit { get; internal set; }
}

/// <summary>A YAML scalar (string/number/bool/null/etc.) node.</summary>
public sealed class YamlScalarNode : YamlDocNode
{
    public override YamlNodeKind Kind => YamlNodeKind.Scalar;

    /// <summary>How the scalar was written -- plain, single/double-quoted, literal (<c>|</c>), or folded (<c>&gt;</c>).</summary>
    public ScalarStyle Style { get; internal set; }

    /// <summary>The scalar's raw text content, exactly as YamlDotNet resolved it.</summary>
    public required string Value { get; init; }

    public override string ToString()
    {
        return Value;
    }
}

/// <summary>A single key/value pair inside a <see cref="YamlMappingNode" />.</summary>
public sealed class YamlMappingEntry(YamlDocNode key, YamlDocNode value)
{
    public YamlDocNode Key   { get; } = key;
    public YamlDocNode Value { get; } = value;
}

/// <summary>A YAML mapping (<c>key: value</c>) node.</summary>
public sealed class YamlMappingNode : YamlDocNode
{
    public required IReadOnlyList<YamlMappingEntry> Entries { get; init; }

    /// <summary>
    ///     Looks up an entry by a plain scalar key (the common case -- <c>key: value</c>). Returns
    ///     <c>null</c> both when there's no such key and when the key exists but isn't a plain
    ///     scalar (a mapping/sequence key has no single string to match against); use
    ///     <see cref="Entries" /> directly for anything more involved.
    /// </summary>
    public YamlDocNode? this[string key] =>
        Entries.FirstOrDefault(e => e.Key is YamlScalarNode scalar && scalar.Value == key)?.Value;

    public override YamlNodeKind Kind => YamlNodeKind.Mapping;

    /// <summary>Whether the mapping was written in block or flow (<c>{ }</c>) style.</summary>
    public MappingStyle Style { get; internal set; }
}

/// <summary>A YAML sequence (<c>- item</c>) node.</summary>
public sealed class YamlSequenceNode : YamlDocNode
{
    public required IReadOnlyList<YamlDocNode> Items { get; init; }
    public override YamlNodeKind               Kind  => YamlNodeKind.Sequence;

    /// <summary>Whether the sequence was written in block or flow (<c>[ ]</c>) style.</summary>
    public SequenceStyle Style { get; internal set; }
}

/// <summary>
///     A YAML alias (<c>*name</c>) node -- a reference back to an earlier node in the same
///     document that was given an anchor (<c>&amp;name</c>).
/// </summary>
public sealed class YamlAliasNode : YamlDocNode
{
    public override YamlNodeKind Kind => YamlNodeKind.Alias;

    /// <summary>
    ///     The node <see cref="TargetAnchor" /> resolved to, once resolution was possible (see
    ///     <see cref="YamlDocumentParser" />). YAML requires an anchor to be defined before any
    ///     alias referring to it, so in practice this is always set by the time a full document
    ///     has been read; it's nullable defensively, for a malformed/truncated document where the
    ///     anchor was never seen.
    /// </summary>
    public YamlDocNode? Target { get; internal set; }

    /// <summary>The anchor name this alias refers to, e.g. <c>"name"</c> for <c>*name</c>.</summary>
    public required string TargetAnchor { get; init; }
}