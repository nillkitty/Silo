using System.Diagnostics;
using Superpower;
using Superpower.Model;
using Superpower.Parsers;

namespace Silo.Labs.Parser;

/// <summary>A problem found while a spec was compiled (a rule that refers to a rule that is not there, an empty rule...).</summary>
public sealed record SpecDiagnostic(string Rule, string Message);

/// <summary>How one parse went.</summary>
/// <param name="Success">Whether the text (up to where it may end) is a match.</param>
/// <param name="Value">What was extracted (see <see cref="SpecValues" />).</param>
/// <param name="Consumed">How many characters were used (all of them, or up to a token the spec may end on).</param>
/// <param name="ErrorPosition">Where it went wrong (the furthest place any attempt reached); -1 on success.</param>
/// <param name="Error">What was expected there.</param>
/// <param name="Elapsed">How long it took; zero for a compiled function.</param>
public sealed record SpecResult(bool Success, object? Value, int Consumed, int ErrorPosition, string? Error, TimeSpan Elapsed)
{
    public static SpecResult Failed(int position, string error, TimeSpan elapsed = default) => new(false, null, 0, position, error, elapsed);
}

/// <summary>One piece of a tokenized text.</summary>
public sealed record SpecToken(string Kind, string Text, int Position);

/// <summary>What the tokenizer made of a text.</summary>
public sealed record TokenizeResult(bool Success, IReadOnlyList<SpecToken> Tokens, int ErrorPosition, string? Error);

/// <summary>
///     A spec turned into parsers: one <c>TextParser</c> per rule, built from the spec's tree once and reused, so what runs in the Lab is the
///     same thing that runs at full speed (<see cref="Compile" />). Leaves are Superpower's character and string parsers; the combinators are
///     written here with Superpower's parser shape (a function from a <see cref="TextSpan" /> to a <see cref="Result{T}" />), because the Lab
///     needs what the stock ones do not give: skipping the ignored tokens between items, quantifiers with a least and a most, values that
///     leave out what is ignored, and the furthest position any attempt reached for the error message.
/// </summary>
/// <remarks>
///     Choice and repetition backtrack: an alternative that fails after using some text is simply not taken, and nothing needs
///     <c>Try</c> (which a <c>try</c> node still accepts and which then does nothing). A compiled spec can be used from many threads at once.
/// </remarks>
public sealed class CompiledSpec
{
    private readonly Dictionary<string, RuleHolder> _rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SpecDiagnostic>           _diagnostics = [];
    private readonly Func<char, bool>?              _ignore;
    private readonly Func<char, bool>?              _exit;
    private readonly List<(TextParser<object?> Parser, string? Kind)> _tokenRules = [];

    [ThreadStatic]
    private static Tracker? _tracker;

    private readonly Func<string, ParserSpecDoc?>?       _load;
    private readonly List<string>                       _chain;
    private readonly Dictionary<string, CompiledSpec?>  _imports = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>                    _tokenStack = new(StringComparer.OrdinalIgnoreCase);

    private CompiledSpec(ParserSpecDoc doc, Func<string, ParserSpecDoc?>? load, List<string> chain)
    {
        Doc     = doc;
        _load   = load;
        _chain  = chain;
        _ignore = TestOf(doc.Ignore);
        _exit   = TestOf(doc.ExitOn);

        foreach (var rule in doc.Rules.Where(r => !string.IsNullOrWhiteSpace(r.Name)))
            _rules[rule.Name] = new RuleHolder(rule.Name);

        foreach (var rule in doc.Rules.Where(r => !string.IsNullOrWhiteSpace(r.Name)))
            _rules[rule.Name].Parser = BuildRule(rule);

        foreach (var rule in doc.Tokenizer.Rules)
            if (rule.Matcher is { } matcher)
                _tokenRules.Add((Build(matcher, "(tokenizer)"), rule.Kind));
            else
                _diagnostics.Add(new SpecDiagnostic("(tokenizer)", "A tokenizer rule has no matcher."));
    }

    /// <summary>The spec this was made from (as it was: later changes to it are not seen).</summary>
    public ParserSpecDoc Doc { get; }

