using System.Text.RegularExpressions;
using Silo.DbModel.Meta;
using Silo.Providers.Data;
using Silo.Providers.Data.Shape;

namespace Silo.Providers.Definitions;

/// <summary>
///     A syntax defined in a Silo, working as an <see cref="ISyntax" />: built from its <see cref="SyntaxRecord" />,
///     either on one of the
///     built-in parsers (values become real objects) or on a pattern the whole string has to match (values stay strings).
/// </summary>
public sealed class StoredSyntax : ISyntax
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private readonly SyntaxParser? _parser;
    private readonly Regex?        _pattern;

    public SyntaxRecord Record { get; }

    /// <summary>The type of object <see cref="GetValue" /> gives: the parser's, or string for a pattern.</summary>
    public Type ValueType => _parser?.ValueType ?? typeof(string);

    public string? DisplayName => Record.Name;

    /// <summary>
    ///     Builds the syntax a record describes. Returns false, with what is wrong, when the parser is unknown or the
    ///     pattern does not compile.
    /// </summary>
    public static bool TryCreate(SyntaxRecord record, out StoredSyntax? syntax, out string? error)
    {
        syntax = null;
        error  = null;

        switch (record.Kind)
        {
            case SyntaxKind.Parser:
                if (string.IsNullOrWhiteSpace(record.Parser))
                {
                    error = "Choose the parser this syntax is built on.";
                    return false;
                }

                if (SyntaxParsers.Find(record.Parser) is not { } parser)
                {
                    error =
                        $"There is no built-in parser \"{record.Parser.Trim()}\". Known parsers: {string.Join(", ", SyntaxParsers.All.Select(p => p.Key))}.";
                    return false;
                }

                syntax = new StoredSyntax(record, parser, null);
                return true;

            case SyntaxKind.Pattern:
                if (string.IsNullOrEmpty(record.Pattern))
                {
                    error = "A pattern is required.";
                    return false;
                }

                try
                {
                    var regex = new Regex($@"\A(?:{record.Pattern})\z", RegexOptions.CultureInvariant, MatchTimeout);
                    syntax = new StoredSyntax(record, null, regex);
                    return true;
                }
                catch (ArgumentException ex)
                {
                    error = "The pattern is not a valid regular expression: " + ex.Message;
                    return false;
                }

            default:
                error = "The syntax kind is not one of the known kinds.";
                return false;
        }
    }

    /// <summary>Builds the syntax a record describes; throws <see cref="DefinitionException" /> when it cannot be built.</summary>
    public static StoredSyntax Create(SyntaxRecord record)
    {
        if (TryCreate(record, out var syntax, out string? error)) return syntax!;
        throw new DefinitionException(error ?? "The syntax cannot be built.");
    }

    /// <summary>Reads the text: whether it is valid, the object it becomes and, when it is not valid, why.</summary>
    public bool TryParse(string? text, out object? value, out string? error)
    {
        if (_parser is not null) return _parser.TryParse(text, out value, out error);

        value = null;
        error = null;
        if (text is null)
        {
            error = "There is no value.";
            return false;
        }

        try
        {
            if (_pattern!.IsMatch(text))
            {
                value = text;
                return true;
            }

            error = "The text does not match the pattern " + Record.Pattern;
        }
        catch (RegexMatchTimeoutException)
        {
            error = "Checking the pattern took too long.";
        }

        return false;
    }

    private StoredSyntax(SyntaxRecord record, SyntaxParser? parser, Regex? pattern)
    {
        Record   = record;
        _parser  = parser;
        _pattern = pattern;
    }

    public bool IsValid(string value)
    {
        return TryParse(value, out _, out _);
    }

    /// <summary>
    ///     The string stored for a value: a value of <see cref="ValueType" /> is written canonically, a string is read
    ///     first and written the same way.
    /// </summary>
    public string GetString(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (_parser is null)
        {
            string text = value as string ?? throw new ArgumentException($"{Record.Name} values are strings.", nameof(value));
            if (!IsValid(text)) throw new FormatException($"\"{text}\" is not a valid {Record.Name}.");
            return text;
        }

        if (value is string asText) return _parser.Format(GetValue(asText));
        return _parser.Format(value);
    }

    /// <summary>The object a stored string stands for; throws <see cref="FormatException" /> when the string is not valid.</summary>
    public object GetValue(string value)
    {
        if (TryParse(value, out object? result, out string? error)) return result!;
        throw new FormatException($"Not a valid {Record.Name}: {error}");
    }
}