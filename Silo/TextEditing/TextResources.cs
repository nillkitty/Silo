using Newtonsoft.Json;
using Silo.Connectors;
using Silo.Model;
using Silo.Storage;

namespace Silo.TextEditing;

/// <summary>A text as it was read: the characters, and how they were written (so saving writes them back the same way).</summary>
/// <param name="Text">The text.</param>
/// <param name="Encoding">The character encoding of the file; UTF-8 for text that is not a file.</param>
/// <param name="HasBom">Whether the file started with a byte-order mark.</param>
/// <param name="NewLine">The line end the text mostly uses ("\r\n", "\n" or "\r").</param>
/// <param name="Language">The language the source says the text is in; null to work it out.</param>
public sealed record TextContent(string Text, Encoding Encoding, bool HasBom, string NewLine, TextLanguage? Language = null)
{
    /// <summary>What to call the encoding in the status line.</summary>
    public string EncodingName => Encoding switch
                                  {
                                      UTF8Encoding      => HasBom ? "UTF-8 with BOM" : "UTF-8",
                                      UnicodeEncoding u => u.GetPreamble() is [0xFE, 0xFF, ..] ? "UTF-16 BE" : "UTF-16 LE",
                                      UTF32Encoding     => "UTF-32",
                                      _                 => Encoding.WebName.ToUpperInvariant()
                                  };

    public string NewLineName => NewLine switch { "\n" => "LF", "\r" => "CR", _ => "CRLF" };

    public static TextContent Of(string text, TextLanguage? language = null)
    {
        return new TextContent(text, new UTF8Encoding(false), false, NewLineOf(text), language);
    }

    /// <summary>The line end used first in the text ("\r\n" when there is none: the Windows default).</summary>
    public static string NewLineOf(string text)
    {
        int i = text.IndexOfAny(['\r', '\n']);
        if (i       < 0) return "\r\n";
        if (text[i] == '\n') return "\n";
        return i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : "\r";
    }
}

/// <summary>
///     Something a text editor can open: a file, a value kept in the Silo, a web response, a new document. It reads the
///     text and,
///     if it can, writes it back.
/// </summary>
public abstract class TextResource
{
    /// <summary>Identifies the document tab: opening the same resource again brings its tab forward.</summary>
    public abstract string Key { get; }

    /// <summary>Where it comes from, in words (a path, an address, "Saved in the Silo").</summary>
    public abstract string Location { get; }

    /// <summary>What the tab is called.</summary>
    public abstract string Title { get; }

    /// <summary>Whether <see cref="Save" /> can write the text back where it came from. When not, saving asks where to put it.</summary>
    public virtual bool CanSave => true;

    /// <summary>Whether what was read has changed since (a file written by another program); asked before saving over it.</summary>
    public virtual bool ChangedSinceLoad => false;

    /// <summary>The media type it came with (a web response's Content-Type); null when there is none.</summary>
    public virtual string? ContentType => null;

    /// <summary>A name to work out the language from (a file name); null when there is none.</summary>
    public virtual string? FileName => null;

    /// <summary>
    ///     The Silo the text is kept in; null for a file, a web response or a new document. Its tab closes with that
    ///     Silo.
    /// </summary>
    public virtual OpenSilo? Silo => null;

    /// <summary>Reads the text. May be called off the UI thread.</summary>
    public abstract Task<TextContent> LoadAsync(CancellationToken cancel = default);

    /// <summary>
    ///     Writes the text back (on the UI thread: a save is asked for by the person, and must finish before a tab or the
    ///     window closes). Throws with a message for the person when it cannot.
    /// </summary>
    public virtual void Save(TextContent content)
    {
        throw new InvalidOperationException("This text cannot be saved where it came from.");
    }
}

/// <summary>A text file on disk (or a network share).</summary>
public sealed class FileTextResource(string path) : TextResource
{
    /// <summary>The largest file opened without asking first.</summary>
    public const long LargeFile = 50L * 1024 * 1024;