    /// <summary>What is wrong with the spec, if anything; the parsers still work for what is right.</summary>
    public IReadOnlyList<SpecDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    ///     Makes the parsers of <paramref name="doc" />. The document is copied, so it can go on being edited. <paramref name="load" /> finds the specs
    ///     it imports (null: it imports none that can be found).
    /// </summary>
    public static CompiledSpec Compile(ParserSpecDoc doc, Func<string, ParserSpecDoc?>? load = null)
    {
        return new CompiledSpec(doc.Clone(), load, [string.IsNullOrWhiteSpace(doc.Name) ? "(this spec)" : doc.Name]);
    }

    /// <summary>The parser of a rule, for another spec that imports it.</summary>
    private RuleHolder? RuleHolderOf(string name) => _rules.GetValueOrDefault(name);

    /// <summary>The names of the rules.</summary>
    public IEnumerable<string> RuleNames => Doc.Rules.Select(r => r.Name);

    /// <summary>The rule a parse uses when none is named: the root, else the first.</summary>
    public string? DefaultRule => Doc.Root is { Length: > 0 } root && _rules.ContainsKey(root) ? root : Doc.Rules.FirstOrDefault()?.Name;

    /// <summary>Whether there is a rule with this name.</summary>
    public bool HasRule(string name) => _rules.ContainsKey(name);

    // ---- running -------------------------------------------------------------------------------

    /// <summary>Parses all of <paramref name="text" /> (or what comes before a token the spec ends on) as <paramref name="rule" />.</summary>
    public SpecResult Parse(string text, string? rule = null)
    {
        var sw = Stopwatch.StartNew();
        var r  = Run(text, rule);
        return r with { Elapsed = sw.Elapsed };
    }

    /// <summary>The function for <paramref name="rule" />: the parse with nothing around it, to be kept and called as often as needed.</summary>
    public Func<string, SpecResult> Compile(string? rule = null)
    {
        string? name = rule ?? DefaultRule;
        return text => Run(text, name);
    }

    /// <summary>As <see cref="Compile(string?)" />, giving the value as a <typeparamref name="T" /> (and throwing a <see cref="FormatException" /> when the text is not a match).</summary>
    public Func<string, T> Compile<T>(string? rule = null)
    {
        var run = Compile(rule);
        return text =>
               {
                   var result = run(text);
                   if (!result.Success) throw new FormatException($"{result.Error} at {result.ErrorPosition}");
                   return (T)result.Value!;
               };
    }

    private SpecResult Run(string text, string? rule)
    {
        string? name = rule ?? DefaultRule;
        if (name is null || !_rules.TryGetValue(name, out var holder) || holder.Parser is null)
            return SpecResult.Failed(0, name is null ? "The spec has no rules." : $"There is no rule called '{name}'.");

        var previous = _tracker;
        var tracker  = new Tracker();
        _tracker = tracker;
        try
        {
            var input  = new TextSpan(text);
            var result = holder.Parser(input);
            if (!result.HasValue)
            {
                int at = tracker.Position >= 0 ? tracker.Position : Position(result.Remainder);
                return SpecResult.Failed(at, tracker.Expected is { } want ? "expected " + want : result.ErrorMessage ?? "no match");
            }

            var rest     = result.Remainder;
            int consumed = Position(rest);
            if (!rest.IsAtEnd && !AtExit(rest))
            {
                bool deeper = tracker.Position >= consumed && tracker.Expected is not null;
                return SpecResult.Failed(deeper ? tracker.Position : consumed, deeper ? "expected " + tracker.Expected : "expected end of input");
            }

            return new SpecResult(true, result.Value, consumed, -1, null, TimeSpan.Zero);
        }
        finally
        {
            _tracker = previous;
        }
    }

    /// <summary>Cuts <paramref name="text" /> into tokens with the tokenizer rules: the first rule that matches at a place takes the text there.</summary>
    public TokenizeResult Tokenize(string text)
    {
        var tokens = new List<SpecToken>();
        var rest   = new TextSpan(text);
        var previous = _tracker;
        _tracker = new Tracker();
        try
        {
            while (!rest.IsAtEnd)
            {
                bool matched = false;
                int  start   = Position(rest);
                foreach (var (parser, kind) in _tokenRules)
                {
                    var r = parser(rest);
                    if (!r.HasValue || Position(r.Remainder) == start) continue;

                    int end = Position(r.Remainder);
                    if (kind is not null) tokens.Add(new SpecToken(kind, text[start..end], start));
                    rest    = r.Remainder;
                    matched = true;
                    break;
                }

                if (!matched) return new TokenizeResult(false, tokens, start, $"no token starts with '{text[start]}'");
            }

            return new TokenizeResult(true, tokens, -1, null);
        }
        finally
        {
            _tracker = previous;
        }
    }

