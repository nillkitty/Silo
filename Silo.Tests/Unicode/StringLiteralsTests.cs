using NUnit.Framework;
using Silo.Unicode;

namespace Silo.Tests.Unicode;

[TestFixture]
public class StringLiteralsTests
{
    private static CodePoint[] Cps(params int[] values) => values.Select(v => new CodePoint(v)).ToArray();

    [Test]
    public void CSharp_KeepsPrintableAsciiAndEscapesTheRest()
    {
        Assert.That(StringLiterals.CSharp(Cps('a', 0x25B6, 0x1F7E2)), Is.EqualTo("\"a\\u25B6\\U0001F7E2\""));
    }

    [Test]
    public void Json_WritesSurrogatePairsForAstralCodePoints()
    {
        Assert.That(StringLiterals.Json(Cps(0x25B6, 0x1F7E2)), Is.EqualTo("\"\\u25B6\\uD83D\\uDFE2\""));
    }

    [Test]
    public void Quotes_Backslashes_AndControlsAreEscaped()
    {
        Assert.That(StringLiterals.CSharp(Cps('"', '\\', '\n', '\t', 0)), Is.EqualTo("\"\\\"\\\\\\n\\t\\u0000\""));
    }

    [Test]
    public void Empty_IsEmptyLiteral()
    {
        Assert.That(StringLiterals.Json([]), Is.EqualTo("\"\""));
    }

    [Test]
    public void Json_RoundTripsThroughAJsonParser()
    {
        var cps = Cps('x', 0x1F7E2, 0xE9, '"');
        string literal = StringLiterals.Json(cps);
        Assert.That(Newtonsoft.Json.JsonConvert.DeserializeObject<string>(literal), Is.EqualTo(CodePoint.ToText(cps)));
    }
}
