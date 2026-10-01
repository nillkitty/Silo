using System.IO;

namespace Silo.Contracts;

/// <summary>
///     An object which can read one or more documents of type <typeparamref name="TContent" />
///     out of a single file stream.
/// </summary>
/// <typeparam name="TContent"></typeparam>
/// <remarks>
///     A stream can hold more than one document -- NDJSON-style concatenated JSON values, or
///     YAML's own native <c>---</c>-separated documents -- so <see cref="Read" /> always yields a
///     sequence rather than a single <typeparamref name="TContent" />. A format with no
///     multi-document convention of its own (XML has none) still satisfies this contract by
///     yielding exactly one item. The sequence is lazily produced (see each implementation): it's
///     only fully read once enumerated, and only as much of <paramref name="stream" /> is
///     consumed as the caller actually enumerates.
/// </remarks>
public interface IDocumentProvider<out TContent> : IDataProvider
{
    IEnumerable<TContent> Read(FileStream stream);
}

public interface IDocument<TContent>
{
}