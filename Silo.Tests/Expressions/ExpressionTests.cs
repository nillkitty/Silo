using Silo.Expressions;
using Silo.Unicode;

namespace Silo.Tests.Expressions;

[TestFixture]
public class ExpressionTests
{
    private static VariableScope Scope() => new VariableScope()
        .Set("name", "Nill")
        .Set("count", 3L)
        .Set("nothing", null)
        .AddNamespace("your", new VariableScope()
                              .Set("blue", new CodePoint(0x1F535))
                              .Set("green", new CodePoint(0x1F7E2))
                              .Set("mychars", new[] { new CodePoint(0x25B6), new CodePoint(0x221A) }));

    private static string Expand(string source, EvaluationOptions? options = null) => Template.Expand(source, Scope(), options);

    private static object? Eval(string source) => ExpressionEvaluator.Evaluate(source, Scope());

    // ---- templates -------------------------------------------------------------------------------

    [Test]
    public void PlainTextIsLeftAlone()
    {
        Assert.That(Expand("no expressions here"), Is.EqualTo("no expressions here"));
        Assert.That(Expand(""), Is.EqualTo(""));
        Assert.That(Expand("price: $5 {not} $"), Is.EqualTo("price: $5 {not} $"));
        Assert.That(Template.Parse("just text").IsLiteral, Is.True);
    }

    [Test]
    public void ReplacesVariablesByTheirValues()
    {
        Assert.That(Expand("Hello ${name}!"), Is.EqualTo("Hello Nill!"));
        Assert.That(Expand("${your:blue}"), Is.EqualTo("\U0001F535"));
        Assert.That(Expand("type ${your:blue} and ${your:green}"), Is.EqualTo("type \U0001F535 and \U0001F7E2"));
        Assert.That(Expand("${ name }"), Is.EqualTo("Nill"));
        Assert.That(Expand("${your:mychars}"), Is.EqualTo("▶√"));
        Assert.That(Expand("${count} items"), Is.EqualTo("3 items"));
        Assert.That(Expand("[${nothing}]"), Is.EqualTo("[]"));
    }

    [Test]
    public void NamesAndNamespacesIgnoreCase()
    {
        Assert.That(Expand("${NAME} ${Your:BLUE}"), Is.EqualTo("Nill \U0001F535"));
    }

    [Test]
    public void ADollarBraceCanBeEscaped()
    {
        Assert.That(Expand("$${name}"), Is.EqualTo("${name}"));
        Assert.That(Expand("a $${ b ${name}"), Is.EqualTo("a ${ b Nill"));
    }

    [Test]
    public void BraceInsideTextDoesNotEndTheExpression()
    {
        Assert.That(Expand("${'a}b'}"), Is.EqualTo("a}b"));
        Assert.That(Expand("${\"}\" + name}"), Is.EqualTo("}Nill"));
    }

    [Test]
    public void ReportsAnUnknownVariableWithItsPlace()
    {
        var ex = Assert.Throws<ExpressionException>(() => Expand("type ${your:red}"));
        Assert.That(ex!.MissingVariableName, Is.EqualTo("your:red"));
        Assert.That(ex.Position, Is.EqualTo(7));
        Assert.That(ex.Describe(), Does.Contain("your:red"));
        Assert.Throws<ExpressionException>(() => Expand("${nope}"));
        Assert.Throws<ExpressionException>(() => Expand("${other:blue}"));
    }

    [Test]
    public void CanTreatMissingVariablesAsEmptyOrKeepThem()
    {
        Assert.That(Expand("a${nope}b", new EvaluationOptions { Missing = MissingVariableBehavior.Empty }), Is.EqualTo("ab"));
        Assert.That(Expand("a ${your:red} b ${name}", new EvaluationOptions { Missing = MissingVariableBehavior.KeepPlaceholder }), Is.EqualTo("a ${your:red} b Nill"));
    }

    [Test]
    public void RefusesBrokenTemplates()
    {
        foreach (string bad in new[] { "${", "${name", "${}", "${ }", "${name +}", "${1 2}", "${'open}", "${a b}", "${?}", "${name..x}", "${f(}", "${\\}" })
            Assert.Throws<ExpressionException>(() => Template.Parse(bad), bad);

        Assert.That(Template.TryParse("${name", out var t, out var error), Is.False);
        Assert.That(t, Is.Null);
        Assert.That(error!.Position, Is.GreaterThan(-1));
        Assert.That(Template.TryParse("ok ${name}", out var ok, out _), Is.True);
        Assert.That(ok!.Variables.Count, Is.EqualTo(1));
        Assert.That(Template.TryParse(null, out _, out var nullError), Is.False);
        Assert.That(nullError, Is.Not.Null);
    }

