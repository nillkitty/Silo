using Serilog;
using Silo.Providers.Data;
using Telefrag.DI;

namespace Silo.Labs.Schemas;

public static class SchemaLabParameters
{
    public const string ShowMembers = "showMembers";
    public const string MemberLines = "memberLines";
    public const string Confirm     = "confirm";
}

/// <summary>What the active Silo can do for schema constructs: the stores it has, and what it already holds.</summary>
internal static class SchemaSilo
{
    private static object? Data => App.Instance?.ActiveSilo?.Data;

    public static IEnumStore?   Enums    => Data as IEnumStore;
    public static ISyntaxStore? Syntaxes => Data as ISyntaxStore;
    public static ISchemaStore? Schema   => Data as ISchemaStore;

    public static bool IsOpen => Data is not null;

    public static SchemaSupport Support => new(Enums is not null, Syntaxes is not null, Schema is not null);

    /// <summary>What the Silo holds that the checks and promotion compare with (empty where the Silo has no store).</summary>
    public static SchemaSnapshot Snapshot()
    {
        try
        {
            var   schema = Schema?.GetSnapshot() ?? SchemaSnapshot.Empty;
            return new SchemaSnapshot
                   {
                       Enums            = Enums?.GetEnums() ?? [],
                       Syntaxes         = Syntaxes?.GetSyntaxes() ?? [],
                       Classes          = schema.Classes,
                       AuxiliaryClasses = schema.AuxiliaryClasses,
                       Attributes       = schema.Attributes,
                       Hierarchy        = schema.Hierarchy,
                       LocalWorldTypes  = schema.LocalWorldTypes,
                       Units            = schema.Units,
                       UnitRelations    = schema.UnitRelations
                   };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading what the Silo holds for the Schema Lab failed");
            return SchemaSnapshot.Empty;
        }
    }
}

/// <summary>
///     Drafts the Silo's schema: classes with their attributes, enums, syntaxes, type relations and units, drawn as a diagram and edited as properties,
///     checked with the Silo's own rules, and promoted to the metaverse or the local world. The draft is scratch until it is promoted.
/// </summary>
[Singleton(typeof(ILab))]
public class SchemaLab : LabBase<SchemaDraft>
{
    public override string Description => "Draft classes, attributes, enums, syntaxes, type relations and units as a diagram with a property editor. The draft is checked with the Silo's own rules " +
                                          "and promoted to the metaverse or the local world; an enum or syntax of the Silo can be pulled in to edit a copy.";

    public override string IconKey => "HierarchyStructure";

    public override string Id => "schema";

    public override string Name => "Schema Lab";

    public override int Order => 60;

    protected override IEnumerable<LabParameter> DefineParameters()
    {
        yield return new LabParameter(SchemaLabParameters.ShowMembers, "List attributes inside each class", LabParameterKind.Bool, true,
                                      "Show the attributes of a class in its box, with # marking the key.", "Diagram");
        yield return new LabParameter(SchemaLabParameters.MemberLines, "Draw a line from a class to each attribute", LabParameterKind.Bool, false,
                                      "Adds lines from classes to their attributes; busy for large drafts.", "Diagram");
        yield return new LabParameter(SchemaLabParameters.Confirm, "Ask before promoting", LabParameterKind.Bool, true,
                                      "Show what will be written, and ask, before anything goes into the Silo.", "Promotion");
    }

    public override LabSession<SchemaDraft> CreateSession()
    {
        return new SchemaLabSession(this);
    }

    internal bool ShowMembers => Parameters[SchemaLabParameters.ShowMembers].Bool;

    internal bool MemberLines => Parameters[SchemaLabParameters.MemberLines].Bool;

    internal bool Confirm => Parameters[SchemaLabParameters.Confirm].Bool;

    protected override bool TryConvert(object? candidate, out SchemaDraft? value)
    {
        switch (candidate)
        {
            case SchemaDraft draft:
                value = draft;
                return true;
            // an enum or a syntax of the Silo (from its list): a draft with it, to edit a copy
            case EnumRecord e:
                value = new SchemaDraft { Name = e.Name };
                value.Items.Add(SchemaRecords.FromEnum(e));
                return true;
            case SyntaxRecord s:
                value = new SchemaDraft { Name = s.Name };
                value.Items.Add(SchemaRecords.FromSyntax(s));
                return true;
            // asked of every node when a draft is built: only the schema:Name way of referring to a kept draft is taken
            case string text when text.StartsWith("schema:", StringComparison.OrdinalIgnoreCase) && text.Length > 7:
                value = SchemaLabSession.Kept.Load(text[7..].Trim());
                return value is not null;
            default:
                value = null;
                return false;
        }
    }

    /// <summary>A text is the draft's JSON, or the name of a kept draft.</summary>
    public override bool TryParse(string text, out SchemaDraft? value)
    {
        if (text.TrimStart().StartsWith('{') && SchemaJson.TryFromJson(text, out var draft, out _))
        {
            value = draft;
            return true;
        }

        value = SchemaLabSession.Kept.Load(text.Trim());
        return value is not null;
    }
}
