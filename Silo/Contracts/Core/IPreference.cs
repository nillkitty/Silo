using System.ComponentModel;
using Silo.Hotkeys;
using Silo.Providers.Data;

namespace Silo.Contracts.Core;

/// <summary>
///     One preference, whatever it is kept in: a property of a model (<c>Preference</c>), a global shortcut key binding (
///     <see cref="IGlobalShortcutPref" />).
///     What an <see cref="IPreferenceProvider" /> lists.
/// </summary>
public interface IPreference : INotifyPropertyChanged
{
    /// <summary>The heading it is shown under; null for none.</summary>
    string? Category { get; }

    /// <summary>The value when nothing else was chosen.</summary>
    object? Default { get; }

    /// <summary>What it is for, in a sentence.</summary>
    string Description { get; }

    /// <summary>Whether the value is the default.</summary>
    bool IsDefault { get; }

    /// <summary>Whether this is a yes/no preference.</summary>
    bool IsToggle { get; }

    /// <summary>What the preference is kept under; unique among the preferences of a provider.</summary>
    string Key { get; }

    /// <summary>The value written as it is kept.</summary>
    string Text { get; }

    /// <summary>The current value; setting it keeps the new value.</summary>
    /// <exception cref="ArgumentException">
    ///     The value is not of <see cref="ValueType" /> (or not acceptable for the
    ///     preference).
    /// </exception>
    object? Value { get; set; }

    /// <summary>The type of the value.</summary>
    Type ValueType { get; }

    /// <summary>Back to the default, kept like any other choice.</summary>
    void Reset();

    /// <summary>Tells whoever shows the preference that its value was changed in some other way.</summary>
    void Refresh();
}

/// <summary>A preference whose value is a <typeparamref name="T" />.</summary>
public interface IPreference<T> : IPreference
{
    /// <summary>The value when nothing else was chosen.</summary>
    new T Default { get; }

    /// <summary>The current value; setting it keeps the new value.</summary>
    new T Value { get; set; }
}

/// <summary>
///     A preference that binds a shortcut key to an action (a <see cref="ShortcutBinding" />): <c>Ctrl+Win+J</c> types
///     <c>${your:blue}</c>. The binding is
///     what the preference holds; it can also be run.
/// </summary>
public interface IGlobalShortcutPref : IPreference<ShortcutBinding>
{
    /// <summary>The action, <c>verb template</c>.</summary>
    string Action { get; }

    /// <summary>Whether the shortcut is on.</summary>
    bool Enabled { get; }

    /// <summary>Whether it has a key and an action.</summary>
    bool IsBound { get; }

    /// <summary>The name of the binding; the part of <see cref="IPreference.Key" /> after <c>shortcut:</c>.</summary>
    string Name { get; }

    /// <summary>Where the shortcut works.</summary>
    ShortcutScope Scope { get; }

    /// <summary>The shortcut key; <see cref="ShortcutKey.None" /> when it has none.</summary>
    ShortcutKey Shortcut { get; }

    /// <summary>Whose it is: the person's own, or shared by everyone who uses the Silo.</summary>
    StoredScope Storage { get; }

    /// <summary>Does the action now, whatever the key; nothing is thrown, a failure is the result.</summary>
    ShortcutRunResult Run();
}