    private DateTime? _loadedWriteTime;

    public override bool ChangedSinceLoad
    {
        get
        {
            try
            {
                return _loadedWriteTime is { } loaded && File.Exists(Path) && File.GetLastWriteTimeUtc(Path) != loaded;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    public override string? FileName => Path;

    public override string Key      => "text:file:" + Path.ToUpperInvariant();
    public override string Location => Path;

    public          string Path  { get; } = System.IO.Path.GetFullPath(path);
    public override string Title => System.IO.Path.GetFileName(Path);

    public override async Task<TextContent> LoadAsync(CancellationToken cancel = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path, cancel);
        _loadedWriteTime = File.GetLastWriteTimeUtc(Path);
        return TextFiles.Decode(bytes);
    }

    public override void Save(TextContent content)
    {
        TextFiles.Write(Path, content);
        _loadedWriteTime = File.GetLastWriteTimeUtc(Path);
    }
}

/// <summary>Reading and writing text files, keeping their encoding.</summary>
public static class TextFiles
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    ///     The text of a file's bytes: the encoding its byte-order mark names, else UTF-8 when the bytes are valid UTF-8, else
    ///     Latin-1 (every byte is a character). Throws when the bytes do not look like text at all.
    /// </summary>
    public static TextContent Decode(byte[] bytes)
    {
        (Encoding encoding, int skip) = bytes switch
                                        {
                                            [0xEF, 0xBB, 0xBF, ..]       => (new UTF8Encoding(true), 3),
                                            [0xFF, 0xFE, 0x00, 0x00, ..] => (new UTF32Encoding(false, true), 4),
                                            [0xFF, 0xFE, ..]             => (new UnicodeEncoding(false, true), 2),
                                            [0xFE, 0xFF, ..]             => (new UnicodeEncoding(true,  true), 2),
                                            _                            => ((Encoding)new UTF8Encoding(false), 0)
                                        };

        string text;
        if (skip == 0)
        {
            int probe = Math.Min(bytes.Length, 8192);
            for (int i = 0; i < probe; i++)
                if (bytes[i] == 0)
                    throw new
                        InvalidDataException("This file does not look like text (it has NUL bytes): it cannot be opened in the text editor.");

            try
            {
                text = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                encoding = Encoding.Latin1;
                text     = encoding.GetString(bytes);
            }
        }
        else
        {
            text = encoding.GetString(bytes, skip, bytes.Length - skip);
        }

        return new TextContent(text, encoding, skip > 0, TextContent.NewLineOf(text));
    }

    /// <summary>
    ///     Writes the text in its encoding (with its byte-order mark when it had one), through a temporary file so a
    ///     failure leaves the old file whole.
    /// </summary>
    public static void Write(string path, TextContent content)
    {
        Encoding encoding = content.Encoding switch
                            {
                                UTF8Encoding => new UTF8Encoding(content.HasBom),
                                _            => content.Encoding
                            };

        byte[] preamble = content.HasBom || content.Encoding is UnicodeEncoding or UTF32Encoding ? encoding.GetPreamble() : [];
        byte[] body     = encoding.GetBytes(content.Text);

        string folder = Path.GetDirectoryName(path) ?? ".";
        string temp   = Path.Combine(folder, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(preamble);
                stream.Write(body);
            }

            if (File.Exists(path))
            {
                // keep the attributes of the file being replaced (a read-only file is refused rather than overwritten)
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    throw new UnauthorizedAccessException($"{Path.GetFileName(path)} is read-only.");
                File.Move(temp, path, true);
                File.SetAttributes(path, attributes & ~FileAttributes.Temporary);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException)
            {
                // the temporary file is left behind; it is hidden by its leading dot only on other systems, but harmless
            }
        }
    }
}

