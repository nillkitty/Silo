using System.Globalization;
using System.Text;
using Silo.DbModel.Meta;

namespace Silo.Labs.Schemas;

/// <summary>A draft as text: an outline to read, and a C# sketch of its classes and enums to start code from.</summary>
public static class SchemaText
{
    /// <summary>One line per construct, grouped by kind, classes with their attributes and bases.</summary>
    public static string Outline(SchemaDraft draft)
    {
        var sb = new StringBuilder();
        sb.Append(draft.Name).Append(" (promotes to ").Append(draft.Target == PromoteTarget.Meta ? "the metaverse" : "the local world").AppendLine(")");
        if (!string.IsNullOrWhiteSpace(draft.Description)) sb.AppendLine(draft.Description);

        foreach (var kind in ConstructKinds.All)
        {
            var items = draft.Of(kind).ToList();
            if (items.Count == 0) continue;

            sb.AppendLine().Append(kind.Plural()).AppendLine(":");
            foreach (var c in items) sb.Append("  ").AppendLine(Line(draft, c));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The one line a construct is described by.</summary>
    public static string Line(SchemaDraft draft, SchemaConstruct c)
    {
        switch (c.Kind)
        {
            case ConstructKind.Class:
                var bases    = SchemaCheck.Bases(draft, c.Name).ToList();
                var includes = SchemaCheck.Includes(draft, c.Name).ToList();
                string head  = c.Name + (c.Flag("auxiliary") ? " (auxiliary)" : string.Empty) + (bases.Count > 0 ? " : " + string.Join(", ", bases) : string.Empty) +
                               (includes.Count > 0 ? " + " + string.Join(", ", includes) : string.Empty);
                return c.Members.Count == 0 ? head : $"{head} [{string.Join(", ", c.Members)}]";
            case ConstructKind.Attribute:
                var parts = new List<string> { c.Get("type", "String") };
                if (c.Has("syntax")) parts.Add("syntax " + c.Get("syntax"));
                if (c.Has("flags")) parts.Add(c.Get("flags"));
                if (c.Has("default")) parts.Add("default " + c.Get("default"));
                return $"{c.Name}: {string.Join("; ", parts)}";
            case ConstructKind.Enum:
                string values = c.Values.Count == 0 ? "no values" : string.Join(", ", c.Values.Select(v => string.IsNullOrWhiteSpace(v.Value) ? v.Name : $"{v.Name}={v.Value}"));
                return $"{c.Name} ({c.Get("type", nameof(EnumType.Integral)).ToLowerInvariant()}{(c.Has("flags") ? ", " + c.Get("flags").ToLowerInvariant() : string.Empty)}): {values}";
            case ConstructKind.Syntax:
                return $"{c.Name}: {(c.Get("kind", nameof(SyntaxKind.Pattern)) == nameof(SyntaxKind.Parser) ? "parser " + c.Get("parser") : "pattern " + c.Get("pattern"))}";
            case ConstructKind.Relation:
                return c.Name;
            case ConstructKind.Unit:
                var u = new List<string> { c.Get("type", nameof(UnitType.Unit)).ToLowerInvariant() };
                if (c.Has("namePlural")) u.Add("plural " + c.Get("namePlural"));
                if (c.Has("target")) u.Add("of " + c.Get("target"));
                if (c.Has("relation")) u.Add("via " + c.Get("relation"));
                if (c.Has("formula")) u.Add("formula " + c.Get("formula"));
                if (c.Has("scale")) u.Add("scale " + c.Get("scale"));
                if (c.Flag("reciprocal")) u.Add("reciprocal");
                if (c.Has("tags")) u.Add(c.Get("tags"));
                return $"{c.Name}: {string.Join("; ", u)}";
            case ConstructKind.UnitRelation:
                return $"{c.Name}: {(c.Has("unit") ? "unit " + c.Get("unit") : "no unit")}{(c.Has("next") ? "; then " + c.Get("next") : string.Empty)}{(c.Has("scale") ? "; scale " + c.Get("scale") : string.Empty)}";
            default:
                return c.Name;
        }
    }

    /// <summary>What a plan would do, one line per step, and the problems that stop it.</summary>
    public static string Describe(PromotionPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append("Promote to ").AppendLine(plan.Target == PromoteTarget.Meta ? "the metaverse" : "the local world");
        foreach (var s in plan.Steps)
        {
            sb.Append("  ").Append(s.Action.ToString().PadRight(7)).Append(' ').Append(s.Kind.Title()).Append(' ').Append(s.Name);
            if (!string.Equals(s.StoredAs, s.Name, StringComparison.Ordinal)) sb.Append(" as ").Append(s.StoredAs);
            sb.Append(" - ").AppendLine(s.Why);
        }

        foreach (var p in plan.Problems.Where(p => p.Severity == SchemaSeverity.Error))
            sb.Append("  ERROR ").Append(p.Where).Append(": ").AppendLine(p.Text);

        return sb.ToString().TrimEnd();
    }

    private static readonly Dictionary<string, string> CsTypes = new()
                                                                 {
                                                                     ["String"]         = "string",
                                                                     ["Int32"]          = "int",
                                                                     ["Int64"]          = "long",
                                                                     ["Boolean"]        = "bool",
                                                                     ["Double"]         = "double",
                                                                     ["Decimal"]        = "decimal",
                                                                     ["DateTime"]       = "DateTime",
                                                                     ["DateTimeOffset"] = "DateTimeOffset",
                                                                     ["TimeSpan"]       = "TimeSpan",
                                                                     ["Guid"]           = "Guid",
                                                                     ["Uri"]            = "Uri",
                                                                     ["Byte[]"]         = "byte[]"
                                                                 };

    private static readonly HashSet<string> ValueTypes = ["int", "long", "bool", "double", "decimal", "DateTime", "DateTimeOffset", "TimeSpan", "Guid"];

    /// <summary>A C# identifier from any name (anything that is not a letter, digit or underscore becomes an underscore).</summary>
    public static string Ident(string name)
    {
        var sb = new StringBuilder();
        foreach (char ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    /// <summary>The draft's enums and classes as C# (a starting point, not something compiled by the Silo).</summary>
    public static string CSharp(SchemaDraft draft)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Sketched from the schema draft \"" + draft.Name.Replace("\"", "'") + "\"");
        sb.AppendLine();

        foreach (var e in draft.Of(ConstructKind.Enum))
        {
            var rec  = SchemaRecords.ToEnum(e);
            string n = Ident(e.Name);
            if (!string.IsNullOrWhiteSpace(rec.Description)) sb.Append("/// <summary>").Append(rec.Description!.Replace("\n", " ")).AppendLine("</summary>");
            switch (rec.Type)
            {
                case EnumType.Integral or EnumType.Signed or EnumType.Byte:
                    string under = rec.Type switch { EnumType.Byte => "byte", EnumType.Signed => "long", _ => "ulong" };
                    if ((rec.Flags & EnumFlags.Bitmask) != 0) sb.AppendLine("[Flags]");
                    sb.Append("public enum ").Append(n).Append(" : ").AppendLine(under).AppendLine("{");
                    foreach (var v in rec.Values) sb.Append("    ").Append(Ident(v.Name)).Append(" = ").Append(v.Value).AppendLine(",");
                    sb.AppendLine("}");
                    break;
                default:
                    string cs = rec.Type switch { EnumType.Float => "double", EnumType.Bool => "bool", EnumType.Guid => "Guid", _ => "string" };
                    sb.Append("public static class ").AppendLine(n).AppendLine("{");
                    foreach (var v in rec.Values)
                    {
                        string literal = rec.Type switch
                                         {
                                             EnumType.String => "\"" + (v.Value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
                                             EnumType.Guid   => $"Guid.Parse(\"{v.Value}\")",
                                             _               => v.Value ?? "default"
                                         };
                        sb.Append("    public ").Append(rec.Type == EnumType.Guid ? "static readonly " : "const ").Append(cs).Append(' ').Append(Ident(v.Name)).Append(" = ").Append(literal).AppendLine(";");
                    }

                    sb.AppendLine("}");
                    break;
            }

            sb.AppendLine();
        }

        foreach (var c in draft.Of(ConstructKind.Class))
        {
            var bases    = SchemaCheck.Bases(draft, c.Name).ToList();
            var includes = SchemaCheck.Includes(draft, c.Name).ToList();
            if (c.Has("description")) sb.Append("/// <summary>").Append(c.Get("description").Replace("\n", " ")).AppendLine("</summary>");
            if (includes.Count > 0) sb.Append("// includes: ").AppendLine(string.Join(", ", includes));
            sb.Append("public ").Append(c.Flag("auxiliary") ? "partial " : string.Empty).Append("class ").Append(Ident(c.Name));
            if (bases.Count > 0) sb.Append(" : ").Append(Ident(bases[0]));
            sb.AppendLine().AppendLine("{");
            foreach (string m in c.Members)
            {
                var attr = draft.Find(ConstructKind.Attribute, m);
                string type = attr?.Get("type", "String") ?? "String";
                string cs = SchemaTypes.EnumOf(type) is { } en ? Ident(en) : CsTypes.GetValueOrDefault(type, "string");
                var flags = attr is null ? MvAttributeFlags.None : SchemaRecords.AttributeFlags(attr);
                if ((flags & MvAttributeFlags.Multivalue) != 0) cs = $"List<{cs}>";
                else if ((flags & MvAttributeFlags.Nullable) != 0) cs += ValueTypes.Contains(cs) || SchemaTypes.EnumOf(type) is not null ? "?" : string.Empty;
                if (attr is not null && attr.Has("syntax")) sb.Append("    // syntax: ").AppendLine(attr.Get("syntax"));
                if ((flags & MvAttributeFlags.Key) != 0) sb.AppendLine("    // primary key");
                if ((flags & MvAttributeFlags.RelativeKey) != 0) sb.AppendLine("    // relative key (name within its container)");
                if ((flags & MvAttributeFlags.AbsoluteKey) != 0) sb.AppendLine("    // absolute key (full path)");
                sb.Append("    public ").Append(cs).Append(' ').Append(Ident(m)).Append(" { get; set; }");
                if ((flags & MvAttributeFlags.Multivalue) != 0) sb.Append(" = [];");
                else if (cs == "string" && (flags & MvAttributeFlags.Nullable) == 0) sb.Append(" = string.Empty;");
                sb.AppendLine();
            }

            sb.AppendLine("}").AppendLine();
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }
}

/// <summary>Drafts to start from.</summary>
public static class SchemaLibrary
{
    public static IReadOnlyList<(string Title, Func<SchemaDraft> Make)> Templates { get; } =
    [
        ("People: a class hierarchy with an enum and a syntax", People),
        ("Orders: a key, an enum and a syntax for codes", Orders),
        ("Units: bytes, kilo and mega", Units),
        ("Directory: a relative and an absolute key", Directory)
    ];

    private static SchemaConstruct Attr(SchemaDraft d, string name, string type, string? flags = null, string? syntax = null, string? display = null)
    {
        var a = d.Add(ConstructKind.Attribute, name);
        a.Set("type", type);
        a.Set("flags", flags);
        a.Set("syntax", syntax);
        a.Set("displayName", display);
        return a;
    }

    private static SchemaDraft People()
    {
        var d = new SchemaDraft { Name = "People", Description = "People and the employees among them." };

        var syntax = d.Add(ConstructKind.Syntax, "EmailAddress");
        syntax.Set("kind",        "Pattern");
        syntax.Set("pattern",     @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
        syntax.Set("example",     "ada@example.com");
        syntax.Set("description", "An address with an at sign and a dot after it.");

        var role = d.Add(ConstructKind.Enum, "Role");
        role.Set("type", "Integral");
        role.Values.AddRange([new SchemaValue { Name = "Staff", Value = "0" }, new SchemaValue { Name = "Manager", Value = "1" }, new SchemaValue { Name = "Admin", Value = "2" }]);

        Attr(d, "Id",       "Int64",    "Key");
        Attr(d, "Name",     "String");
        Attr(d, "Email",    "String",   "Nullable", "EmailAddress");
        Attr(d, "Hired",    "DateTime", "Nullable");
        Attr(d, "Role",     "enum:Role");
        Attr(d, "Modified", "DateTime");

        var person = d.Add(ConstructKind.Class, "Person");
        person.Members.AddRange(["Id", "Name", "Email"]);
        var employee = d.Add(ConstructKind.Class, "Employee");
        employee.Members.AddRange(["Hired", "Role"]);
        var audit = d.Add(ConstructKind.Class, "Auditable");
        audit.Set("auxiliary", "true");
        audit.Members.Add("Modified");

        d.Connect("Employee", "Person",    false);
        d.Connect("Person",   "Auditable", true);
        return d;
    }

    private static SchemaDraft Orders()
    {
        var d = new SchemaDraft { Name = "Orders", Description = "An order with a status and a stock code." };

        var sku = d.Add(ConstructKind.Syntax, "Sku");
        sku.Set("kind",    "Pattern");
        sku.Set("pattern", @"^[A-Z]{3}-\d{4}$");
        sku.Set("example", "ABC-1234");

        var status = d.Add(ConstructKind.Enum, "OrderStatus");
        status.Set("type", "Integral");
        status.Values.AddRange([new SchemaValue { Name = "Draft" }, new SchemaValue { Name = "Placed" }, new SchemaValue { Name = "Shipped" }]);

        Attr(d, "OrderId", "Guid", "Key");
        Attr(d, "Sku",     "String", syntax: "Sku");
        Attr(d, "Total",   "Decimal");
        Attr(d, "Status",  "enum:OrderStatus");

        var order = d.Add(ConstructKind.Class, "Order");
        order.Members.AddRange(["OrderId", "Sku", "Total", "Status"]);
        return d;
    }

    private static SchemaDraft Directory()
    {
        var d = new SchemaDraft { Name = "Directory", Description = "Entries named within their container (cn) and by their full path (distinguishedName)." };

        Attr(d, "objectGUID",        "Guid",   "Key");
        Attr(d, "cn",                "String", "RelativeKey, Indexed", display: "Common name");
        Attr(d, "distinguishedName", "String", "AbsoluteKey, Indexed", display: "Distinguished name");
        Attr(d, "description",       "String", "Nullable");
        Attr(d, "mail",              "String", "Nullable, Anr");

        var entry = d.Add(ConstructKind.Class, "entry");
        entry.Members.AddRange(["objectGUID", "cn", "distinguishedName", "description"]);
        var user = d.Add(ConstructKind.Class, "user");
        user.Members.Add("mail");
        var ou = d.Add(ConstructKind.Class, "organizationalUnit");
        ou.Set("displayName", "Organizational unit");

        d.Connect("user",               "entry", false);
        d.Connect("organizationalUnit", "entry", false);
        return d;
    }

    private static SchemaDraft Units()
    {
        var d = new SchemaDraft { Name = "Units", Description = "Bytes and the usual multiples." };
        d.Target = PromoteTarget.Meta;

        var b = d.Add(ConstructKind.Unit, "B");
        b.Set("type",       "Unit");
        b.Set("namePlural", "bytes");
        b.Set("tags",       "#bin");

        var octet = d.Add(ConstructKind.Unit, "octet");
        octet.Set("type",   "Alias");
        octet.Set("target", "B");

        var kb = d.Add(ConstructKind.Unit, "KB");
        kb.Set("type",   "Prefix");
        kb.Set("target", "B");
        kb.Set("scale",  "1000");

        var mb = d.Add(ConstructKind.Unit, "MB");
        mb.Set("type",    "Conversion");
        mb.Set("target",  "B");
        mb.Set("formula", "? * 1000000");
        mb.Set("scale",   "1000000");

        var kilo = d.Add(ConstructKind.UnitRelation, "KiloChain");
        kilo.Set("unit",  "KB");
        kilo.Set("next",  "MegaChain");
        kilo.Set("scale", "1000");
        var mega = d.Add(ConstructKind.UnitRelation, "MegaChain");
        mega.Set("unit", "MB");
        return d;
    }
}
