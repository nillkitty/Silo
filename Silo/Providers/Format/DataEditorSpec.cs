namespace Silo.Providers.Data;

/// <summary>
///     The editor a data specification asks for, as it is kept (it is a member of <c>ParserSpecDoc</c>, so it is stored in the Silo with the
///     specification). Plain data: <see cref="IDataEditor" /> gives the settings their meaning.
/// </summary>
public sealed class DataEditorSpec : IDataEditorSpec
{
    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    IReadOnlyDictionary<string, string> IDataEditorSpec.Settings => Settings;

    /// <summary>A setting, or <paramref name="fallback" /> when there is none (or it is empty).</summary>
    public string Get(string name, string fallback = "") => Settings.TryGetValue(name, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    /// <summary>A copy of the editor's spec.</summary>
    public DataEditorSpec Clone() => new() { Kind = Kind, Title = Title, Settings = new Dictionary<string, string>(Settings, StringComparer.OrdinalIgnoreCase) };

    /// <summary>An editor spec for <paramref name="editor" />, with the settings it starts with.</summary>
    public static DataEditorSpec For(IDataEditor editor) => new() { Kind = editor.Kind, Title = editor.DisplayName, Settings = new Dictionary<string, string>(editor.DefaultSettings, StringComparer.OrdinalIgnoreCase) };

    /// <summary>A setting of any <see cref="IDataEditorSpec" />, <paramref name="fallback" /> when there is none.</summary>
    public static string Setting(IDataEditorSpec spec, string name, string fallback = "") =>
        spec.Settings.TryGetValue(name, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
}
