using System.Collections;
using Newtonsoft.Json;
using Serilog;
using Silo.Storage;

namespace Silo.Labs;

/// <summary>How a Lab parameter is edited, and so what its value is.</summary>
public enum LabParameterKind
{
    /// <summary>One line of text (a <see cref="string" />).</summary>
    Text,

    /// <summary>Any number of lines of text, one entry each (a <see cref="string" />[]).</summary>
    Lines,

    /// <summary>On or off (a <see cref="bool" />).</summary>
    Bool,

    /// <summary>One of <see cref="LabParameter.Choices" /> (a <see cref="string" />).</summary>
    Choice,

    /// <summary>Any of <see cref="LabParameter.Choices" /> (a <see cref="string" />[] of the chosen ones).</summary>
    Flags
}

/// <summary>
///     One setting of a Lab (the home address of the Web Lab, the hosts it blocks...): described once here, edited in the
///     Lab's Parameters
///     pane or with <c>set</c> in its terminal, and kept in the Silo by its <see cref="LabParameterSet" />.
/// </summary>
public sealed class LabParameter
{
    private object? _value;

    /// <summary>The value of an on/off parameter.</summary>
    public bool Bool => _value is true;

    /// <summary>
    ///     For <see cref="LabParameterKind.Choice" /> and <see cref="LabParameterKind.Flags" />: what there is to choose
    ///     from.
    /// </summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>The value when nothing has been set.</summary>
    public object? Default { get; }

    /// <summary>What it does; shown under the title.</summary>
    public string Description { get; }

    /// <summary>The heading it is listed under in the Parameters pane.</summary>
    public string Group { get; }

    /// <summary>Shown as a tooltip on the editor (the format of a line, say).</summary>
    public string? Hint { get; }

    /// <summary>Whether the value is what it is when nothing is set.</summary>
    public bool IsDefault => SameValue(_value, Default);

    public LabParameterKind Kind { get; }

    /// <summary>The entries of a <see cref="LabParameterKind.Lines" /> or <see cref="LabParameterKind.Flags" /> parameter.</summary>
    public IReadOnlyList<string> Lines => _value as string[] ?? [];

    /// <summary>What it is called in the terminal and in the saved text ("blockedHosts"); not changed once released.</summary>
    public string Name { get; }

    /// <summary>The value as text.</summary>
    public string Text => _value as string ?? string.Empty;

    /// <summary>What it is called to a person ("Blocked hosts").</summary>
    public string Title { get; }

    /// <summary>
    ///     The value: a <see cref="string" />, a <see cref="bool" /> or a <see cref="string" />[] by <see cref="Kind" />.
    ///     Setting it raises <see cref="Changed" />.
    /// </summary>
    public object? Value
    {
        get => _value;
        set
        {
            var normalized = Normalize(Kind, value);
            if (SameValue(_value, normalized)) return;

            _value = normalized;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Changed;

    /// <summary>The value written as a person would type it in the terminal.</summary>
    public string Display()
    {
        return Kind switch
               {
                   LabParameterKind.Bool  => Bool ? "on" : "off",
                   LabParameterKind.Lines => string.Join(" | ", Lines),
                   LabParameterKind.Flags => string.Join(", ",  Lines),
                   _                      => Text
               };
    }

    /// <summary>
    ///     Sets the value from text typed in the terminal. Throws <see cref="FormatException" /> when the text is not a
    ///     value of this parameter.
    /// </summary>
    public void SetFromText(string text)
    {
        switch (Kind)
        {
            case LabParameterKind.Bool:
                Value = text.Trim().ToLowerInvariant() switch
                        {
                            "true" or "on" or "yes" or "1" or "y"  => true,
                            "false" or "off" or "no" or "0" or "n" => false,
                            _                                      => throw new FormatException($"'{text}' is not on or off.")
                        };
                break;

            case LabParameterKind.Choice:
                Value = Choices.FirstOrDefault(c => string.Equals(c, text.Trim(), StringComparison.OrdinalIgnoreCase)) ??
                        throw new FormatException($"'{text}' is not one of: {string.Join(", ", Choices)}.");
                break;

            case LabParameterKind.Flags:
                var picked = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .Select(w => Choices.FirstOrDefault(c => string.Equals(c, w, StringComparison.OrdinalIgnoreCase)) ??
                                              throw new FormatException($"'{w}' is not one of: {string.Join(", ", Choices)}."))
                                 .ToArray();
                Value = picked;
                break;

            case LabParameterKind.Lines:
                Value = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                break;

            default:
                Value = text;
                break;
        }
    }

    /// <summary>The value as JSON text (what is kept in the Silo).</summary>
    internal string ToJson()
    {
        return JsonConvert.SerializeObject(_value);
    }

    /// <summary>Takes the value from JSON text written by <see cref="ToJson" />; an unreadable one leaves the value alone.</summary>
    internal void FromJson(string json)
    {
        try
        {
            Value = Kind switch
                    {
                        LabParameterKind.Bool                            => JsonConvert.DeserializeObject<bool>(json),
                        LabParameterKind.Lines or LabParameterKind.Flags => JsonConvert.DeserializeObject<string[]>(json),
                        _                                                => JsonConvert.DeserializeObject<string>(json)
                    };
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "The saved value of Lab parameter {Name} cannot be read", Name);
        }
    }

