using System.IO;
using Markdig;
using MarkdigDocument = Markdig.Syntax.MarkdownDocument;

namespace Silo.Providers.Format;

/// <summary>
///     Reads Markdown files into Markdig's own <see cref="MarkdigDocument" /> AST. Table and list
///     projection onto <see cref="Silo.Contracts.IFlatDataProvider" /> happens separately, in
///     <c>MarkdownDataProvider</c>, which walks this AST rather than re-parsing the file.
/// </summary>
/// <remarks>
///     <see cref="MarkdigDocument" /> is aliased on import (rather than referenced via its full
///     name everywhere) to avoid a repeat of the <c>Silo.DbModel.World</c> self-collision: without
///     the alias, a type named <c>MarkdownDocument</c> declared in this very namespace (see below)
///     would shadow Markdig's own type of the same simple name.
/// </remarks>
public class MarkdownFormat : IFileFormat, IDocumentProvider<MarkdigDocument>
{
    /// <summary>
    ///     Only pipe tables are opted in -- just enough extended syntax to recognize the table
    ///     shape <c>MarkdownDataProvider</c> projects onto <see cref="Silo.Contracts.ITabularDataSource" />.
    ///     Bulleted/numbered lists (<c>Markdig.Syntax.ListBlock</c>) are part of Markdig's
    ///     CommonMark-compliant core and need no extension to parse.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .Build();

    public string DisplayName   { get; } = "Markdown File";
    public string FileExtension { get; } = "md";

    /// <summary>
    ///     Markdown has no native multi-document convention (unlike YAML's <c>---</c> separators),
    ///     so this always yields exactly one parsed document.
    /// </summary>
    public IEnumerable<MarkdigDocument> Read(FileStream stream)
    {
        stream.Required();

        using var reader = new StreamReader(stream);
        var       text   = reader.ReadToEnd();

        yield return Markdig.Markdown.Parse(text, Pipeline);
    }
}

/// <summary>
///     A Markdown document
/// </summary>
public class MarkdownDocument(MarkdownFormat provider, Uri uri) : DocumentBase<MarkdigDocument>(provider, uri);
