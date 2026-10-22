using Silo.Contracts.ConnectorServices;

namespace Silo.Providers.Data.Shape;

/// <summary>
///     Represents a syntax;  rules for validating a string and converting it to a usable runtime object value
/// </summary>
public interface ISyntax : IDisplayName
{
    /// <summary>
    ///     Gets whether the supplied string value represents a valid value within this Syntax
    /// </summary>
    bool IsValid(string value);

    /// <summary>
    ///     Gets the storage string for a given input value
    /// </summary>
    string GetString(object value);

    /// <summary>
    ///     Gets the decoded output object for a given storage string
    /// </summary>
    object GetValue(string value);
}