    internal void Reset()
    {
        Value = Default;
    }

    private static object? Normalize(LabParameterKind kind, object? value)
    {
        switch (kind)
        {
            case LabParameterKind.Bool:
                return value is bool b ? b : bool.TryParse(value?.ToString(), out var parsed) && parsed;

            case LabParameterKind.Lines:
            case LabParameterKind.Flags:
                IEnumerable<string> entries;
                if (value is null) entries                           = Array.Empty<string>();
                else if (value is string s) entries                  = s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                else if (value is IEnumerable<string> typed) entries = typed;
                else if (value is IEnumerable untyped) entries       = untyped.Cast<object?>().Select(o => o?.ToString() ?? string.Empty);
                else entries                                         = [value.ToString() ?? string.Empty];

                var cleaned = entries.Select(e => e.Trim()).Where(e => e.Length > 0);
                return (kind == LabParameterKind.Flags ? cleaned.Distinct(StringComparer.OrdinalIgnoreCase) : cleaned).ToArray();

            default:
                return value?.ToString() ?? string.Empty;
        }
    }

    private static bool SameValue(object? a, object? b)
    {
        if (a is string[] x && b is string[] y) return x.SequenceEqual(y);
        return Equals(a, b);
    }

    public LabParameter(string  name,         string title, LabParameterKind kind, object? defaultValue = null, string? description = null,
                        string? group = null, IReadOnlyList<string>? choices = null, string? hint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name        = name;
        Title       = title;
        Kind        = kind;
        Description = description ?? string.Empty;
        Group       = group       ?? string.Empty;
        Hint        = hint;
        Choices     = choices ?? [];
        Default     = Normalize(kind, defaultValue);
        _value      = Default;
    }
}

/// <summary>
///     The parameters of one Lab, kept in the Silo for the person using it: what is not the default is stored (as one
///     item,
///     <c>lab:&lt;id&gt;:parameters</c>, which the Explorer lists with the other saved things), so a Lab that gains a
///     parameter, or changes a
///     default, reaches people who never touched it. Named presets (<c>lab:&lt;id&gt;:preset:&lt;name&gt;</c>) keep whole
///     sets to load later.
///     With no Silo open the parameters work for the session and are not kept.
/// </summary>
public sealed class LabParameterSet : IEnumerable<LabParameter>
{
    private readonly List<LabParameter>               _all    = [];
    private readonly Dictionary<string, LabParameter> _byName = new(StringComparer.OrdinalIgnoreCase);
    private          bool                             _loading;

    public int Count => _all.Count;

    public LabParameter this[string name] => _byName[name];

    /// <summary>The Lab the parameters are of.</summary>
    public string LabId { get; }

    /// <summary>What the parameters are kept under in the Silo.</summary>
    public string StoreKey => $"lab:{LabId}:parameters";

    private string PresetPrefix => $"lab:{LabId}:preset:";

    /// <summary>
    ///     Raised after a parameter changed (that parameter), or after many did at once -- loading, resetting, a preset
    ///     (null). May be raised
    ///     on any thread; whoever shows the parameters marshals.
    /// </summary>
    public event Action<LabParameter?>? Changed;

    public bool TryGet(string name, out LabParameter parameter)
    {
        return _byName.TryGetValue(name, out parameter!);
    }

