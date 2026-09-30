using Silo.Contracts;
using Silo.Extensions;
using Silo.Parsing;

namespace Silo.DbModel.Meta;

/*
 *  Unit:  [B, byte|bytes] #bin .#si
 *  Unit:  [s, sec|secs, second|seconds] #si
 *  Unit:  [$?, USD, dollar|dollars] #si
 *  Mod:   [K?, Kilo?] = (? * 1000) #si
 *
 * Alias: Bps = B/s
 *
 */

public record Unit(
    int           Id,
    UnitType      Type,
    Unit?         Target,
    string        Name,
    string        NamePlural,
    string        Formula,
    UnitRelation? Relation,
    string        Formatter,
    double?       Scale,
    string?       Tags,
    bool          Reciprocal) : IValidatable
{
    public void Validate()
    {
        this.Assert(Name.There(), "Unit.Name is required and was null");
        switch (Type)
        {
            case UnitType.Alias:
                this.Assert(Relation is null, "Unit.Relation must be null for Alias definitions.");
                this.Assert(Name.There(),     "Unit.Name is required and was null");
                this.Assert(Target != null,   "Unit.Target is required for Alias definitions, but it is null.");
                ;
                break;
            case UnitType.Unit:
                this.Assert(Relation is null, "Unit.Relation must be null for Unit definitions.");
                break;
            case UnitType.Conversion:
                this.Assert(Relation != null || Target != null,
                            "Unit.Target or Unit.Relation must be defined for" + " Prefixes, Suffixes, and Conversions");
                this.Assert(Formula.There(), "Unit.Formula is required for conversions and was null");
                break;
            case UnitType.Prefix:
            case UnitType.Suffix:
                this.Assert(Relation != null || Target != null,
                            "Unit.Target or Unit.Relation must be defined for" + " Prefixes, Suffixes, and Conversions");

                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }
}

public enum UnitType
{
    Alias,
    Conversion,
    Unit,
    Prefix,
    Suffix,
}

public record UnitRelation(int Id, Unit? Unit, string Name, double? Scale, bool Reciprocal, UnitRelation? Next);

//public record UnitTag(int Id, Unit Unit, string Tag);