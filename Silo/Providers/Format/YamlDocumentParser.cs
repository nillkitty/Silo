using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Silo.Providers.Format;

/// <summary>
///     Drives a <see cref="YamlDotNet.Core.IParser" /> event stream directly to build a
///     <see cref="YamlDocNode" /> tree per <c>---</c>-separated document in the stream, retaining
///     comments, the explicit/implicit tag distinction, and source position -- none of which
///     <c>YamlDotNet.RepresentationModel</c>'s own DOM preserves. See <see cref="YamlFormat" />,
///     which owns constructing the <see cref="Scanner" />/<see cref="Parser" /> this is driven
///     from (with <c>skipComments: false</c>, which is what makes <see cref="Comment" /> events
///     show up in the stream at all).
/// </summary>
internal sealed class YamlDocumentParser
{
    private readonly IParser _parser;

    /// <summary>
    ///     Anchors seen so far in the *current* document, so later aliases can resolve back to
    ///     them. Cleared between documents -- per the YAML spec, an anchor is only valid within
    ///     the document that defines it.
    /// </summary>
    private readonly Dictionary<string, YamlDocNode> _anchors = new(StringComparer.Ordinal);

    /// <summary>
    ///     Alias nodes whose target anchor hadn't been seen yet at the point the alias itself was
    ///     parsed. A single forward pass over a well-formed document should never actually hit
    ///     this (YAML requires an anchor to precede any alias referring to it), but it's tracked
    ///     defensively rather than assumed impossible -- see <see cref="YamlAliasNode.Target" />.
    /// </summary>
    private readonly List<YamlAliasNode> _unresolvedAliases = [];

    public YamlDocumentParser(IParser parser)
    {
        _parser = parser.Required();
    }

    /// <summary>
    ///     Reads every <c>---</c>-separated document out of the stream, in order. Lazily
    ///     evaluated: nothing is parsed until (and only as far as) the result is enumerated.
    /// </summary>
    public IEnumerable<YamlDocNode> ReadDocuments()
    {
        Advance(); // Current is null until the first MoveNext(); prime it onto StreamStart.
        Expect<StreamStart>();
        Advance();

        while (true)
        {
            // Comments before the first document, or between two documents, have no sibling
            // node in the tree to attach to -- they're collected (so they don't trip up the
            // event-type checks below) and then simply dropped.
            CollectComments();

            if (Current is StreamEnd) yield break;

            Expect<DocumentStart>();
            Advance();

            _anchors.Clear();
            _unresolvedAliases.Clear();

            var root = ReadNode();

            CollectComments(); // trailing comments before '...'/the next '---' -- dropped, same as above.
            Expect<DocumentEnd>();
            Advance();

            yield return root;
        }
    }

    /// <summary>
    ///     Reads one full node (and everything nested under it) starting at <see cref="Current" />.
    /// </summary>
    private YamlDocNode ReadNode()
    {
        var leading = CollectComments();
        return ReadNodeCore(leading);
    }

    /// <summary>
    ///     Reads one full node starting at <see cref="Current" />, using <paramref name="leading" />
    ///     as its <see cref="YamlDocNode.LeadingComments" /> instead of collecting them itself --
    ///     for callers (<see cref="ReadMapping" />/<see cref="ReadSequence" />) that already had
    ///     to peek past comments to tell whether a collection had ended.
    /// </summary>
    private YamlDocNode ReadNodeCore(List<string> leading)
    {
        // Explicitly typed (not 'var'): the four arms produce sibling types (YamlScalarNode,
        // YamlMappingNode, YamlSequenceNode, YamlAliasNode) whose only common ground is the
        // YamlDocNode base, and switch-expression natural-type inference doesn't walk up to a
        // common ancestor on its own -- it needs a target type to drive the conversion, or the
        // compiler rejects it with CS8506 ("no best type was found for the switch expression").
        YamlDocNode node = Current switch
        {
            Scalar scalar      => ReadScalar(scalar),
            MappingStart ms    => ReadMapping(ms),
            SequenceStart ss   => ReadSequence(ss),
            AnchorAlias alias  => ReadAlias(alias),
            var other          => throw new YamlException(
                $"Unexpected YAML event '{other?.GetType().Name ?? "<end of stream>"}' where a node was expected.")
        };

        node.LeadingComments = leading;
        AttachTrailingInlineComment(node);
        return node;
    }

    private YamlScalarNode ReadScalar(Scalar scalar)
    {
        Advance(); // past the Scalar event itself -- it carries its own content, nothing nested under it.

        var node = new YamlScalarNode
        {
            Value         = scalar.Value,
            Style         = scalar.Style,
            Anchor        = scalar.Anchor.IsEmpty ? null : scalar.Anchor.Value,
            Tag           = scalar.Tag.IsEmpty ? null : scalar.Tag.Value,
            TagIsExplicit = !scalar.IsPlainImplicit && !scalar.IsQuotedImplicit,
            Start         = ToOrigin(scalar.Start),
            End           = ToOrigin(scalar.End)
        };
        RegisterAnchor(node);
        return node;
    }

