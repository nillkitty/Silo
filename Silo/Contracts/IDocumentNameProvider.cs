namespace Silo.Contracts;

/// <summary>
///     Provides name and namespace for documents.
/// </summary>
public interface IDocumentNameProvider
{
    /// <summary>
    ///     Resolves a document name (or not)
    /// </summary>
    (string nativeNs, string localNs, string canonicalName)? ResolveDocumentName(Uri documentUri, object document);
}