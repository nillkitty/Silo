using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Silo.Extensions;
using Silo.Meta;
using Telefrag.Collections;
using Telefrag.Common;

namespace Silo.DbModel.Meta;

public class UnitParser
{
    public UnitSchema Schema { get; }

    public UnitParser(UnitSchema unitSchema)
    {
        Schema = unitSchema.Required();
    }

    public List<ParsedText> Tokenize(string input)
    {
        input.Required();
        List<ParsedText> l  = [];
        var              ps = new Fragment(0, 0, input);
        while (!ps.AtEnd)
        {
            ps.Length++;
            if (ParseToken(ps) is ParsedText pt)
            {
                l.Add(pt);
                ps.Eat();
            }
        }

        return l;
    }

    private ParsedText? ParseToken(Fragment ps)
    {
        if (ps.IsEmpty)
            return null;

        var x = ps.Read();
        if (ps.IsWhitespace)
        {
            ps.Eat();
            return null;
        }

        if (ps.Is("/"))
            return new ParsedSlash(ps);

        var u = Schema.Find(ps.Read().ToString());
        return u switch
               {
                   null => null,
                   { Type: UnitType.Alias, Target: Unit uu } =>
                       new ParsedUnit(ps, uu),
                   { Type: UnitType.Unit }       => new ParsedUnit(ps, u),
                   { Type: UnitType.Conversion } => new ParsedConversion(ps, u),
                   { Type: UnitType.Prefix }     => new ParsedPrefix(ps, u),
                   { Type: UnitType.Suffix }     => new ParsedSuffix(ps, u),
                   _ => throw new
                            InvalidOperationException($"Unexpected unit type '{u.Type}'")
               };
    }
}

public record UnitSchema
{
    private readonly FragTable<Unit, string> _units = [];

    public UnitSchema(Schema source, DateTime effectiveUtc, List<Unit> units)
    {
        Source       = source;
        EffectiveUtc = effectiveUtc;
        Units        = units ?? [];
        foreach (var u in Units)
        {
            u.Validate();
            if (u.Name is string n) _add(n,         u);
            if (u.NamePlural is string pl) _add(pl, u);
        }
    }

    private void _add(string name, Unit u)
    {
        var e = _units[name];
        if (e != null && e != u)
            throw new
                InvalidOperationException($"The name '{name}' is already in use for unit or conversion '{e}'");

        _units[name] = u;
    }

    public Schema     Source       { get; init; }
    public DateTime   EffectiveUtc { get; init; }
    public List<Unit> Units        { get; init; }

    public Unit? Find(string input)
    {
        return _units[input];
    }
}

public struct Fragment(int offset, int length, string input)
{
    public int    Offset        { get; set; } = offset;
    public int    Length        { get; set; } = length;
    public string Input         { get; }      = input.Required();
    public bool   AtEnd         => Offset + Length >= input.Length;
    public int    SizeFromHere  => input.Length - Offset;
    public int    SizeRemaining => input.Length - Offset - Length;
    public bool   IsEmpty       => SizeRemaining == 0;
    public bool   IsWhitespace  => string.IsNullOrWhiteSpace(Read().ToString());

    public int Eat()
    {
        var x = Length;
        Offset += Length;
        Length =  0;
        return x;
    }

    public ReadOnlySpan<char> Read() => Input.Substring(Offset, Length);

    public bool Is(string s)
    {
        s.Required();
        return Telefrag.Common.TelefragCommonExtensions
                       .Is(s, Read().ToString());
    }
}

public record ParsedText(Fragment Fragment);

public record ParsedSlash(Fragment Fragment) : ParsedText(Fragment);

public record ParsedNumber(Fragment Fragment, double Value)
    : ParsedText(Fragment);

public record ParsedUnit(Fragment Fragment, Unit Unit) : ParsedText(Fragment);

public record ParsedPrefix(Fragment Fragment, Unit Prefix)
    : ParsedText(Fragment);

public record ParsedSuffix(Fragment Fragment, Unit Suffix)
    : ParsedText(Fragment);

public record ParsedConversion(Fragment Fragment, Unit Conversion)
    : ParsedText(Fragment);