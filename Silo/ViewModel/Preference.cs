using System.ComponentModel;
using Serilog;

namespace Silo.Model;

/// <summary>
///     One preference of one model: the value of a property marked with <see cref="PreferenceAttribute" /> (or
///     <see cref="ToggleAttribute" />), with its key, text and default. Setting <see cref="Value" /> sets the property and keeps the
///     new value in the Silo.
/// </summary>
/// <remarks>
///     The property is plain code, so setting it directly does not write anything by itself: <see cref="ModelBase.SavePreferences" />
///     (which a Silo runs before it is saved or closed) writes what changed that way.
/// </remarks>
public sealed class Preference : INotifyPropertyChanged
{
    private readonly object                  _owner;
    private readonly Func<IPreferenceStore?> _store;

    // the text the Silo holds for the key now; null: it holds none
    private string? _storedText;

    /// <param name="definition">What is declared for the preference.</param>
    /// <param name="owner">The model whose property it is.</param>
    /// <param name="store">Where the model keeps its preferences now (asked each time, a model gets its Silo late); null for nowhere.</param>
    internal Preference(PreferenceDefinition definition, object owner, Func<IPreferenceStore?> store)
    {
        Definition = definition;
        _owner     = owner;
        _store     = store;
        Default    = definition.DeclaredDefault ?? definition.Property.GetValue(owner);
    }

    public string? Category => Definition.Category;

    /// <summary>The value when nothing else was chosen: the attribute's default, else what the property started as.</summary>
    public object? Default { get; }

    public PreferenceDefinition Definition { get; }

    public string Description => Definition.Description;

    /// <summary>Whether the value is the default.</summary>
    public bool IsDefault => Text == PreferenceValues.ToText(Default);

    /// <summary>Whether this is a yes/no preference (a <see cref="ToggleAttribute" />).</summary>
    public bool IsToggle => Definition.IsToggle;

    public string Key => Definition.Key;

    /// <summary>The value written as it is kept in the Silo.</summary>
    public string Text => PreferenceValues.ToText(Value);

    /// <summary>The current value: reads the property; setting it sets the property and writes the value to the Silo.</summary>
    /// <exception cref="ArgumentException">The value is not of the preference's type.</exception>
    public object? Value
    {
        get => Definition.Property.GetValue(_owner);
        set
        {
            Apply(value);
            Persist();
        }
    }

    public Type ValueType => Definition.ValueType;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Back to the default, written to the Silo like any other choice.</summary>
    public void Reset() => Value = Default;

    /// <summary>Tells whoever shows the preference that the property was changed in code.</summary>
    public void Refresh() => RaiseChanged();

    /// <summary>Sets the property without writing anything.</summary>
    internal void Apply(object? value)
    {
        if (!Accepts(value, out object? coerced))
            throw new ArgumentException($"\"{value}\" is not a {ValueType.Name}, which is what the preference {Key} holds.", nameof(value));

        Definition.Property.SetValue(_owner, coerced);
        RaiseChanged();
    }

    // a value of the right type as it is; else one that reads as such (the int 5 for a double)
    private bool Accepts(object? value, out object? coerced)
    {
        coerced = value;
        if (value is null) return !ValueType.IsValueType || Nullable.GetUnderlyingType(ValueType) is not null;

        var target = Nullable.GetUnderlyingType(ValueType) ?? ValueType;
        if (target.IsInstanceOfType(value)) return true;

        return PreferenceValues.TryFromText(ValueType, PreferenceValues.ToText(value), out coerced);
    }

    /// <summary>Takes the value the Silo holds (null: none) as the property's -- or, when it holds none, the declared default.</summary>
    internal void Load(string? stored)
    {
        _storedText = stored;

        if (stored is null)
        {
            if (Definition.DeclaredDefault is not null) Apply(Definition.DeclaredDefault);
            return;
        }

        if (PreferenceValues.TryFromText(ValueType, stored, out object? value))
            Apply(value);
        else
            Log.Warning("The stored value \"{Stored}\" of the preference {Key} is not a {Type}: using {Default}", stored, Key, ValueType.Name, Default);
    }

    /// <summary>Writes the value to the Silo if it is not what the Silo holds (or, with nothing held, if it is not the default).</summary>
    internal void Persist()
    {
        var store = _store();
        if (store is null) return;

        string text = Text;
        if (text == (_storedText ?? PreferenceValues.ToText(Default))) return;

        store.SetPreference(Key, text);
        _storedText = text;
    }

    private void RaiseChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDefault)));
    }
}