    // ---- building ------------------------------------------------------------------------------

    private TextParser<object?> BuildRule(RuleDef rule)
    {
        if (rule.Body is null)
        {
            _diagnostics.Add(new SpecDiagnostic(rule.Name, "The rule is empty."));
            return Fail($"a definition of '{rule.Name}'");
        }

        var parser = Build(rule.Body, rule.Name);

        if (string.Equals(rule.Shape, "Linear", StringComparison.OrdinalIgnoreCase))
            parser = Map(parser, v => SpecValues.Flatten(v).ToArray(), "a list");

        if (!string.IsNullOrWhiteSpace(rule.Returns))
        {
            string type = rule.Returns!;
            parser = Map(parser, v => SpecValues.Coerce(type, v), type);
        }

        return parser;
    }

    private TextParser<object?> Build(SpecNode node, string rule)
    {
        switch (node.Kind)
        {
            case "token":
                return BuildToken(node, rule);

            case "literal":
            {
                string text = node.Text ?? string.Empty;
                if (text.Length == 0) return Accept();
                return Literal(text, node.IgnoreCase);
            }

            case "chars":
            {
                string set    = node.Text ?? string.Empty;
                bool   negate = node.Negate;
                string name   = set.Length == 1 && !negate ? "'" + set + "'" : (negate ? "any but " : "one of ") + "'" + set + "'";
                return Leaf(Character.Matching(c => set.Contains(c) != negate, name), c => c.ToString(), name);
            }

            case "eof":
                return input => input.IsAtEnd ? Result.Value<object?>(null, input, input) : NoteFail(input, "end of input");

            case "ref":
            {
                string target = node.Text ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(node.Spec)) return BuildForeignRef(node, rule);

                if (!_rules.TryGetValue(target, out var holder))
                {
                    _diagnostics.Add(new SpecDiagnostic(rule, $"There is no rule called '{target}'."));
                    return Fail($"a rule called '{target}'");
                }

                // looked up when it runs: a rule may use one that is defined further on, or itself
                return input => holder.Parser is { } p ? p(input) : NoteFail(input, $"a definition of '{target}'");
            }

            case "unicode":
            {
                string classes = node.Text ?? string.Empty;
                bool   negate  = node.Negate;
                if (!UnicodeClasses.TryParse(classes, out var inClass, out string? problem))
                {
                    _diagnostics.Add(new SpecDiagnostic(rule, problem!));
                    return Fail("a Unicode class");
                }

                string name = (negate ? "any character but " : "a character of ") + "Unicode " + string.Join("/", UnicodeClasses.Names(classes));
                return Leaf(Character.Matching(c => inClass(c) != negate, name), c => c.ToString(), name);
            }

            case "not":
                return BuildNot(node, rule);

            case "seq":
                return BuildSeq(node, rule);

            case "choice":
                return BuildChoice(node, rule);

            case "repeat":
                return BuildRepeat(node, rule);

            case "try":
                return Child(node, 0, rule);

            case "between":
                return BuildBetween(node, rule);

            case "delimited":
                return BuildDelimited(node, rule);

            case "convert":
            {
                string to = node.Text ?? "text";
                return Map(Child(node, 0, rule), v => SpecValues.Convert(to, v), to);
            }

            case "where":
            {
                var test = SpecValues.Predicate(node.Text);
                if (test is null) _diagnostics.Add(new SpecDiagnostic(rule, $"'{node.Text}' is not a test (len:2-4, range:0..255 or regex:...)."));
                string what = "text that is " + node.Text;
                var    inner = Child(node, 0, rule);
                return input =>
                       {
                           var r = inner(input);
                           if (!r.HasValue || test is null || test(SpecValues.Text(r.Value))) return r;

                           return NoteFail(input, what);
                       };
            }

            case "named":
            {
                string name = node.Name ?? string.Empty;
                return Map(Child(node, 0, rule), v => new NamedValue(name, v), name);
            }

            default:
                _diagnostics.Add(new SpecDiagnostic(rule, $"There is no kind of node called '{node.Kind}'."));
                return Fail($"a node I know ('{node.Kind}')");
        }
    }

    private TextParser<object?> Child(SpecNode node, int index, string rule)
    {
        if (index < node.Children.Count) return Build(node.Children[index], rule);

        _diagnostics.Add(new SpecDiagnostic(rule, $"A '{node.Kind}' needs something inside it."));
        return Fail($"something inside the '{node.Kind}'");
    }

    private TextParser<object?> BuildToken(SpecNode node, string rule)
    {
        var info = SpecCatalog.Token(node.Text);
        if (info is null && Doc.FindFragment(node.Text, FragmentKind.Token) is { Node: { } inner } fragment)
        {
            if (!_tokenStack.Add(fragment.Name))
            {
                _diagnostics.Add(new SpecDiagnostic(rule, $"The token '{fragment.Name}' is made of itself."));
                return Fail($"a token '{fragment.Name}' that is not made of itself");
            }

            try
            {
                return Build(inner, rule);
            }
            finally
            {
                _tokenStack.Remove(fragment.Name);
            }
        }

        if (info is null)
        {
            _diagnostics.Add(new SpecDiagnostic(rule, $"There is no token called '{node.Text}'."));
            return Fail($"a token called '{node.Text}'");
        }

        if (info.Id == "EOF") return input => input.IsAtEnd ? Result.Value<object?>(null, input, input) : NoteFail(input, "end of input");
        if (info.Id == "accept") return Accept();

        var test = info.Test!;
        return Leaf(Character.Matching(test, info.Id), c => c.ToString(), info.Id);
    }

    private TextParser<object?> BuildSeq(SpecNode node, string rule)
    {
        if (node.Children.Count == 0) return Accept();

        var items = node.Children.Select(c => Build(c, rule)).ToArray();
        var keep  = node.Children.Select(c => !c.Ignore && c.Kind != "not").ToArray();
        int kept  = keep.Count(k => k);
        var skip  = _ignore;

        return input =>
               {
                   var rest   = input;
                   var values = new List<object?>(items.Length);
                   for (int i = 0; i < items.Length; i++)
                   {
                       if (i > 0 && skip is not null) rest = Skip(rest, skip);

                       var r = items[i](rest);
                       if (!r.HasValue) return r;

                       if (keep[i]) values.Add(r.Value);
                       rest = r.Remainder;
                   }

                   object? value = kept == 0 ? null : kept == 1 ? values[0] : values.ToArray();
                   return Result.Value(value, input, rest);
               };
    }

    private TextParser<object?> BuildChoice(SpecNode node, string rule)
    {
        if (node.Children.Count == 0) return Fail("one of the choices (there are none)");

        var items = node.Children.Select(c => Build(c, rule)).ToArray();
        return input =>
               {
                   foreach (var item in items)
                   {
                       var r = item(input);
                       if (r.HasValue) return r;
                   }

                   return NoteFail(input, "one of the choices");
               };
    }

    private TextParser<object?> BuildRepeat(SpecNode node, string rule)
    {
        var  item = Child(node, 0, rule);
        bool keep = node.Children.Count == 0 || !node.Children[0].Ignore;
        int  min  = Math.Max(0, node.Min);
        int? max  = node.Max;
        var  skip = _ignore;

        return input =>
               {
                   var rest   = input;
                   var values = new List<object?>();
                   int count  = 0;

                   while (max is null || count < max)
                   {
                       var attempt = count > 0 && skip is not null ? Skip(rest, skip) : rest;
                       var r       = item(attempt);
                       if (!r.HasValue) break;

                       if (keep) values.Add(r.Value);
                       count++;
                       bool empty = Position(r.Remainder) == Position(attempt);
                       rest = r.Remainder;

                       // an empty match would match for ever: it counts for all that is still wanted, and stops
                       if (empty)
                       {
                           count = Math.Max(count, min);
                           break;
                       }
                   }

                   if (count < min) return NoteFail(input, $"at least {min} of it (found {count})");

                   return Result.Value<object?>(values.ToArray(), input, rest);
               };
    }

    private TextParser<object?> BuildBetween(SpecNode node, string rule)
    {
        if (node.Children.Count != 3)
        {
            _diagnostics.Add(new SpecDiagnostic(rule, "Between needs an opening, a body and a closing."));
            return Fail("an opening, a body and a closing");
        }

        var open  = Build(node.Children[0], rule);
        var body  = Build(node.Children[1], rule);
        var close = Build(node.Children[2], rule);
        var skip  = _ignore;

        return input =>
               {
                   var a = open(input);
                   if (!a.HasValue) return a;

                   var rest = skip is null ? a.Remainder : Skip(a.Remainder, skip);
                   var b    = body(rest);
                   if (!b.HasValue) return b;

                   rest = skip is null ? b.Remainder : Skip(b.Remainder, skip);
                   var c = close(rest);
                   if (!c.HasValue) return c;

                   return Result.Value(b.Value, input, c.Remainder);
               };
    }

    private TextParser<object?> BuildDelimited(SpecNode node, string rule)
    {
        if (node.Children.Count != 2)
        {
            _diagnostics.Add(new SpecDiagnostic(rule, "A delimited list needs an item and a separator."));
            return Fail("an item and a separator");
        }

        var item = Build(node.Children[0], rule);
        var sep  = Build(node.Children[1], rule);
        int min  = Math.Max(0, node.Min);
        var skip = _ignore;

        return input =>
               {
                   var values = new List<object?>();
                   var first  = item(input);
                   if (!first.HasValue)
                       return min == 0 ? Result.Value<object?>(Array.Empty<object?>(), input, input) : first;

                   values.Add(first.Value);
                   var rest = first.Remainder;

                   while (true)
                   {
                       var before = skip is null ? rest : Skip(rest, skip);
                       var s      = sep(before);
                       if (!s.HasValue) break;

                       var after = skip is null ? s.Remainder : Skip(s.Remainder, skip);
                       var next  = item(after);
                       if (!next.HasValue) break; // the separator belongs to whatever comes after, not to the list

                       values.Add(next.Value);
                       rest = next.Remainder;
                   }

                   return Result.Value<object?>(values.ToArray(), input, rest);
               };
    }

    /// <summary>Matches, using up nothing, where the child does not match. What the child noted while it was tried is not an error of the text.</summary>
    private TextParser<object?> BuildNot(SpecNode node, string rule)
    {
        var    inner = Child(node, 0, rule);
        string what  = node.Children.Count > 0 ? "not " + node.Children[0].Describe() : "not (nothing)";

        return input =>
               {
                   var outer = _tracker;
                   _tracker = new Tracker();
                   Result<object?> r;
                   try
                   {
                       r = inner(input);
                   }
                   finally
                   {
                       _tracker = outer;
                   }

                   return r.HasValue ? NoteFail(input, what) : Result.Value<object?>(null, input, input);
               };
    }

    private TextParser<object?> BuildForeignRef(SpecNode node, string rule)
    {
        string spec   = node.Spec!;
        string target = node.Text ?? string.Empty;
        if (!Doc.Imports.Contains(spec, StringComparer.OrdinalIgnoreCase))
        {
            _diagnostics.Add(new SpecDiagnostic(rule, $"The spec '{spec}' is not in this spec's imports."));
            return Fail($"a rule of an imported spec ('{spec}' is not imported)");
        }

        var other = Imported(spec, rule);
        if (other is null) return Fail($"a rule of the spec '{spec}'");

        if (other.RuleHolderOf(target) is not { } holder)
        {
            _diagnostics.Add(new SpecDiagnostic(rule, $"There is no rule called '{target}' in the spec '{spec}'."));
            return Fail($"a rule called '{target}' in '{spec}'");
        }

        return input => holder.Parser is { } p ? p(input) : NoteFail(input, $"a definition of '{spec}/{target}'");
    }

    private CompiledSpec? Imported(string spec, string rule)
    {
        if (_imports.TryGetValue(spec, out var made)) return made;

        CompiledSpec? result = null;
        int at = _chain.FindIndex(c => string.Equals(c, spec, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            _diagnostics.Add(new SpecDiagnostic(rule, "Specs cannot import each other in a circle: " + string.Join(" → ", _chain.Skip(at).Append(spec))));
        }
        else if (_load?.Invoke(spec) is not { } doc)
        {
            _diagnostics.Add(new SpecDiagnostic(rule, $"The spec '{spec}' is not in the Silo."));
        }
        else
        {
            result = new CompiledSpec(doc.Clone(), _load, [.. _chain, spec]);
            if (result.Diagnostics.Count > 0)
                _diagnostics.Add(new SpecDiagnostic(rule, $"The imported spec '{spec}' has a problem: {result.Diagnostics[0].Rule}: {result.Diagnostics[0].Message}"));
        }

        _imports[spec] = result;
        return result;
    }

    // ---- small parts ---------------------------------------------------------------------------

    private static TextParser<object?> Accept() => input => Result.Value<object?>(null, input, input);

    private static TextParser<object?> Fail(string expected) => input => NoteFail(input, expected);

    /// <summary>Notes that <paramref name="expected" /> was wanted at the start of <paramref name="input" />, and fails.</summary>
    private static Result<object?> NoteFail(TextSpan input, string expected)
    {
        _tracker?.Note(Position(input), expected);
        return Result.Empty<object?>(input, "expected " + expected);
    }

    /// <summary>A fixed string, matched character by character (so it does not depend on which helpers a version of Superpower has); the value is the text as written in the input.</summary>
    private static TextParser<object?> Literal(string text, bool ignoreCase)
    {
        string expected = "\"" + text + "\"";
        var    compare  = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return input =>
               {
                   var rest    = input;
                   var matched = new System.Text.StringBuilder(text.Length);
                   foreach (char want in text)
                   {
                       var c = rest.ConsumeChar();
                       if (!c.HasValue || !char.ToString(c.Value).Equals(char.ToString(want), compare)) return NoteFail(input, expected);

                       matched.Append(c.Value);
                       rest = c.Remainder;
                   }

                   return Result.Value<object?>(matched.ToString(), input, rest);
               };
    }

    /// <summary>A Superpower parser of one kind of thing made a parser of values (what was matched, as text); a failure is noted for the error message.</summary>
    private static TextParser<object?> Leaf<T>(TextParser<T> parser, Func<T, string> text, string expected)
    {
        return input =>
               {
                   var r = parser(input);
                   if (r.HasValue) return Result.Value<object?>(text(r.Value), r.Location, r.Remainder);

                   return NoteFail(input, expected);
               };
    }

    /// <summary>A parser whose value is changed; if the change fails the parser fails (at the start) saying what it wanted.</summary>
    private static TextParser<object?> Map(TextParser<object?> parser, Func<object?, object?> change, string expected)
    {
        return input =>
               {
                   var r = parser(input);
                   if (!r.HasValue) return r;

                   try
                   {
                       return Result.Value(change(r.Value), r.Location, r.Remainder);
                   }
                   catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException)
                   {
                       _tracker?.Note(Position(input), $"{expected} ({ex.Message})");
                       return Result.Empty<object?>(input, ex.Message);
                   }
               };
    }

    private static TextSpan Skip(TextSpan span, Func<char, bool> ignored)
    {
        while (true)
        {
            var c = span.ConsumeChar();
            if (!c.HasValue || !ignored(c.Value)) return span;

            span = c.Remainder;
        }
    }

    private bool AtExit(TextSpan rest)
    {
        if (_exit is null) return false;

        var c = rest.ConsumeChar();
        return c.HasValue && _exit(c.Value);
    }

    private static int Position(TextSpan span) => span.Position.Absolute;

    private static Func<char, bool>? TestOf(IEnumerable<string> tokenIds)
    {
        var tests = tokenIds.Select(SpecCatalog.Token).Where(t => t?.Test is not null).Select(t => t!.Test!).ToArray();
        if (tests.Length == 0) return null;

        return c =>
               {
                   foreach (var t in tests)
                       if (t(c))
                           return true;
                   return false;
               };
    }

    private sealed class RuleHolder(string name)
    {
        public string Name { get; } = name;

        public TextParser<object?>? Parser { get; set; }
    }

    /// <summary>Remembers the furthest place any attempt reached, and what it wanted there.</summary>
    private sealed class Tracker
    {
        public int Position { get; private set; } = -1;

        public string? Expected { get; private set; }

        public void Note(int position, string expected)
        {
            if (position > Position)
            {
                Position = position;
                Expected = expected;
            }
            else if (position == Position && Expected is not null && !Expected.Split(" or ").Contains(expected) && Expected.Length < 120)
            {
                Expected += " or " + expected;
            }
        }
    }
}