    [Test]
    public void ListsTheVariablesATemplateUses()
    {
        var t = Template.Parse("${your:blue}${name} ${upper(your:green)} ${x ?? y}");
        Assert.That(t.Variables.Select(v => v.FullName).ToList(), Is.EqualTo(new List<string> { "your:blue", "name", "your:green", "x", "y" }));
        Assert.That(t.IsLiteral, Is.False);
        Assert.That(t.Parts.Count, Is.EqualTo(6));
    }

    // ---- expressions -------------------------------------------------------------------------------

    [Test]
    public void ReadsLiterals()
    {
        Assert.That(Eval("42"), Is.EqualTo(42L));
        Assert.That(Eval("0x1F7E2"), Is.EqualTo(0x1F7E2L));
        Assert.That(Eval("1.5"), Is.EqualTo(1.5));
        Assert.That(Eval("'single'"), Is.EqualTo("single"));
        Assert.That(Eval("\"double\""), Is.EqualTo("double"));
        Assert.That(Eval("true"), Is.EqualTo(true));
        Assert.That(Eval("false"), Is.EqualTo(false));
        Assert.That(Eval("null"), Is.Null);
    }

    [Test]
    public void ReadsTheEscapesInText()
    {
        Assert.That(Eval("'a\\nb\\tc\\\\d\\'e'"), Is.EqualTo("a\nb\tc\\d'e"));
        Assert.That(Eval("'\\u0041'"), Is.EqualTo("A"));
        Assert.That(Eval("'\\u{1F7E2}'"), Is.EqualTo("\U0001F7E2"));
        Assert.Throws<ExpressionException>(() => ExpressionParser.Parse("'\\q'"));
        Assert.Throws<ExpressionException>(() => ExpressionParser.Parse("'\\u12'"));
        Assert.Throws<ExpressionException>(() => ExpressionParser.Parse("'\\u{110000}'"));
        Assert.Throws<ExpressionException>(() => ExpressionParser.Parse("'never closed"));
    }

    [Test]
    public void AddsNumbersAndJoinsText()
    {
        Assert.That(Eval("1 + 2 + 3"), Is.EqualTo(6L));
        Assert.That(Eval("1.5 + 1"), Is.EqualTo(2.5));
        Assert.That(Eval("'a' + 'b'"), Is.EqualTo("ab"));
        Assert.That(Eval("name + ' has ' + count"), Is.EqualTo("Nill has 3"));
        Assert.That(Eval("your:blue + your:green"), Is.EqualTo("\U0001F535\U0001F7E2"));
        Assert.That(Eval("(1 + 2) + 'x'"), Is.EqualTo("3x"));
    }

    [Test]
    public void CoalescesMissingAndNullValues()
    {
        Assert.That(Eval("nope ?? 'fallback'"), Is.EqualTo("fallback"));
        Assert.That(Eval("nothing ?? 'fallback'"), Is.EqualTo("fallback"));
        Assert.That(Eval("name ?? 'fallback'"), Is.EqualTo("Nill"));
        Assert.That(Eval("nope ?? also ?? 'last'"), Is.EqualTo("last"));
        Assert.That(Eval("your:red ?? your:blue"), Is.EqualTo(new CodePoint(0x1F535)));
        Assert.Throws<ExpressionException>(() => Eval("nope1 ?? nope2 + nope3"));
        Assert.That(Expand("${your:red ?? 'none'}"), Is.EqualTo("none"));
    }

    [Test]
    public void CallsFunctions()
    {
        Assert.That(Eval("upper(name)"), Is.EqualTo("NILL"));
        Assert.That(Eval("lower('ABC')"), Is.EqualTo("abc"));
        Assert.That(Eval("trim('  x ')"), Is.EqualTo("x"));
        Assert.That(Eval("len('a\\u{1F7E2}b')"), Is.EqualTo(3L));
        Assert.That(Eval("repeat('ab', 3)"), Is.EqualTo("ababab"));
        Assert.That(Eval("join(', ', 'a', 'b', 'c')"), Is.EqualTo("a, b, c"));
        Assert.That(Eval("join('-', your:mychars)"), Is.EqualTo("▶-√"));
        Assert.That(Eval("char(0x1F7E2)"), Is.EqualTo("\U0001F7E2"));
        Assert.That(Eval("char('U+25B6', 0x221A)"), Is.EqualTo("▶√"));
        Assert.That(Eval("codepoint(your:green)"), Is.EqualTo(0x1F7E2L));
        Assert.That(Eval("hex(255)"), Is.EqualTo("FF"));
        Assert.That(Eval("UPPER('x')"), Is.EqualTo("X"));
        Assert.That(Expand("${upper(your:blue)}${repeat('!', 2)}"), Is.EqualTo("\U0001F535!!"));
    }

