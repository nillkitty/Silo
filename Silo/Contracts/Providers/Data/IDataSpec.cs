using System.IO;

namespace Silo.Contracts.Providers.Data;

/// <summary>
///     Defines a data specification
/// </summary>
public interface IDataSpec
{
    /// <summary>
    ///     Gets whether the data specification matches the Stream,
    ///     at the current stream position.
    /// </summary>
    /// <returns>
    ///     Returns true if successful,
    ///     false if no match, and
    ///     null if indeterminate (e.g. not enough data)
    /// </returns>
    bool? MatchesData(Stream stream);
}

public interface IFormatProvider
{
}