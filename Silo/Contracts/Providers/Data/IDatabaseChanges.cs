namespace Silo.Providers.Data;

/// <summary>
///     Implemented by a provider that can tell its owner the document changed, so the owner can mark it as having unsaved
///     changes (see <c>OpenSilo.IsDirty</c>).
/// </summary>
/// <remarks>
///     Raised on whichever thread made the change, possibly while the provider holds its internal lock: handlers must be
///     quick and must not call back into the provider (marshal to the UI thread with <c>BeginInvoke</c> instead).
/// </remarks>
public interface IDatabaseChanges
{
    /// <summary>Raised after items were added, changed or removed.</summary>
    event EventHandler? Changed;
}