    /// <summary>
    ///     The parameter a person means by <paramref name="key" />: its name or its title with the spaces taken out (any
    ///     case); null if none.
    /// </summary>
    public LabParameter? Find(string key)
    {
        if (_byName.TryGetValue(key, out var byName)) return byName;

        string squeezed = new string(key.Where(char.IsLetterOrDigit).ToArray());
        return _all.FirstOrDefault(p => string.Equals(new string(p.Title.Where(char.IsLetterOrDigit).ToArray()), squeezed,
                                                      StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Puts every parameter to what is kept in the Silo (the default for those not kept). Called when the Silo
    ///     changes.
    /// </summary>
    public void Load()
    {
        _loading = true;
        try
        {
            foreach (var p in _all) p.Reset();

            var stored = Stored.At<Dictionary<string, string>>(StoreKey);
            if (stored.IsAvailable && stored.TryGet(out var kept) && kept is not null)
                Apply(kept);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Loading the parameters of Lab {Lab} failed", LabId);
        }
        finally
        {
            _loading = false;
        }

        Changed?.Invoke(null);
    }

    /// <summary>Keeps what is not the default in the Silo.</summary>
    public void Save()
    {
        try
        {
            var stored = Stored.At<Dictionary<string, string>>(StoreKey);
            if (!stored.IsAvailable) return;

            var kept = _all.Where(p => !p.IsDefault).ToDictionary(p => p.Name, p => p.ToJson());
            if (kept.Count == 0) stored.Delete();
            else stored.Set(kept);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Saving the parameters of Lab {Lab} failed", LabId);
        }
    }

    /// <summary>Puts every parameter back to its default (and keeps that).</summary>
    public void ResetToDefaults()
    {
        _loading = true;
        try
        {
            foreach (var p in _all) p.Reset();
        }
        finally
        {
            _loading = false;
        }

        Save();
        Changed?.Invoke(null);
    }

    /// <summary>The names of the presets kept in the Silo.</summary>
    public IReadOnlyList<string> PresetNames()
    {
        try
        {
            var store = Stored.DefaultStore();
            return store?.ListStored(PresetPrefix).Select(r => r.Key[PresetPrefix.Length..]).ToList() ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing the presets of Lab {Lab} failed", LabId);
            return [];
        }
    }

    /// <summary>Keeps every parameter as it is now under a name.</summary>
    public void SavePreset(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var stored = Stored.At<Dictionary<string, string>>(PresetPrefix + name.Trim());
        if (!stored.IsAvailable) throw new InvalidOperationException("Open a Silo to keep presets in.");

        stored.Set(_all.ToDictionary(p => p.Name, p => p.ToJson()));
    }

    /// <summary>Puts the parameters to a preset; false when there is none with the name.</summary>
    public bool LoadPreset(string name)
    {
        var stored = Stored.At<Dictionary<string, string>>(PresetPrefix + name.Trim());
        if (!stored.IsAvailable || !stored.TryGet(out var kept) || kept is null) return false;

        _loading = true;
        try
        {
            foreach (var p in _all) p.Reset();
            Apply(kept);
        }
        finally
        {
            _loading = false;
        }

        Save();
        Changed?.Invoke(null);
        return true;
    }

    /// <summary>Every parameter as it is now, as text (what the Presets pane keeps in the profile).</summary>
    internal Dictionary<string, string> Snapshot()
    {
        return _all.ToDictionary(p => p.Name, p => p.ToJson());
    }

    /// <summary>Puts the parameters to a snapshot: the ones it does not have go back to their defaults.</summary>
    internal void Restore(Dictionary<string, string> snapshot)
    {
        _loading = true;
        try
        {
            foreach (var p in _all) p.Reset();
            Apply(snapshot);
        }
        finally
        {
            _loading = false;
        }

        Save();
        Changed?.Invoke(null);
    }

    /// <summary>Forgets a preset; false when there is none with the name.</summary>
    public bool DeletePreset(string name)
    {
        return Stored.At<Dictionary<string, string>>(PresetPrefix + name.Trim()).Delete();
    }

    private void Apply(Dictionary<string, string> kept)
    {
        foreach (var (name, json) in kept)
            if (_byName.TryGetValue(name, out var p))
                p.FromJson(json);
    }

    private void OnParameterChanged(LabParameter parameter)
    {
        if (_loading) return;

        Save();
        Changed?.Invoke(parameter);
    }

    public LabParameterSet(string labId, IEnumerable<LabParameter> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labId);
        LabId = labId;

        foreach (var p in parameters)
        {
            _all.Add(p);
            _byName[p.Name] =  p;
            p.Changed       += (_, _) => OnParameterChanged(p);
        }

        if (App.Instance is { } app) app.ActiveSiloChanged += (_, _) => Load();
    }

    public IEnumerator<LabParameter> GetEnumerator()
    {
        return _all.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}