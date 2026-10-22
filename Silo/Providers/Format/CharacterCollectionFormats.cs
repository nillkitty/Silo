using System.IO;
using Newtonsoft.Json.Linq;
using Silo.Providers.Data.Source;
using Silo.Providers.Document;
using Silo.Providers.Reflection;
using Silo.Unicode;
using Telefrag.DI;
using YamlDotNet.Core;

namespace Silo.Providers.Format;

/// <summary>
///     A document of the Unicode Explorer: the characters of a collection, in order, with the person's labels. The
///     optional parts say how a writer
///     that shows them should (a title for a page, the mark colors).
/// </summary>
/// <param name="Items">The characters.</param>
/// <param name="Title">What the collection is called, for a format that has a title (the HTML gallery).</param>
/// <param name="ColorHex">The mark color of a code point as <c>#RRGGBB</c>; null for none.</param>
/// <param name="ColorMode">How a format that shows colors shows them.</param>
public sealed record CharacterCollection(
    IReadOnlyList<CollectedCharacter> Items,
    string?                           Title     = null,
    Func<int, string?>?               ColorHex  = null,
    ColorMode                         ColorMode = ColorMode.Glyph);

/// <summary>
///     The base of the formats a <see cref="CharacterCollection" /> is kept in. Each is a file format that reads and
///     writes text (so the same file can
///     be opened in the Unicode Explorer, saved from it, or converted to another of them); a format that cannot be read
///     back says so in
///     <see cref="CanRead" />.
/// </summary>
public abstract class CharacterCollectionFormat : IFileFormat, IDocumentProvider<CharacterCollection>, IDocumentWriter<CharacterCollection>
{
    /// <summary>A short, stable name for the format (<c>json</c>), for code that asks for one in particular.</summary>
    public abstract string Id { get; }

    /// <summary>Whether a file in this format can be read back into a collection.</summary>
    public virtual bool CanRead => true;

    protected static UnicodeDatabase Db => UnicodeDatabase.Default;

    /// <summary>
    ///     Whether the file starts with a byte-order mark (Excel and Notepad need it to see UTF-8; JSON, XML and YAML do
    ///     not want one).
    /// </summary>
    protected virtual bool WriteBom => false;

    public abstract string DisplayName   { get; }
    public abstract string FileExtension { get; }

    public abstract string ContentType { get; }

    /// <summary>The collection as the format's text.</summary>
    public abstract string WriteText(CharacterCollection collection);

    /// <summary>The characters of text in the format.</summary>
    /// <exception cref="FormatException">The text is not in the format.</exception>
    public abstract IReadOnlyList<CollectedCharacter> ReadText(string text);

    public IEnumerable<DataVerb> GetSupportedVerbs()
    {
        return [];
    }

    /// <inheritdoc />
    public IEnumerable<CharacterCollection> Read(FileStream stream)
    {
        stream.Required();
        if (!CanRead) throw new NotSupportedException($"{DisplayName} can be written but not read back.");

        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true);
        yield return new CharacterCollection(ReadText(reader.ReadToEnd()));
    }

    /// <summary>Writes every collection, one after the other, as a single one: these formats hold one collection per file.</summary>
    public void Write(Stream stream, IEnumerable<CharacterCollection> documents)
    {
        stream.Required();
        var all = documents.ToList();
        var merged = new CharacterCollection(all.SelectMany(d => d.Items).ToList(),
                                             all.Select(d => d.Title).FirstOrDefault(t => t is not null),
                                             all.Select(d => d.ColorHex).FirstOrDefault(c => c is not null),
                                             all.Count > 0 ? all[0].ColorMode : ColorMode.Glyph);
        byte[] bytes = new UTF8Encoding(WriteBom).GetBytes(WriteText(merged));
        if (WriteBom) stream.Write(Encoding.UTF8.GetPreamble());
        stream.Write(bytes);
    }
}

[Singleton(typeof(IDocumentWriter<CharacterCollection>))]
public sealed class TextCollectionFormat : CharacterCollectionFormat
{
    public override    string ContentType   => "text/plain";
    public override    string DisplayName   => "Characters (text)";
    public override    string FileExtension => "txt";
    public override    string Id            => "text";
    protected override bool   WriteBom      => true;

    public override string WriteText(CharacterCollection c)
    {
        return CollectionFormats.Write(c.Items, CollectionFormat.Text, Db);
    }

    public override IReadOnlyList<CollectedCharacter> ReadText(string text)
    {
        return CollectionFormats.Read(text);
        // the text itself, or whatever it turns out to be
    }
}

[Singleton(typeof(IDocumentWriter<CharacterCollection>))]
public sealed class CodePointListCollectionFormat : CharacterCollectionFormat
{
    public override    string ContentType   => "text/plain";
    public override    string DisplayName   => "Code point list";
    public override    string FileExtension => "txt";
    public override    string Id            => "codepoints";
    protected override bool   WriteBom      => true;

