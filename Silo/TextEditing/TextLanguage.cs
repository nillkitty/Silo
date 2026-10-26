using Silo.Providers.Format;
using Silo.Providers.Reflection;

namespace Silo.TextEditing;

/// <summary>What the text in a text editor is written in: decides the highlighting, the checks, the folding and the formatting.</summary>
public enum TextLanguage
{
    /// <summary>Text with no structure: no highlighting, no checks.</summary>
    PlainText,

    Json,

    Xml,

    Yaml
}

/// <summary>Names, file types and detection for <see cref="TextLanguage" />.</summary>
public static class TextLanguages
{
    /// <summary>Every language, in the order menus list them.</summary>
    public static IReadOnlyList<TextLanguage> All { get; } = [TextLanguage.PlainText, TextLanguage.Json, TextLanguage.Xml, TextLanguage.Yaml];

    /// <summary>The languages that have a structure (they can be checked, formatted and converted).</summary>
    public static IReadOnlyList<TextLanguage> Structured { get; } = [TextLanguage.Json, TextLanguage.Xml, TextLanguage.Yaml];

    /// <summary>What to call the language in menus and the status line.</summary>
    public static string DisplayName(this TextLanguage language) => language switch
                                                                    {
                                                                        TextLanguage.Json => "JSON",
                                                                        TextLanguage.Xml  => "XML",
                                                                        TextLanguage.Yaml => "YAML",
                                                                        _                 => "Plain Text"
                                                                    };

    /// <summary>The file format the language's files are saved as (plain text for <see cref="TextLanguage.PlainText" />).</summary>
    public static IFileFormat Format(this TextLanguage language) => language switch
                                                                    {
                                                                        TextLanguage.Json => Formats.Of<JsonFormat>(),
                                                                        TextLanguage.Xml  => Formats.Of<XmlFormat>(),
                                                                        TextLanguage.Yaml => Formats.Of<YamlFormat>(),
                                                                        _                 => Formats.Of<PlainTextFormat>()
                                                                    };

    /// <summary>The usual file extension, with its dot.</summary>
    public static string DefaultExtension(this TextLanguage language) => language.Format().DefaultExtension();

    /// <summary>A Save/Open dialog filter that lists this language's files first, then all files.</summary>
    public static string FileFilter(this TextLanguage language) => language.Format().Filter(true);

    /// <summary>The filter of the Open Text File dialog: every kind of text file the editor is used for, each structured language, then all files.</summary>
    public static string OpenFilter
    {
        get
        {
            IFileFormat[] text =
            [
                Formats.Of<PlainTextFormat>(), Formats.Of<JsonFormat>(), Formats.Of<XmlFormat>(), Formats.Of<YamlFormat>(), Formats.Of<LogFormat>(),
                Formats.Of<MarkdownFormat>(), Formats.Of<CsvFormat>(), Formats.Of<IniFormat>(), Formats.Of<ConfigFormat>()
            ];
            string all = string.Join(';', text.Select(f => f.Pattern()));
            return $"Text files ({all})|{all}|" + string.Join('|', Structured.Select(l => l.Format().Filter())) + "|" + Formats.AllFiles;
        }
    }

    /// <summary>The language a file name suggests (from the formats that claim its extension); null when its extension says nothing.</summary>
    public static TextLanguage? FromFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;

        foreach (var format in Formats.ForFile(fileName))
            if (FromFormat(format) is { } language)
                return language;

        string name = Path.GetFileName(fileName);
        if (name.Equals(".clang-format", StringComparison.OrdinalIgnoreCase)) return TextLanguage.Yaml;
        return Path.GetExtension(fileName).Length > 0 ? TextLanguage.PlainText : null;
    }

    /// <summary>The language files of <paramref name="format" /> are written in (a dialect's base syntax); null when it is none of them.</summary>
    public static TextLanguage? FromFormat(IFileFormat format) => Formats.SyntaxOf(format) switch
                                                                  {
                                                                      JsonFormat => TextLanguage.Json,
                                                                      XmlFormat  => TextLanguage.Xml,
                                                                      YamlFormat => TextLanguage.Yaml,
                                                                      _          => null
                                                                  };

    /// <summary>The language an HTTP <c>Content-Type</c> names; null when it names none of them.</summary>
    public static TextLanguage? FromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;

        string type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (type is "application/json" or "text/json" || type.EndsWith("+json", StringComparison.Ordinal)) return TextLanguage.Json;
        if (type is "application/xml" or "text/xml" || type.EndsWith("+xml", StringComparison.Ordinal)) return TextLanguage.Xml;
        if (type is "application/yaml" or "application/x-yaml" or "text/yaml" or "text/x-yaml") return TextLanguage.Yaml;
        if (type.StartsWith("text/", StringComparison.Ordinal)) return TextLanguage.PlainText;
        return null;
    }

    /// <summary>
    ///     A guess from the text itself, for text with no name to go by: JSON when it starts with <c>{</c> or <c>[</c>, XML when it
    ///     starts with <c>&lt;</c>, YAML when it starts with <c>---</c> or its first lines look like <c>key: value</c>.
    /// </summary>
    public static TextLanguage Sniff(string? text)
    {
        if (string.IsNullOrEmpty(text)) return TextLanguage.PlainText;

        int i = 0;
        if (text[0] == '﻿') i++;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length) return TextLanguage.PlainText;

        char first = text[i];
        if (first is '{' or '[') return TextLanguage.Json;
        if (first == '<') return TextLanguage.Xml;
        if (string.CompareOrdinal(text, i, "---", 0, 3) == 0 || string.CompareOrdinal(text, i, "%YAML", 0, 5) == 0) return TextLanguage.Yaml;

        // a few lines of "key: value" (or "- item"), ignoring comments
        int checkedLines = 0, yamlLines = 0;
        foreach (string raw in text[i..].Split('\n').Take(12))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            checkedLines++;
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[\w\-""'. ]+:(\s|$)"))
                yamlLines++;
        }

        return checkedLines >= 2 && yamlLines == checkedLines ? TextLanguage.Yaml : TextLanguage.PlainText;
    }

    /// <summary>The language to use: what the name says, else the content type, else a guess from the text.</summary>
    public static TextLanguage Detect(string? fileName, string? contentType, string? text)
    {
        var byName = FromFileName(fileName);
        if (byName is { } named && named != TextLanguage.PlainText) return named;

        if (FromContentType(contentType) is { } typed && typed != TextLanguage.PlainText) return typed;

        // a .txt (or a name with no extension) can still hold JSON: only trust plain text from the name when the text agrees
        var sniffed = Sniff(text);
        return byName is TextLanguage.PlainText && sniffed == TextLanguage.Yaml ? TextLanguage.PlainText : sniffed;
    }
}
