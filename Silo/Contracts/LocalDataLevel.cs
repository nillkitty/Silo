namespace Silo.Connectors;

/// <summary>
/// Values indicating to what degree data from connected sources is stored locally.
/// </summary>
public enum LocalDataLevel
{
    /// <summary>
    /// No data for the targeted scope is stored locally
    /// </summary>
    None,

    /// <summary>
    /// A cache of the most recent data being worked with is stored locally
    /// </summary>
    Cache,

    /// <summary>
    /// A complete copy of the data is made before it is available for processing.
    /// </summary>
    Shadow
}