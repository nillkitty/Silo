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

    private static readonly Dictionary<string, TextLanguage> Extensions = new(StringComparer.OrdinalIgnoreCase)
                                                                          {
                                                                              [".json"]   = TextLanguage.Json,
                                                                              [".jsonc"]  = TextLanguage.Json,
                                                                              [".json5"]  = TextLanguage.Json,
                                                                              [".geojson"] = TextLanguage.Json,
                                                                              [".har"]    = TextLanguage.Json,
                                                                              [".ipynb"]  = TextLanguage.Json,
                                                                              [".webmanifest"] = TextLanguage.Json,
                                                                              [".xml"]    = TextLanguage.Xml,
                                                                              [".xaml"]   = TextLanguage.Xml,
                                                                              [".xsd"]    = TextLanguage.Xml,
                                                                              [".xsl"]    = TextLanguage.Xml,
                                                                              [".xslt"]   = TextLanguage.Xml,
                                                                              [".config"] = TextLanguage.Xml,
                                                                              [".csproj"] = TextLanguage.Xml,
                                                                              [".vbproj"] = TextLanguage.Xml,
                                                                              [".props"]  = TextLanguage.Xml,
                                                                              [".targets"] = TextLanguage.Xml,
                                                                              [".nuspec"] = TextLanguage.Xml,
                                                                              [".resx"]   = TextLanguage.Xml,
                                                                              [".svg"]    = TextLanguage.Xml,
                                                                              [".plist"]  = TextLanguage.Xml,
                                                                              [".drawio"] = TextLanguage.Xml,
                                                                              [".xshd"]   = TextLanguage.Xml,
                                                                              [".rss"]    = TextLanguage.Xml,
                                                                              [".atom"]   = TextLanguage.Xml,
                                                                              [".yaml"]   = TextLanguage.Yaml,
                                                                              [".yml"]    = TextLanguage.Yaml
                                                                          };

    /// <summary>What to call the language in menus and the status line.</summary>
    public static string DisplayName(this TextLanguage language) => language switch
                                                                    {
                                                                        TextLanguage.Json => "JSON",
                                                                        TextLanguage.Xml  => "XML",
                                                                        TextLanguage.Yaml => "YAML",
                                                                        _                 => "Plain Text"
                                                                    };

    /// <summary>The usual file extension, with its dot.</summary>
    public static string DefaultExtension(this TextLanguage language) => language switch
                                                                         {
                                                                             TextLanguage.Json => ".json",
                                                                             TextLanguage.Xml  => ".xml",
                                                                             TextLanguage.Yaml => ".yaml",
                                                                             _                 => ".txt"
                                                                         };

    /// <summary>A Save/Open dialog filter that lists this language's files first, then all files.</summary>
    public static string FileFilter(this TextLanguage language) => language switch
                                                                   {
                                                                       TextLanguage.Json => "JSON files (*.json)|*.json|All files (*.*)|*.*",
                                                                       TextLanguage.Xml  => "XML files (*.xml)|*.xml|All files (*.*)|*.*",
                                                                       TextLanguage.Yaml => "YAML files (*.yaml;*.yml)|*.yaml;*.yml|All files (*.*)|*.*",
                                                                       _                 => "Text files (*.txt)|*.txt|All files (*.*)|*.*"
                                                                   };

    /// <summary>The filter of the Open Text File dialog: every language the editor knows, then all files.</summary>
    public const string OpenFilter = "Text files (*.txt;*.json;*.xml;*.yaml;*.yml;*.log;*.md;*.csv;*.ini;*.config)|*.txt;*.json;*.xml;*.yaml;*.yml;*.log;*.md;*.csv;*.ini;*.config|" +
                                     "JSON files (*.json)|*.json|XML files (*.xml)|*.xml|YAML files (*.yaml;*.yml)|*.yaml;*.yml|All files (*.*)|*.*";

    /// <summary>The language a file name suggests; null when its extension says nothing.</summary>
    public static TextLanguage? FromFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;

        string extension = Path.GetExtension(fileName);
        if (Extensions.TryGetValue(extension, out var language)) return language;

        string name = Path.GetFileName(fileName);
        if (name.Equals(".clang-format", StringComparison.OrdinalIgnoreCase)) return TextLanguage.Yaml;
        return extension.Length > 0 ? TextLanguage.PlainText : null;
    }

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
