using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Silo.Providers.Format;

public class JsonFormat : IFileFormat, IDocumentProvider<JToken>
{
    public JsonLoadSettings? Settings { get; set; } = new()
                                                      {
                                                          CommentHandling               = CommentHandling.Load,
                                                          DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace,
                                                          LineInfoHandling              = LineInfoHandling.Load
                                                      };

    public string DisplayName   { get; } = "JSON File";
    public string FileExtension { get; } = "json";

    /// <summary>
    ///     Reads every JSON value out of <paramref name="stream" />, in order. An ordinary JSON
    ///     file holds exactly one; an NDJSON-style file (back-to-back values, with or without
    ///     newlines between them) holds more than one -- which
    ///     <see cref="JsonTextReader.SupportMultipleContent" /> is what makes readable at all:
    ///     without it, Newtonsoft throws as soon as it hits trailing content after the first
    ///     value. Lazily evaluated: nothing is parsed until (and only as far as) the result is
    ///     enumerated.
    /// </summary>
    public IEnumerable<JToken> Read(FileStream stream)
    {
        stream.Required();
        var reader = new JsonTextReader(new StreamReader(stream)) { SupportMultipleContent = true };
        while (reader.Read())
            yield return JToken.ReadFrom(reader, Settings);
    }
}

/// <summary>
///     Represents a JSON document
/// </summary>
public class JsonDocument(JsonFormat provider, Uri uri) : DocumentBase<JToken>(provider, uri);