/// <summary>
///     A value kept in a Silo's <c>Stored</c> table. Its text is JSON; a stored string is shown as the string itself (and
///     stored
///     back as JSON), and a <see cref="SiloTextDocument" /> as the document's text in its language.
/// </summary>
public sealed class StoredTextResource(IStoredStore store, string key, StoredScope scope, OpenSilo? silo) : TextResource
{
    private static readonly string            StringTypeName = StoredCodec.TypeNameOf(typeof(string));
    private                 SiloTextDocument? _document;

    private         StoredRecord? _record;
    public override bool          CanSave => silo?.IsWritable ?? true;

    public override string Key => $"text:stored:{TextEditors.SiloKey(silo)}:{scope}:{key.ToUpperInvariant()}";

    /// <summary>The language the editor ended up with, kept with a text document so it opens in it again.</summary>
    public TextLanguage? Language { get; set; }

    public override string Location =>
        $"Saved in {silo?.Name ?? "the Silo"} ({(scope == StoredScope.Personal ? "yours" : "the Silo's")}): {key}";

    public          StoredScope Scope => scope;
    public override OpenSilo?   Silo  => silo;

    public string StoredKey => key;

    public override string Title => key.StartsWith(SiloTextDocument.KeyPrefix, StringComparison.OrdinalIgnoreCase)
                                        ? key[SiloTextDocument.KeyPrefix.Length..]
                                        : key;

    public override Task<TextContent> LoadAsync(CancellationToken cancel = default)
    {
        var record = store.GetStored(key, scope) ?? throw new InvalidOperationException($"Nothing is stored as \"{key}\" any more.");
        _record = record;

        if (record.TypeName == SiloTextDocument.TypeName)
        {
            _document = StoredCodec.Deserialize<SiloTextDocument>(record.Data) ?? new SiloTextDocument();
            return Task.FromResult(TextContent.Of(_document.Text ?? string.Empty, TextLanguageNames.Parse(_document.Language)));
        }

        if (IsString(record))
            return Task.FromResult(TextContent.Of(StoredCodec.Deserialize<string>(record.Data) ?? string.Empty));

        return Task.FromResult(TextContent.Of(record.Data, record.Format == StoredCodec.Json ? TextLanguage.Json : null));
    }

    public override void Save(TextContent content)
    {
        var    record = _record ?? store.GetStored(key, scope);
        string typeName, format, data;

        if (_document is not null || record?.TypeName == SiloTextDocument.TypeName)
        {
            var doc = _document ?? new SiloTextDocument();
            doc.Name     = Title;
            doc.Text     = content.Text;
            doc.Language = (Language ?? TextLanguage.PlainText).ToString();
            typeName     = SiloTextDocument.TypeName;
            format       = StoredCodec.Json;
            data         = StoredCodec.Serialize(doc);
            _document    = doc;
        }
        else if (record is not null && IsString(record))
        {
            (typeName, format, data) = (record.TypeName, StoredCodec.Json, StoredCodec.Serialize(content.Text));
        }
        else
        {
            // stored as JSON: it must still be JSON, or whoever reads it would fail
            if ((record?.Format ?? StoredCodec.Json) == StoredCodec.Json)
                try
                {
                    using var reader = new JsonTextReader(new StringReader(content.Text));
                    while (reader.Read())
                    {
                    }
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("This value is kept as JSON, and the text is not valid JSON: " + ex.Message, ex);
                }

            (typeName, format, data) = (record?.TypeName ?? StringTypeName, record?.Format ?? StoredCodec.Json, content.Text);
        }

        _record = store.SetStored(key, typeName, format, data, scope);
        Stored.RaiseChanged(key, scope, false);
    }

    /// <summary>A new text document to be kept in the Silo under <paramref name="name" /> (for everyone who opens the Silo).</summary>
    public static StoredTextResource NewDocument(IStoredStore store, string name, OpenSilo? silo)
    {
        return new StoredTextResource(store, SiloTextDocument.KeyPrefix + name, StoredScope.Shared, silo)
               { _document = new SiloTextDocument { Name = name } };
    }

