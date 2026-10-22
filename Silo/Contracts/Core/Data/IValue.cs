using Silo.Contracts.ConnectorServices;
using Silo.Providers.Data.Shape;

namespace Silo.Contracts.Core.Data;

/// <summary>
///     A value wrapper which provides the metadata needed by Silo to render and respond to data
/// </summary>
public interface IValue
{
    /// <summary>
    ///     Gets a collection of units of measure
    /// </summary>
    IEnumerable<IUnit> MeasureUnits { get; }

    /// <summary>
    ///     Gets an object providing details on the source of this value
    /// </summary>
    IValueSource Source { get; }

    /// <summary>
    ///     Gets a collection of non-quantitative measures
    /// </summary>
    IEnumerable<ITag> TagUnits { get; }

    /// <summary>
    ///     The time the value was captured
    /// </summary>
    DateTime? Time { get; }

    /// <summary>
    ///     The extended type of this value
    /// </summary>
    IType Type { get; }

    /// <summary>
    ///     The underlying value captured
    /// </summary>
    object Value { get; }
}

/// <summary>
///     A possible source of values. used in IValue.Source
/// </summary>
public interface IValueSource : IDisplayName
{
}

/// <summary>
///     A quantitative unit of measure, such as "Ounces"
/// </summary>
public interface IUnit : IDisplayName
{
    string Abbreviation { get; }
    object FormatValue(object vakue);
}

/// <summary>
///     A qualitative unit or marker of origin, such as "Prod"
/// </summary>
public interface ITag : IDisplayName
{
    string Abbreviation { get; }
    object FormatValue(object vakue);
}