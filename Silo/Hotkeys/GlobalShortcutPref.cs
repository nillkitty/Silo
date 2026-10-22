using System.ComponentModel;
using Silo.Expressions;
using Silo.Providers.Data;
using Silo.Storage;

namespace Silo.Hotkeys;

/// <summary>
///     A shortcut key binding as a preference (see <see cref="IGlobalShortcutPref" />): the <see cref="ShortcutBinding" />
///     is kept in the Silo as a
///     <c>Stored&lt;ShortcutBinding&gt;</c> under <c>shortcut:name</c>, in the person's own part of the Silo or the shared
///     one, as it was made.
/// </summary>
/// <remarks>The default is the binding that has a name and nothing else, so resetting the preference removes the row.</remarks>
public sealed class GlobalShortcutPref : IGlobalShortcutPref
{
    /// <summary>What every key of a shortcut preference starts with.</summary>
    public const string KeyPrefix = "shortcut:";

    private readonly Stored<ShortcutBinding> _stored;
    private readonly Func<IVariableScope>    _variables;
    private readonly ShortcutVerbs           _verbs;

    /// <inheritdoc />
    public string Action => Value.Action;

    /// <inheritdoc />
    public bool Enabled => Value.Enabled;

    /// <inheritdoc />
    public bool IsBound => Value.IsBound;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ShortcutScope Scope => Value.Scope;

    /// <inheritdoc />
    public ShortcutKey Shortcut => Value.Key;

    /// <summary>Whose the binding is.</summary>
    public StoredScope Storage { get; }

    /// <inheritdoc />
    public string? Category => "Shortcuts";

    /// <inheritdoc />
    public string Description => IsBound ? $"{Shortcut}: {Action}" : "Not bound to a key yet.";

    /// <inheritdoc />
    public bool IsDefault => Value == Default;

    /// <inheritdoc />
    public bool IsToggle => false;

    /// <inheritdoc />
    public string Key => KeyPrefix + Name;

    /// <inheritdoc />
    public string Text => StoredCodec.Serialize(Value);

    /// <inheritdoc />
    public Type ValueType => typeof(ShortcutBinding);

    object? IPreference.Default => Default;

    object? IPreference.Value
    {
        get => Value;
        set => Value = value as ShortcutBinding ??
                       throw new ArgumentException($"\"{value}\" is not a shortcut binding, which is what the preference {Key} holds.",
                                                   nameof(value));
    }

    /// <inheritdoc />
    public ShortcutBinding Default => ShortcutBinding.Unbound(Name);

