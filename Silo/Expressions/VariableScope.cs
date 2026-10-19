namespace Silo.Expressions;

/// <summary>Where the variables of an expression come from.</summary>
public interface IVariableScope
{
    /// <summary>Looks a variable up. <paramref name="prefix" /> is the namespace written before the colon (<c>your</c> in <c>your:blue</c>), or null for a plain name.</summary>
    /// <returns>True if the variable exists (its value can still be null); false if there is no such variable.</returns>
    bool TryGetVariable(string? prefix, string name, out object? value);
}

/// <summary>
///     A set of variables in memory, plus namespaces that other scopes answer for. Names and prefixes are not case sensitive. A scope can have a parent that
///     is asked about what this one does not know.
/// </summary>
public sealed class VariableScope : IVariableScope
{
    private readonly Dictionary<string, object?>         _plain    = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IVariableScope>  _prefixes = new(StringComparer.OrdinalIgnoreCase);
    private readonly IVariableScope?                     _parent;

    /// <summary>An empty scope; when <paramref name="parent" /> is given it answers for what this scope does not know.</summary>
    public VariableScope(IVariableScope? parent = null)
    {
        _parent = parent;
    }

    /// <summary>Sets a plain variable (<c>${name}</c>).</summary>
    public VariableScope Set(string name, object? value)
    {
        _plain[name] = value;
        return this;
    }

    /// <summary>Hands a whole namespace over to another scope: <c>${your:...}</c> asks <paramref name="scope" />, which is given the part after the colon.</summary>
    public VariableScope AddNamespace(string prefix, IVariableScope scope)
    {
        _prefixes[prefix] = scope;
        return this;
    }

    /// <inheritdoc />
    public bool TryGetVariable(string? prefix, string name, out object? value)
    {
        if (prefix is null)
        {
            if (_plain.TryGetValue(name, out value)) return true;
        }
        else if (_prefixes.TryGetValue(prefix, out var scope) && scope.TryGetVariable(null, name, out value))
        {
            return true;
        }

        if (_parent is not null) return _parent.TryGetVariable(prefix, name, out value);

        value = null;
        return false;
    }
}
