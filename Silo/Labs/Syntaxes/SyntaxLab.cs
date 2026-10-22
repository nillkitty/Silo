using Silo.Providers.Data;
using Silo.Providers.Definitions;
using Telefrag.DI;
using SyntaxKind = Silo.DbModel.Meta.SyntaxKind;

namespace Silo.Labs.Syntaxes;

public static class SyntaxLabParameters
{
    public const string NewKind = "newKind";
}

/// <summary>Builds and tests the Silo's named syntaxes: a built-in parser or a pattern, the strings that have to be valid and the ones that must not be, and what a syntax makes of each.</summary>
[Singleton(typeof(ILab))]
public class SyntaxLab : LabBase<SyntaxRecord>
{
    public override string Description => "Build and test the Silo's syntaxes: choose a parser or write a pattern, keep strings that have to be valid or invalid, see what each one becomes, and save the syntax in the Silo.";

    public override string IconKey => "Coding";

    public override string Id => "syntax";

    public override string Name => "Syntax Lab";

    public override int Order => 55;

    protected override IEnumerable<LabParameter> DefineParameters()
    {
        yield return new LabParameter(SyntaxLabParameters.NewKind, "Kind of a new syntax", LabParameterKind.Choice, nameof(SyntaxKind.Pattern),
                                      "What a new syntax starts as: a pattern of its own, or a built-in parser.", "Starting", [nameof(SyntaxKind.Pattern), nameof(SyntaxKind.Parser)]);
    }

    public override LabSession<SyntaxRecord> CreateSession()
    {
        return new SyntaxLabSession(this);
    }

    protected override bool TryConvert(object? candidate, out SyntaxRecord? value)
    {
        switch (candidate)
        {
            case SyntaxRecord record:
                value = record;
                return true;
            case SyntaxDraft draft:
                value = draft.ToRecord();
                return true;
            case StoredSyntax stored:
                value = stored.Record;
                return true;
            // asked of every node when a menu is built: only the syntax:Name way of referring to one is taken
            case string text when text.StartsWith("syntax:", StringComparison.OrdinalIgnoreCase) && text.Length > 7:
                value = SyntaxSilo.Find(text[7..]);
                return value is not null;
            default:
                value = null;
                return false;
        }
    }

    public override bool TryParse(string text, out SyntaxRecord? value)
    {
        string name = text.Trim();
        if (name.StartsWith("syntax:", StringComparison.OrdinalIgnoreCase)) name = name[7..];

        // a name the Silo knows opens that syntax, any other is a new syntax of that name
        value = name.Length == 0 ? null : SyntaxSilo.Find(name) ?? Blank(name);
        return value is not null;
    }

    internal SyntaxRecord Blank(string name = "")
    {
        var kind = Enum.TryParse<SyntaxKind>(Parameters[SyntaxLabParameters.NewKind].Text, out var k) ? k : SyntaxKind.Pattern;
        return new SyntaxRecord(0, name, null, null, kind, null, null, null);
    }
}
