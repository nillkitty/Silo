namespace Silo.Providers.Tree;

/// <summary>
///     An object which controls the logistical aspects of managing a hierarchy.
/// </summary>
public interface ITreeProvider<TModel>
{
    /// <summary>
    ///     Gets the model which describes the root node of the tree which the
    ///     Uri references (if any).
    /// </summary>
    /// <param name="uri">
    ///     A Uri, partial Uri, or Uri scheme that influences
    ///     which tree root the provider might return.
    /// </param>
    /// <returns>
    ///     Returns a tree root model, or null if one does not exist that
    ///     logically contains or matches <paramref name="uri" />
    /// </returns>
    TModel? GetRoot(Uri uri);

    /// <summary>
    ///     Returns the parent node of the supplied node,
    ///     or null if the supplied node is the root node or
    ///     is disconnected.
    /// </summary>
    TModel? GetParent(TModel node);

    /// <summary>
    ///     Enumerates the supplied node's immediate child nodes (if any)
    /// </summary>
    /// <param name="node"></param>
    /// <returns></returns>
    IEnumerable<TModel?> GetChildren(TModel node);
}