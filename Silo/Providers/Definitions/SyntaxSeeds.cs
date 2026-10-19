using Silo.Contracts.Providers.Data;
using Silo.DbModel.Meta;

namespace Silo.Providers.Definitions;

/// <summary>
///     The syntaxes every new Silo starts with (and that Restore Defaults brings back): one row each, built on the parsers in
///     <see cref="SyntaxParsers" />. They are ordinary rows marked <see cref="SyntaxFlags.Seeded" />; a Silo can change or delete them.
/// </summary>
public static class SyntaxSeeds
{
    private static SyntaxRecord Seed(string name, string parser, string? alias = null)
    {
        var p = SyntaxParsers.Find(parser) ?? throw new InvalidOperationException($"No built-in parser \"{parser}\".");
        return SyntaxRecord.ForParser(name, p.Key, alias, p.Description, p.Example, SyntaxFlags.Seeded);
    }

    /// <summary>The predefined syntaxes, in the order they are added.</summary>
    public static IReadOnlyList<SyntaxRecord> Defaults { get; } =
    [
        Seed("Timestamp",          "timestamp"),
        Seed("Duration",           "duration"),
        Seed("Guid",               "guid"),
        Seed("Regex",              "regex"),
        Seed("Wildcard",           "wildcard"),
        Seed("IPAddress",          "ipaddress"),
        Seed("IPSubnet",           "ipsubnet"),
        Seed("Version",            "version"),
        Seed("Oid",                "oid"),
        Seed("SecurityIdentifier", "securityidentifier"),
        Seed("JObject",            "jobj", "jobj"),
        Seed("JArray",             "jarr", "jarr"),
        Seed("JToken",             "jtok", "jtok"),
        Seed("Base64",             "base64"),
        Seed("Html",               "html"),
        Seed("Csv",                "csv"),
        Seed("Tsv",                "tsv"),
        Seed("CliArgs",            "cliargs"),
        Seed("CliCommand",         "clicommand")
    ];
}
