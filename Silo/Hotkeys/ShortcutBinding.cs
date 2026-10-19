using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Silo.Hotkeys;

/// <summary>Where a shortcut works.</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum ShortcutScope
{
    /// <summary>In every application: Windows reserves the key for Silo while it runs.</summary>
    Global,

    /// <summary>Only while one of Silo's own windows is active.</summary>
    Application
}

/// <summary>
///     A shortcut key tied to an action: <c>Ctrl+Win+J</c> does <c>type ${your:blue}</c>. This is the value a global shortcut preference keeps (see
///     <c>IGlobalShortcutPref</c>); it is written to JSON as it is.
/// </summary>
public sealed record ShortcutBinding
{
    /// <summary>What the person calls the binding. It is also what the binding is stored under, so it is unique among the bindings.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The key that runs the action; <see cref="ShortcutKey.None" /> when the binding has none yet.</summary>
    public ShortcutKey Key { get; init; }

    /// <summary>
    ///     What to do, a verb and a template: <c>type ${your:blue}</c> types the text, <c>copy ${your:blue}</c> puts it on the clipboard. The template
    ///     may use <c>${...}</c> expressions; see <see cref="ShortcutActionText" />.
    /// </summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>Where the shortcut works.</summary>
    public ShortcutScope Scope { get; init; } = ShortcutScope.Global;

    /// <summary>Whether the shortcut is on. A binding that is off keeps its key and action but does nothing.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether the binding has a key and an action, so it could do something.</summary>
    [JsonIgnore]
    public bool IsBound => Key.IsSet && !string.IsNullOrWhiteSpace(Action);

    /// <summary>A binding with a name but no key and no action: what a preference holds before the person binds it.</summary>
    public static ShortcutBinding Unbound(string name) => new() { Name = name };

    /// <summary>
    ///     Whether the binding can be used. An unbound binding is valid (it just does nothing); a bound one needs a name, a key that suits its
    ///     <see cref="Scope" />, and an action that reads.
    /// </summary>
    /// <param name="problem">When false, what to change.</param>
    public bool Validate(out string? problem)
    {
        problem = null;
        if (!Key.IsSet && string.IsNullOrWhiteSpace(Action)) return true;

        if (string.IsNullOrWhiteSpace(Name))
        {
            problem = "Give the shortcut a name.";
            return false;
        }

        if (Scope == ShortcutScope.Global && !Key.IsValidGlobal(out problem)) return false;
        if (!Key.IsSet)
        {
            problem = "Choose a key.";
            return false;
        }

        return ShortcutActionText.TryParse(Action, out _, out problem);
    }
}