    [Test]
    public void ReportsBadCalls()
    {
        Assert.Throws<ExpressionException>(() => Eval("nosuchfunction(1)"));
        Assert.Throws<ExpressionException>(() => Eval("upper()"));
        Assert.Throws<ExpressionException>(() => Eval("repeat('a', 100000)"));
        Assert.Throws<ExpressionException>(() => Eval("repeat('a', 'x')"));
        Assert.Throws<ExpressionException>(() => Eval("char(0x110000)"));
        Assert.Throws<ExpressionException>(() => Eval("char('nope')"));
        var ex = Assert.Throws<ExpressionException>(() => Eval("  upper()"));
        Assert.That(ex!.Position, Is.EqualTo(2));
    }

    [Test]
    public void ReachesIntoValues()
    {
        var scope = new VariableScope().Set("row", new Dictionary<string, object?> { ["id"] = 7L, ["tags"] = new List<object?> { "a", "b" } });
        Assert.That(ExpressionEvaluator.Evaluate("row.id", scope), Is.EqualTo(7L));
        Assert.That(ExpressionEvaluator.Evaluate("row.tags[1]", scope), Is.EqualTo("b"));
        Assert.That(ExpressionEvaluator.Evaluate("row.tags.count", scope), Is.EqualTo(2L));
        Assert.That(ExpressionEvaluator.Evaluate("row['id']", scope), Is.EqualTo(7L));
        Assert.That(ExpressionEvaluator.Evaluate("row.missing", scope), Is.Null);
        Assert.That(ExpressionEvaluator.Evaluate("'héllo'.length", scope), Is.EqualTo(5L));
        Assert.That(ExpressionEvaluator.Evaluate("'abc'[1]", scope), Is.EqualTo("b"));
        Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate("row.tags[5]", scope));
        Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate("5.nope", scope));
        Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate("row.id.nope", scope));
        Assert.That(ExpressionEvaluator.Evaluate("your:x.hex", new VariableScope().AddNamespace("your", new VariableScope().Set("x", new CodePoint(0x41)))), Is.EqualTo("U+0041"));
    }

    [Test]
    public void ScopesCanInherit()
    {
        var parent = new VariableScope().Set("a", "from parent").Set("b", "parent b");
        var child  = new VariableScope(parent).Set("b", "child b");
        Assert.That(Template.Expand("${a} ${b}", child), Is.EqualTo("from parent child b"));
        Assert.That(Template.Expand("${a} ${b}", parent), Is.EqualTo("from parent parent b"));
    }

    [Test]
    public void FunctionsCanBeAdded()
    {
        var functions = ExpressionFunctions.CreateDefault().Add("shout", a => ExpressionValues.ToText(a[0]).ToUpperInvariant() + "!");
        Assert.That(Template.Expand("${shout('hey')}", Scope(), new EvaluationOptions { Functions = functions }), Is.EqualTo("HEY!"));
        Assert.Throws<ExpressionException>(() => Template.Expand("${shout('hey')}", Scope()));
    }

    [Test]
    public void PrintsExpressionsAsSourceThatParsesBack()
    {
        foreach (string source in new[] { "your:blue", "upper(name) + 'x'", "a ?? b ?? 'c'", "row.tags[1]", "1 + 2", "'it\\'s'", "f(g(1), 'x')" })
        {
            var expr = ExpressionParser.Parse(source);
            Assert.That(ExpressionParser.Parse(expr.ToSource()).ToSource(), Is.EqualTo(expr.ToSource()), source);
        }
    }

    [Test]
    public void WritesValuesAsText()
    {
        Assert.That(ExpressionValues.ToText(null), Is.EqualTo(""));
        Assert.That(ExpressionValues.ToText(true), Is.EqualTo("true"));
        Assert.That(ExpressionValues.ToText(1.5), Is.EqualTo("1.5"));
        Assert.That(ExpressionValues.ToText(new List<object?> { 1L, "a" }), Is.EqualTo("1, a"));
        Assert.That(ExpressionValues.ToText(new CodePoint(0x41)), Is.EqualTo("A"));
        Assert.That(ExpressionValues.IsNumber(3), Is.True);
        Assert.That(ExpressionValues.IsNumber("3"), Is.False);
    }
}