    private YamlMappingNode ReadMapping(MappingStart start)
    {
        Advance(); // past MappingStart

        var entries = new List<YamlMappingEntry>();
        while (true)
        {
            var leading = CollectComments();
            if (Current is MappingEnd) break; // any comments just collected are trailing/orphaned inside the mapping -- dropped, see ReadDocuments.

            var key   = ReadNodeCore(leading);
            var value = ReadNode();
            entries.Add(new YamlMappingEntry(key, value));
        }

        var end = ((MappingEnd)Current!).End;
        Advance(); // past MappingEnd

        var node = new YamlMappingNode
        {
            Entries       = entries,
            Style         = start.Style,
            Anchor        = start.Anchor.IsEmpty ? null : start.Anchor.Value,
            Tag           = start.Tag.IsEmpty ? null : start.Tag.Value,
            TagIsExplicit = !start.IsImplicit,
            Start         = ToOrigin(start.Start),
            End           = ToOrigin(end)
        };
        RegisterAnchor(node);
        return node;
    }

    private YamlSequenceNode ReadSequence(SequenceStart start)
    {
        Advance(); // past SequenceStart

        var items = new List<YamlDocNode>();
        while (true)
        {
            var leading = CollectComments();
            if (Current is SequenceEnd) break; // trailing/orphaned comments inside the sequence -- dropped.

            items.Add(ReadNodeCore(leading));
        }

        var end = ((SequenceEnd)Current!).End;
        Advance(); // past SequenceEnd

        var node = new YamlSequenceNode
        {
            Items         = items,
            Style         = start.Style,
            Anchor        = start.Anchor.IsEmpty ? null : start.Anchor.Value,
            Tag           = start.Tag.IsEmpty ? null : start.Tag.Value,
            TagIsExplicit = !start.IsImplicit,
            Start         = ToOrigin(start.Start),
            End           = ToOrigin(end)
        };
        RegisterAnchor(node);
        return node;
    }

    private YamlAliasNode ReadAlias(AnchorAlias alias)
    {
        Advance(); // past the AnchorAlias event itself

        // AnchorAlias's own constructor guarantees Value is never empty, so .Value is always safe here.
        var targetName = alias.Value.Value;

        var node = new YamlAliasNode
        {
            TargetAnchor = targetName,
            Start        = ToOrigin(alias.Start),
            End          = ToOrigin(alias.End)
        };

        if (_anchors.TryGetValue(targetName, out var target))
            node.Target = target;
        else
            _unresolvedAliases.Add(node);

        return node;
    }

    /// <summary>
    ///     Records <paramref name="node" /> under its own anchor (if it has one) so later aliases
    ///     can resolve to it, and retroactively resolves any earlier alias that was waiting on
    ///     this anchor name (see <see cref="_unresolvedAliases" />).
    /// </summary>
    private void RegisterAnchor(YamlDocNode node)
    {
        if (node.Anchor is not { } name) return;

        _anchors[name] = node;

        foreach (var pending in _unresolvedAliases)
            if (pending.Target is null && pending.TargetAnchor == name)
                pending.Target = node;
    }

    /// <summary>
    ///     If the node that was just built is immediately followed by an inline comment (one that
    ///     trailed it on the same source line), attaches it as <see cref="YamlDocNode.InlineComment" />
    ///     and consumes it. A no-op otherwise.
    /// </summary>
    private void AttachTrailingInlineComment(YamlDocNode node)
    {
        if (Current is Comment { IsInline: true } comment)
        {
            node.InlineComment = comment.Value;
            Advance();
        }
    }

    /// <summary>
    ///     Consumes every (non-inline, i.e. own-line/"leading") <see cref="Comment" /> event
    ///     currently at <see cref="Current" />, returning their text in source order. An inline
    ///     comment is never collected here -- it belongs to whatever node precedes it (see
    ///     <see cref="AttachTrailingInlineComment" />), not whatever node follows it.
    /// </summary>
    private List<string> CollectComments()
    {
        var comments = new List<string>();
        while (Current is Comment { IsInline: false } comment)
        {
            comments.Add(comment.Value);
            Advance();
        }

        return comments;
    }

    private void Expect<TEvent>() where TEvent : ParsingEvent
    {
        if (Current is not TEvent)
            throw new YamlException(
                $"Expected a {typeof(TEvent).Name} event but found '{Current?.GetType().Name ?? "<end of stream>"}'.");
    }

    private ParsingEvent? Current => _parser.Current;

    private void Advance()
    {
        if (!_parser.MoveNext())
            throw new YamlException("Unexpected end of the YAML event stream.");
    }

    private static Origin ToOrigin(Mark mark) => new((int)mark.Line, (int)mark.Column, mark.Index);
}