    /// <inheritdoc />
    public ShortcutBinding Value
    {
        get => _stored.TryGet(out var value) && value is not null ? value with { Name = Name } : Default;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            var named = value with { Name = Name };
            if (!named.Validate(out string? problem)) throw new ArgumentException(problem, nameof(value));

            if (named == Default) _stored.Delete();
            else _stored.Set(named);
            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        foreach (string property in new[]
                                    {
                                        nameof(Value), nameof(Text), nameof(IsDefault), nameof(Description), nameof(Shortcut),
                                        nameof(Action), nameof(Scope), nameof(Enabled), nameof(IsBound)
                                    })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    /// <param name="name">The binding's name.</param>
    /// <param name="storage">Whose it is.</param>
    /// <param name="store">Where the Silo is, asked each time.</param>
    /// <param name="verbs">What the verbs of the action do.</param>
    /// <param name="variables">The variables the action's template can use, asked each time it runs.</param>
    public GlobalShortcutPref(string               name, StoredScope storage, Func<IStoredStore?> store, ShortcutVerbs verbs,
                              Func<IVariableScope> variables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name       = name.Trim();
        Storage    = storage;
        _stored    = new Stored<ShortcutBinding>(KeyPrefix + Name, storage, store);
        _verbs     = verbs;
        _variables = variables;
    }

    /// <inheritdoc />
    public void Reset()
    {
        Value = Default;
    }

    /// <inheritdoc />
    public void Refresh()
    {
        RaiseChanged();
    }

    /// <inheritdoc />
    public ShortcutRunResult Run()
    {
        var binding = Value;
        if (!binding.IsBound) return new ShortcutRunResult(false, null, $"\"{Name}\" has no key and action yet.");
        return _verbs.Run(binding.Action, _variables());
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
///     The global shortcut key bindings of the active Silo, as preferences (<see cref="Preferences" />, what an
///     <see cref="IPreferenceProvider" /> lists).
///     A binding is the person's own or the Silo's shared one; where both have the name, the person's wins.
/// </summary>
/// <remarks>
///     Deliberately not an <see cref="IPreferenceProvider" /> itself: that interface is <c>[RequireRegistration]</c> (the
///     container makes its
///     implementations), and this one is made by <see cref="GlobalHotkeyService" /> from the Silo that is active, so it
///     has no
///     container-made constructor.
/// </remarks>
public sealed class GlobalShortcutPrefs : IDisposable
{
    private readonly Func<IStoredStore?>  _store;
    private readonly Func<IVariableScope> _variables;
    private readonly ShortcutVerbs        _verbs;

    /// <summary>The same bindings as <see cref="Shortcuts" />, as the general kind of preference.</summary>
    public IReadOnlyList<IPreference> Preferences => Shortcuts;

    /// <summary>Every binding, ordered by name.</summary>
    public IReadOnlyList<IGlobalShortcutPref> Shortcuts
    {
        get
        {
            var store = _store();
            if (store is null) return [];

            var found = new Dictionary<string, GlobalShortcutPref>(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in new[] { StoredScope.Shared, StoredScope.Personal }) // the person's own last, so it wins
                foreach (var record in store.ListStored(GlobalShortcutPref.KeyPrefix, scope))
                {
                    string name                      = record.Key[GlobalShortcutPref.KeyPrefix.Length..];
                    if (name.Length > 0) found[name] = Make(name, scope);
                }

            return found.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    /// <summary>Raised after a binding was added, changed or removed (by this or by any preference of it).</summary>
    public event EventHandler? Changed;

    /// <summary>The binding with the name (any case); null if there is none.</summary>
    public IGlobalShortcutPref? Find(string name)
    {
        return Shortcuts.FirstOrDefault(p => p.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     The enabled binding in <paramref name="binding" />'s scope that already uses its key, other than
    ///     <paramref name="ignoring" />; null if none does.
    /// </summary>
    public IGlobalShortcutPref? FindConflict(ShortcutBinding binding, string? ignoring = null)
    {
        if (!binding.Key.IsSet || !binding.Enabled) return null;

        return Shortcuts.FirstOrDefault(p => p.Enabled && p.IsBound && p.Shortcut == binding.Key && p.Scope == binding.Scope &&
                                             !p.Name.Equals(ignoring ?? binding.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Whether the binding could be saved as <paramref name="ignoring" /> (its own old name, when it is being
    ///     edited); false says what to change.
    /// </summary>
    public bool Validate(ShortcutBinding binding, string? ignoring, out string? problem)
    {
        if (!binding.Validate(out problem)) return false;

        var conflict = FindConflict(binding, ignoring);
        if (conflict is not null)
        {
            problem = $"{binding.Key} already runs \"{conflict.Name}\"; choose another key, or turn that one off.";
            return false;
        }

        var sameName = Find(binding.Name);
        if (sameName is not null && !binding.Name.Equals(ignoring, StringComparison.OrdinalIgnoreCase))
        {
            problem = $"There is a shortcut called \"{sameName.Name}\" already.";
            return false;
        }

        problem = null;
        return true;
    }

    /// <summary>
    ///     Saves the binding (under its name, replacing the one called <paramref name="replacing" /> when it is being edited:
    ///     renamed, or kept in another place).
    /// </summary>
    /// <exception cref="ArgumentException">The binding cannot be saved (see <see cref="Validate" />).</exception>
    /// <exception cref="InvalidOperationException">No Silo is open.</exception>
    public IGlobalShortcutPref Save(ShortcutBinding binding, StoredScope storage = StoredScope.Personal, string? replacing = null)
    {
        if (_store() is null) throw new InvalidOperationException("No Silo is open to keep the shortcut in.");
        if (!Validate(binding, replacing, out string? problem)) throw new ArgumentException(problem, nameof(binding));
        if (!binding.IsBound) throw new ArgumentException("Choose a key and an action.",             nameof(binding));

        // the old one goes first (also when only its storage changes, so the old row does not linger beside the new one)
        if (replacing is not null) Remove(replacing);

        var pref = Make(binding.Name.Trim(), storage);
        pref.Value = binding; // storing it raises Stored.Changed, which raises Changed
        return pref;
    }

    /// <summary>Removes the binding (the person's and the shared one of that name); false if there was none.</summary>
    public bool Remove(string name)
    {
        var store = _store();
        if (store is null || string.IsNullOrWhiteSpace(name)) return false;

        string key     = GlobalShortcutPref.KeyPrefix + name.Trim();
        bool   removed = false;
        foreach (var scope in new[] { StoredScope.Personal, StoredScope.Shared })
            if (store.DeleteStored(key, scope))
            {
                removed = true;
                Stored.RaiseChanged(key, scope, true);
            }

        return removed;
    }

    private GlobalShortcutPref Make(string name, StoredScope scope)
    {
        return new GlobalShortcutPref(name, scope, _store, _verbs, _variables);
    }

    private void OnStoredChanged(object? sender, StoredChangedEventArgs e)
    {
        if (e.Key.StartsWith(GlobalShortcutPref.KeyPrefix, StringComparison.OrdinalIgnoreCase)) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <param name="store">Where the Silo is, asked each time; null for nowhere.</param>
    /// <param name="verbs">What the verbs of the actions do.</param>
    /// <param name="variables">The variables the actions' templates can use.</param>
    public GlobalShortcutPrefs(Func<IStoredStore?> store, ShortcutVerbs verbs, Func<IVariableScope> variables)
    {
        _store         =  store;
        _verbs         =  verbs;
        _variables     =  variables;
        Stored.Changed += OnStoredChanged;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stored.Changed -= OnStoredChanged;
    }
}