using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Silo.Contracts.Providers.Data;
using Silo.Expressions;
using Silo.Unicode;

namespace Silo.Storage;

/// <summary>A constant of the person's own, <c>your:green</c>: a name, and the characters it stands for.</summary>
/// <param name="Name">The name after <c>your:</c>.</param>
/// <param name="CodePoints">The characters; one for a constant made from a single character.</param>
/// <param name="IsList">True for a constant that was saved as a list of characters (even a list of one).</param>
/// <param name="Modified">When it was last saved (UTC).</param>
public sealed record ConstantValue(string Name, IReadOnlyList<CodePoint> CodePoints, bool IsList, DateTime Modified)
{
    /// <summary>The characters written one after the other: what <c>${your:name}</c> comes to.</summary>
    public string Text => CodePoint.ToText(CodePoints);

    /// <summary>The name as it is used in an expression: <c>your:green</c>.</summary>
    public string FullName => YourConstants.Prefix + ":" + Name;
}

/// <summary>
///     The person's own constants (<c>your:green='🟢'</c>, <c>your:mychars='▶√®™▼▽'</c>), kept in the personal part of the Silo as <c>Stored&lt;CodePoint&gt;</c> and
///     <c>Stored&lt;CodePoint[]&gt;</c>. They are variables of the <c>your</c> namespace for <see cref="Template" /> and expressions: see <see cref="AsScope" />.
/// </summary>
public sealed class YourConstants
{
    /// <summary>The namespace the constants are in: <c>your:name</c>.</summary>
    public const string Prefix = "your";

    private static readonly Regex NamePattern = new("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant);

    private readonly Func<IStoredStore?> _store;

    /// <param name="store">Where the personal part of the Silo is, asked each time; null for nowhere.</param>
    public YourConstants(Func<IStoredStore?> store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>The constants of the active Silo.</summary>
    public static YourConstants Default { get; } = new(static () => Stored.DefaultStore());

    /// <summary>Whether this can be the name of a constant: a letter or underscore, then letters, digits and underscores (at most 64).</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    /// <summary>What a constant is stored under: <c>your:green</c>.</summary>
    public static string KeyFor(string name) => Prefix + ":" + name;

    /// <summary>Every constant, ordered by name. Items that are stored under <c>your:</c> but cannot be read are left out.</summary>
    public IReadOnlyList<ConstantValue> List()
    {
        var store = _store();
        if (store is null) return [];

        var result = new List<ConstantValue>();
        foreach (var record in store.ListStored(Prefix + ":", StoredScope.Personal))
            if (TryRead(record, out var value))
                result.Add(value!);

        return result;
    }

    /// <summary>The constant with the name (any case); null if there is none.</summary>
    public ConstantValue? Get(string name)
    {
        if (!IsValidName(name)) return null;

        var record = _store()?.GetStored(KeyFor(name), StoredScope.Personal);
        return record is not null && TryRead(record, out var value) ? value : null;
    }

    /// <summary>Saves a constant for one character (<c>Stored&lt;CodePoint&gt;</c>), replacing any constant of that name.</summary>
    /// <exception cref="ArgumentException">The name cannot be a constant's.</exception>
    /// <exception cref="InvalidOperationException">No Silo is open.</exception>
    public void SetCodePoint(string name, CodePoint codePoint) => OneStored(name).Set(codePoint);

    /// <summary>Saves a constant for a list of characters (<c>Stored&lt;CodePoint[]&gt;</c>), replacing any constant of that name.</summary>
    /// <exception cref="ArgumentException">The name cannot be a constant's, or there are no characters.</exception>
    /// <exception cref="InvalidOperationException">No Silo is open.</exception>
    public void SetCodePoints(string name, IEnumerable<CodePoint> codePoints)
    {
        var list = codePoints.ToArray();
        if (list.Length == 0) throw new ArgumentException("A constant needs at least one character.", nameof(codePoints));
        ManyStored(name).Set(list);
    }

    /// <summary>Removes the constant; false if there was none.</summary>
    public bool Delete(string name) => IsValidName(name) && OneStored(name).Delete();

    /// <summary>Whether a Silo is open to keep constants in.</summary>
    public bool IsAvailable => _store() is not null;

    /// <summary>
    ///     The constants as variables of an expression: <c>${your:green}</c> is the character, <c>${your:mychars}</c> the list written out as text. The
    ///     scope reads the Silo each time, so a constant saved a moment ago is there. Names are not case sensitive.
    /// </summary>
    /// <param name="parent">Another scope for the variables that are not <c>your:</c> ones; null for none.</param>
    public VariableScope AsScope(IVariableScope? parent = null) => new VariableScope(parent).AddNamespace(Prefix, new Namespace(this));

    private Stored<CodePoint> OneStored(string name) => new(Checked(name), StoredScope.Personal, _store);

    private Stored<CodePoint[]> ManyStored(string name) => new(Checked(name), StoredScope.Personal, _store);

    private static string Checked(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException($"\"{name}\" cannot be the name of a constant: start with a letter or underscore, then letters, digits and underscores (up to 64).", nameof(name));
        return KeyFor(name);
    }

    private static bool TryRead(StoredRecord record, out ConstantValue? value)
    {
        value = null;
        if (record.Key.Length <= Prefix.Length + 1) return false;

        string name = record.Key[(Prefix.Length + 1)..];
        if (!IsValidName(name)) return false;

        try
        {
            switch (JToken.Parse(record.Data))
            {
                case JArray:
                {
                    var list = StoredCodec.Deserialize<CodePoint[]>(record.Data);
                    if (list is not { Length: > 0 }) return false;
                    value = new ConstantValue(name, list, true, record.Modified);
                    return true;
                }
                case JValue { Type: JTokenType.String or JTokenType.Integer }:
                    value = new ConstantValue(name, [StoredCodec.Deserialize<CodePoint>(record.Data)], false, record.Modified);
                    return true;
                default:
                    return false;
            }
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return false;
        }
    }

    private sealed class Namespace(YourConstants owner) : IVariableScope
    {
        public bool TryGetVariable(string? prefix, string name, out object? value)
        {
            value = null;
            if (prefix is not null && !prefix.Equals(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

            var constant = owner.Get(name);
            if (constant is null) return false;

            value = constant.Text;
            return true;
        }
    }
}
