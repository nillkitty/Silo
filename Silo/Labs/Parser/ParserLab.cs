using System.Text.RegularExpressions;
using Silo.Providers.Data;
using Telefrag.DI;

namespace Silo.Labs.Parser;

/// <summary>The names of the Parser Lab's parameters (what <c>set</c> in its terminal and the saved text call them).</summary>
public static class ParserLabParameters
{
    public const string SpecName   = "specName";
    public const string SpecOid    = "specOid";
    public const string ClrType    = "clrType";
    public const string ConvertsTo = "convertsTo";
    public const string Ignore     = "ignoreTokens";
    public const string ExitOn     = "exitTokens";
    public const string Mode       = "mode";
    public const string TokenType  = "tokenType";
}

/// <summary>
///     A place to build a data specification (<see cref="IDataSpec" />) out of parsing rules and try it: drag tokens,
///     other rules and combinators
///     together on a canvas, keep samples of text it should and should not accept, write tests over them, and run text
///     through it in a terminal.
///     When it is right, the spec is compiled to a function that runs at full speed. The same canvas builds the tokenizer
///     a parser starts from.
/// </summary>
/// <remarks>
///     The parameters are the spec being built: its name, OID, CLR type, what it converts to, and which kinds of character
///     are skipped and
///     which end the text. Specs, their samples and their tests are kept in the Silo (see <see cref="SpecStore" />). The
///     rules are made into parsers
///     with the Superpower library (see <see cref="CompiledSpec" />); no Superpower type is part of
///     <see cref="IDataSpec" />.
/// </remarks>
[Singleton(typeof(ILab))]
public class ParserLab : LabBase<IDataSpec>
{
    private static readonly Regex SpecNameShape =
        new(@"^[A-Za-z_][\w\-]*(\.[A-Za-z_][\w\-]*)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public override string Description =>
        "Build a data specification from parsing rules, test it against samples, and compile it to a function that runs at full speed.";

    public override string IconKey => "Puzzle";

    public override string Id => "parser";

    public override string Name => "Parser Lab";

    public override int Order => 40;

    protected override IEnumerable<LabParameter> DefineParameters()
    {
        const string spec = "The spec";
        const string text = "Reading text";

        yield return new LabParameter(ParserLabParameters.SpecName, "Name", LabParameterKind.Text, "My.Data.Spec",
                                      "The fully qualified name of the data specification being built. A spec of this name kept in the Silo is opened with it.",
                                      spec, hint: "Net.Mac.Address");

        yield return new LabParameter(ParserLabParameters.SpecOid, "OID", LabParameterKind.Text, string.Empty,
                                      "The object identifier that tells this specification from any other in the metaverse and the universe. Empty for none.",
                                      spec, hint: "1.3.6.1.4.1.99999.1.1");

        yield return new LabParameter(ParserLabParameters.ClrType, "CLR type", LabParameterKind.Text, string.Empty,
                                      "The type a value of the spec is extracted as (a rule's own return type wins over it).", spec,
                                      hint: "System.Byte[]");

        yield return new LabParameter(ParserLabParameters.ConvertsTo, "Converts to", LabParameterKind.Lines, null,
                                      "The types and formats a value of the spec can be converted to, one per line; the first format is how the Terminal shows a value. Formats: " +
                                      string.Join(", ", SpecCatalog.Formats) + ".", spec, hint: "hex-dotted4\nhex-colon\nSystem.Guid");

        yield return new LabParameter(ParserLabParameters.Mode, "Mode", LabParameterKind.Choice, "Parser",
                                      "Parser: rules over characters. Tokenizer: rules that cut text into tokens first.", spec,
                                      ["Parser", "Tokenizer"]);

        yield return new LabParameter(ParserLabParameters.TokenType, "Token type", LabParameterKind.Text, "Token",
                                      "In the tokenizer mode: what the kinds of token are called as a type (an enum).", spec);

        yield return new LabParameter(ParserLabParameters.Ignore, "Ignore tokens", LabParameterKind.Flags, null,
                                      "Kinds of character that are skipped between the items the parser reads (white space, control characters).",
                                      text, SpecCatalog.IgnorableTokenIds);

        yield return new LabParameter(ParserLabParameters.ExitOn, "Exit on tokens", LabParameterKind.Flags, null,
                                      "Kinds of character that end the text instead of being an error (a white space ends a word). The end of the text always does.",
                                      text, SpecCatalog.IgnorableTokenIds);
    }

    public override LabSession<IDataSpec> CreateSession()
    {
        return new ParserLabSession(this);
    }

    protected override bool TryConvert(object? candidate, out IDataSpec? value)
    {
        switch (candidate)
        {
            case IDataSpec spec:
                value = spec;
                return true;

            // asked of every node when a menu is built: only the name of a spec that is kept is a spec
            case string text when SpecNameShape.IsMatch(text.Trim()) && SpecStore.Exists(text.Trim()) &&
                                  SpecStore.Load(text.Trim()) is { } doc:
                value = new ParserDataSpec(doc);
                return true;

            default:
                value = null;
                return false;
        }
    }

    /// <summary>Reads what is typed in the value box: the name of a kept spec, or a new name (which starts a new spec).</summary>
    public override bool TryParse(string text, out IDataSpec? value)
    {
        value = null;
        text  = text.Trim();
        if (!SpecNameShape.IsMatch(text)) return false;

        value = new ParserDataSpec(SpecStore.Load(text) ?? ParserWorkspace.NewDoc(text));
        return true;
    }
}