    private static bool IsString(StoredRecord record)
    {
        return record.Format == StoredCodec.Json && SavedItems.ResolveType(record.TypeName) == typeof(string) &&
               record.Data.TrimStart().StartsWith('"');
    }
}

/// <summary>A preference kept in a Silo (its <c>Pref</c> table): the text as it is stored.</summary>
public sealed class PreferenceTextResource(IPreferenceStore store, string key, OpenSilo? silo) : TextResource
{
    public override bool      CanSave  => silo?.IsWritable ?? true;
    public override string    Key      => $"text:pref:{TextEditors.SiloKey(silo)}:{key.ToUpperInvariant()}";
    public override string    Location => $"Preference in {silo?.Name ?? "the Silo"}: {key}";
    public override OpenSilo? Silo     => silo;
    public override string    Title    => key;

    public override Task<TextContent> LoadAsync(CancellationToken cancel = default)
    {
        var record = store.GetPreferences().FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) ??
                     throw new InvalidOperationException($"There is no preference \"{key}\" any more.");
        return Task.FromResult(TextContent.Of(record.Data));
    }

    public override void Save(TextContent content)
    {
        store.SetPreference(key, content.Text);
    }
}

/// <summary>
///     Text that came from somewhere it cannot go back to (a web response): it can be edited and saved somewhere
///     else.
/// </summary>
public sealed class DetachedTextResource(
    string                                     key,
    string                                     title,
    string                                     location,
    Func<CancellationToken, Task<TextContent>> load,
    string?                                    fileName    = null,
    string?                                    contentType = null) : TextResource
{
    public override bool    CanSave     => false;
    public override string? ContentType => contentType;
    public override string? FileName    => fileName;
    public override string  Key         => key;
    public override string  Location    => location;
    public override string  Title       => title;

    public override Task<TextContent> LoadAsync(CancellationToken cancel = default)
    {
        return load(cancel);
    }

    /// <summary>A text already in hand.</summary>
    public static DetachedTextResource Of(string  key, string title, string location, string text, string? fileName = null,
                                          string? contentType = null)
    {
        return new DetachedTextResource(key, title, location, _ => Task.FromResult(TextContent.Of(text)), fileName, contentType);
    }
}

/// <summary>
///     A new document (File → New Text Document): it exists only in its tab until it is saved, in the Silo or as a
///     file.
/// </summary>
public sealed class ScratchTextResource(TextLanguage language, int number, string? initialText = null) : TextResource
{
    public override bool    CanSave  => false;
    public override string? FileName => Title;

    public override string       Key      => "text:new:" + number;
    public          TextLanguage Language { get; } = language;
    public override string       Location => "New document (not saved)";
    public override string       Title    => "Untitled-" + number + Language.DefaultExtension();

    public override Task<TextContent> LoadAsync(CancellationToken cancel = default)
    {
        return Task.FromResult(TextContent.Of(initialText ?? string.Empty, Language));
    }
}

/// <summary>
///     A text document kept in the Silo (File → New Text Document, saved in the Silo): its name, language and text,
///     stored as one value.
/// </summary>
public sealed class SiloTextDocument
{
    /// <summary>The start of the key of every text document in the <c>Stored</c> table.</summary>
    public const string KeyPrefix = "textdoc:";

    public static readonly string  TypeName = StoredCodec.TypeNameOf(typeof(SiloTextDocument));
    public                 string? Language { get; set; }

    public string? Name { get; set; }
    public string? Text { get; set; }
}

/// <summary>Reading a stored language name back.</summary>
public static class TextLanguageNames
{
    public static TextLanguage Parse(string? name)
    {
        return Enum.TryParse<TextLanguage>(name, true, out var language) ? language : TextLanguage.PlainText;
    }
}