    public override string WriteText(CharacterCollection c)
    {
        return CollectionFormats.Write(c.Items, CollectionFormat.CodePointList, Db);
    }

    public override IReadOnlyList<CollectedCharacter> ReadText(string text)
    {
        return CollectionFormats.Read(text, CollectionFormat.CodePointList);
    }
}

[Singleton(typeof(IDocumentWriter<CharacterCollection>))]
public sealed class JsonCollectionFormat : CharacterCollectionFormat
{
    public override string ContentType   => "application/json";
    public override string DisplayName   => "Character collection (JSON)";
    public override string FileExtension => "json";
    public override string Id            => "json";

    public override string WriteText(CharacterCollection c)
    {
        return CollectionFormats.Write(c.Items, CollectionFormat.Json, Db);
    }

    public override IReadOnlyList<CollectedCharacter> ReadText(string text)
    {
        return CollectionFormats.Read(text, CollectionFormat.Json);
    }
}

[Singleton(typeof(IDocumentWriter<CharacterCollection>))]
public sealed class TsvCollectionFormat : CharacterCollectionFormat
{
    public override    string ContentType   => "text/tab-separated-values";
    public override    string DisplayName   => "Table (tab-separated)";
    public override    string FileExtension => "tsv";
    public override    string Id            => "tsv";
    protected override bool   WriteBom      => true;

    public override string WriteText(CharacterCollection c)
    {
        return CollectionFormats.Write(c.Items, CollectionFormat.Tsv, Db);
    }

    public override IReadOnlyList<CollectedCharacter> ReadText(string text)
    {
        return CollectionFormats.Read(text, CollectionFormat.Tsv);
    }
}

[Singleton(typeof(IDocumentWriter<CharacterCollection>))]
public sealed class CsvCollectionFormat : CharacterCollectionFormat
{
    public override    string ContentType   => "text/csv";
    public override    string DisplayName   => "Table (comma-separated)";
    public override    string FileExtension => "csv";
    public override    string Id            => "csv";
    protected override bool   WriteBom      => true;

    public override string WriteText(CharacterCollection c)
    {
        return CollectionFormats.Write(c.Items, CollectionFormat.Csv, Db);
    }

    public override IReadOnlyList<CollectedCharacter> ReadText(string text)
    {
        return CollectionFormats.Read(text, CollectionFormat.Csv);
    }
}

[Singleton(typeof(IDocumentWriter<CharacterCollection>))]
public sealed class XmlCollectionFormat : CharacterCollectionFormat
{
    public override string ContentType   => "application/xml";
    public override string DisplayName   => "Character collection (XML)";
    public override string FileExtension => "xml";
    public override string Id            => "xml";

    public override string WriteText(CharacterCollection c)
    {
        return CollectionExports.WriteXml(c.Items, Db);
    }

    public override IReadOnlyList<CollectedCharacter> ReadText(string text)
    {
        return CollectionExports.ReadXml(text);
    }
}

/// <summary>
///     YAML. Written by <see cref="CollectionExports.WriteYaml" />; read with the application's own YAML reader (
///     <see cref="YamlDocumentParser" />,
///     through <see cref="YamlJson" />), so any YAML that holds the same list opens, not only what was written here.
/// </summary>
[Singleton(typeof(IDocumentWriter<CharacterCollection>))]
public sealed class YamlCollectionFormat : CharacterCollectionFormat
{
    public override string ContentType   => "application/yaml";
    public override string DisplayName   => "Character collection (YAML)";
    public override string FileExtension => "yaml,yml";
    public override string Id            => "yaml";

    public override string WriteText(CharacterCollection c)
    {
        return CollectionExports.WriteYaml(c.Items, Db);
    }

    public override IReadOnlyList<CollectedCharacter> ReadText(string text)
    {
        JToken json;
        try
        {
            var parser = new Parser(new Scanner(new StringReader(text.TrimStart('﻿')), false));
            json = YamlJson.Convert(new YamlDocumentParser(parser).ReadDocuments().ToList());
        }
        catch (YamlException ex)
        {
            throw new FormatException("The file is not valid YAML: " + ex.Message, ex);
        }

        return CollectionFormats.FromJson(json);
    }
}

/// <summary>A web page with a card per character. It cannot be read back.</summary>
[Singleton(typeof(IDocumentWriter<CharacterCollection>))]
public sealed class HtmlGalleryCollectionFormat : CharacterCollectionFormat
{
    public override bool   CanRead       => false;
    public override string ContentType   => "text/html";
    public override string DisplayName   => "HTML gallery";
    public override string FileExtension => "html,htm";
    public override string Id            => "html";

    public override string WriteText(CharacterCollection c)
    {
        return CollectionExports.WriteHtmlGallery(c.Items, c.Title ?? "Character collection", c.ColorHex, c.ColorMode, Db);
    }

    public override IReadOnlyList<CollectedCharacter> ReadText(string text)
    {
        throw new NotSupportedException("An HTML gallery cannot be read back.");
    }
}