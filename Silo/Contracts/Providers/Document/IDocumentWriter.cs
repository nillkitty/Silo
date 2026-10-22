using System.IO;
using Silo.Providers.Reflection;

namespace Silo.Providers.Document;

/// <summary>
///     The other half of <see cref="IDocumentProvider{TContent}" />: a format that can write documents of type
///     <typeparamref name="TContent" /> to a
///     stream. A format that can do both is one the application opens and saves: what is read in one can be written out in
///     another, which is all a
///     conversion is.
/// </summary>
/// <typeparam name="TContent">The kind of document the format holds.</typeparam>
/// <remarks>
///     It is a file format (<see cref="IFileFormat" />), so it has a content type and extensions, and it can be asked for
///     by extension from the
///     <c>Formats</c> catalog. It is not an <see cref="IDocumentProvider{TContent}" /> because writing needs nothing of
///     the data-provider contract
///     (verbs, nodes) and many formats can be written that cannot be read back.
/// </remarks>
public interface IDocumentWriter<TContent> : IFileFormat
{
    /// <summary>
    ///     Writes the documents to <paramref name="stream" />, in order, from its current position. A format that holds one
    ///     document per file
    ///     (most do) writes them one after the other as if they were one, or says otherwise in its own remarks.
    /// </summary>
    /// <param name="stream">Where to write; it is left open.</param>
    /// <param name="documents">The documents.</param>
    void Write(Stream stream, IEnumerable<TContent> documents);
}