using System.ComponentModel;
using System.Windows.Markup;

namespace Silo.DbModel.Worlds;

/// <summary>
///     A by-name reference to a <typeparamref name="TTarget" /> (or, when <typeparamref name="TTarget" /> is an array, to
///     several parts named in a comma-separated list), resolved on first use by the owner that bound it (see
///     <see cref="World.BindReferences" />). In XAML it is written as the plain name, e.g. <c>Inherits="Contact"</c>.
/// </summary>
[ContentProperty(nameof(Value))]
[TypeConverter(typeof(RuntimeRefConverter))]
public class RuntimeRef<TTarget> : IRuntimeRef
{
    private bool                         _resolved;
    private Func<Type, string, object?>? _resolver;
    private TTarget?                     _target;
    private string?                      _value;

    /// <summary>
    ///     The referenced part, resolving it on first use.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not bound yet, or a name is not found.</exception>
    public TTarget Target
    {
        get
        {
            if (!_resolved)
            {
                _target   = Resolve();
                _resolved = true;
            }

            return _target!;
        }
    }

    /// <inheritdoc />
    public Type ElementType => typeof(TTarget).IsArray ? typeof(TTarget).GetElementType()! : typeof(TTarget);

    /// <inheritdoc />
    public bool IsBound => _resolver is not null;

    /// <inheritdoc />
    public string? Value
    {
        get => _value;
        set
        {
            _value    = value;
            _resolved = false;
        }
    }

    public override string ToString()
    {
        return _value ?? string.Empty;
    }

    private TTarget Resolve()
    {
        if (_resolver is null)
            throw new
                InvalidOperationException($"The reference to {typeof(TTarget).Name} '{_value}' is not bound; its owner must bind it (e.g. World.BindReferences) before it is used.");

        if (!typeof(TTarget).IsArray)
            return string.IsNullOrWhiteSpace(_value) ? default! : (TTarget)Find(_value.Trim());

        var names = (_value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var items = Array.CreateInstance(ElementType, names.Length);
        for (int i = 0; i < names.Length; i++) items.SetValue(Find(names[i]), i);
        return (TTarget)(object)items;
    }

    private object Find(string name)
    {
        return _resolver!(ElementType, name) ?? throw new InvalidOperationException($"No {ElementType.Name} named '{name}' was found.");
    }

    /// <summary>
    ///     The referenced part (see <see cref="Target" />); null when <paramref name="r" /> is null.
    /// </summary>
    public static implicit operator TTarget(RuntimeRef<TTarget> r)
    {
        return r is null ? default! : r.Target;
    }

    public static implicit operator RuntimeRef<TTarget>(string s)
    {
        return new RuntimeRef<TTarget> { Value = s.Required() };
    }

    /// <inheritdoc />
    public void Bind(Func<Type, string, object?> resolver)
    {
        _resolver = resolver;
        _resolved = false;
    }
}