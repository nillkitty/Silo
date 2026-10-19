using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows.Markup;

namespace Silo.DbModel.Worlds;

/// <summary>
///     An item that is owned by a World (and can nest inside a World in XAML)
/// </summary>
public interface IWorldPart
{
    World World { get; set; }
}

public interface IWorldTypePart
{
    WorldType WorldType { get; set; }
}

/// <summary>
///     Defines a World
/// </summary>
public class World : ItemContainer<IWorldPart>, ISupportInitialize
{
    /// <inheritdoc />
    void ISupportInitialize.BeginInit()
    {
    }

    /// <inheritdoc />
    void ISupportInitialize.EndInit()
    {
        BindReferences();
    }

    /// <summary>
    ///     Points every <see cref="IRuntimeRef" /> held by this world's parts at this world, and sets their
    ///     <see cref="IWorldPart.World" />. XAML loading calls it; call it after assembling a World in code.
    /// </summary>
    public void BindReferences()
    {
        foreach (var part in Parts())
        {
            if (part is IWorldPart wp && wp.World is null) wp.World = this;

            foreach (var prop in part.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (typeof(IRuntimeRef).IsAssignableFrom(prop.PropertyType) && prop.GetIndexParameters().Length == 0 &&
                    prop.GetValue(part) is IRuntimeRef r)
                    r.Bind(Resolve);
        }
    }

    /// <summary>
    ///     Finds the part of type <paramref name="type" /> whose <c>Name</c> is <paramref name="name" />; the name may be
    ///     prefixed with this world's <see cref="Key" /> (<c>key:Name</c>).
    /// </summary>
    public object? Resolve(Type type, string name)
    {
        if (Key is { Length: > 0 } && name.StartsWith(Key + ":", StringComparison.Ordinal)) name = name[(Key.Length + 1)..];

        foreach (var part in Parts())
            if (type.IsInstanceOfType(part) &&
                part.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(part) is string n &&
                string.Equals(n, name, StringComparison.Ordinal))
                return part;

        return null;
    }

    /// <summary>
    ///     This world's parts and, recursively, their own items.
    /// </summary>
    private IEnumerable<object> Parts()
    {
        var pending = new Stack<object>(Items.OfType<object>().Reverse());
        while (pending.Count > 0)
        {
            var part = pending.Pop();
            yield return part;

            if (part.GetType().GetProperty(nameof(Items))?.GetValue(part) is System.Collections.IEnumerable children)
                foreach (var child in children.Cast<object>().Reverse())
                    if (child is not null)
                        pending.Push(child);
        }
    }

    /// <summary>Renamable, human-readable display name</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>World-level flags</summary>
    public WorldFlags Flags { get; set; }

    /// <summary>Primary key</summary>
    public int Id { get; set; }

    /// <summary>Unique key within the Silo for this world (cannot be changed -- used in its file name)</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The type name of the World provider</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The URI of the World root</summary>
    public Uri Uri { get; set; } = null!;
}

/// <summary>
///     Declares a type of object that a World owns
/// </summary>
public class WorldType : ItemContainer<IWorldTypePart>, IWorldPart
{
    /// <summary>Human-readable display name</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Type-level flags</summary>
    public WorldTypeFlags Flags { get; set; }

    /// <summary>Primary key</summary>
    public int Id { get; set; }

    /// <summary>
    ///     Optional list of auxiliary types to include
    /// </summary>
    public RuntimeRef<WorldType[]>? Include { get; set; }

    /// <summary>
    ///     Optional base type to inherit from
    /// </summary>
    public RuntimeRef<WorldType>? Inherits { get; set; }

    /// <summary>The name of the type in the Silo</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The native type name</summary>
    public string NativeName { get; set; } = string.Empty;

    /// <summary>The type name of the provider which handles this type</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>An optional URI to the schema object which defines this</summary>
    public Uri SchemaUri { get; set; } = null!;

    public string TableName { get; set; } = string.Empty;

    /// <summary>The world that owns the object type</summary>
    public World World { get; set; } = null!;
}

/// <summary>
///     A reference to another part, by name, that is resolved when it is first used (so it may point forward in XAML).
/// </summary>
public interface IRuntimeRef
{
    /// <summary>
    ///     The type referred to: the element type when the reference is to an array of parts.
    /// </summary>
    Type ElementType { get; }

    /// <summary>
    ///     Whether <see cref="Bind" /> has been called.
    /// </summary>
    bool IsBound { get; }

    /// <summary>
    ///     The name(s) referred to; several are separated by commas.
    /// </summary>
    string? Value { get; }

    /// <summary>
    ///     Sets how names are looked up: <c>(type, name) =&gt; part or null</c>.
    /// </summary>
    void Bind(Func<Type, string, object?> resolver);
}

/// <summary>
///     A by-name reference to a <typeparamref name="TTarget" /> (or, when <typeparamref name="TTarget" /> is an array, to
///     several parts named in a comma-separated list), resolved on first use by the owner that bound it (see
///     <see cref="World.BindReferences" />). In XAML it is written as the plain name, e.g. <c>Inherits="Contact"</c>.
/// </summary>
[ContentProperty(nameof(Value))]
[TypeConverter(typeof(RuntimeRefConverter))]
public class RuntimeRef<TTarget> : IRuntimeRef
{
    private Func<Type, string, object?>? _resolver;
    private bool                         _resolved;
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

    /// <inheritdoc />
    public void Bind(Func<Type, string, object?> resolver)
    {
        _resolver = resolver;
        _resolved = false;
    }

    public override string ToString()
    {
        return _value ?? string.Empty;
    }

    private TTarget Resolve()
    {
        if (_resolver is null)
            throw new InvalidOperationException(
                $"The reference to {typeof(TTarget).Name} '{_value}' is not bound; its owner must bind it (e.g. World.BindReferences) before it is used.");

        if (!typeof(TTarget).IsArray)
            return string.IsNullOrWhiteSpace(_value) ? default! : (TTarget)Find(_value.Trim());

        var names = (_value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var items = Array.CreateInstance(ElementType, names.Length);
        for (int i = 0; i < names.Length; i++) items.SetValue(Find(names[i]), i);
        return (TTarget)(object)items;
    }

    private object Find(string name)
    {
        return _resolver!(ElementType, name) ??
               throw new InvalidOperationException($"No {ElementType.Name} named '{name}' was found.");
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
}

/// <summary>
///     Converts a name (as written in XAML) to the <see cref="RuntimeRef{TTarget}" /> type of the property being set.
/// </summary>
public class RuntimeRefConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is not string s) return base.ConvertFrom(context, culture, value);

        var type = TargetType(context) ??
                   throw new NotSupportedException($"Cannot tell which RuntimeRef<> type '{s}' is being converted to.");

        var r = (IRuntimeRef)Activator.CreateInstance(type)!;
        type.GetProperty(nameof(IRuntimeRef.Value))!.SetValue(r, s);
        return r;
    }

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
    {
        return destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
    }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        return destinationType == typeof(string) && value is IRuntimeRef r
                   ? r.Value
                   : base.ConvertTo(context, culture, value, destinationType);
    }

    /// <summary>
    ///     The closed RuntimeRef type of the property being set, from the XAML service context.
    /// </summary>
    private static Type? TargetType(ITypeDescriptorContext? context)
    {
        var type = context?.PropertyDescriptor?.PropertyType;
        if (type is null && context?.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget target)
            type = target.TargetProperty switch
                   {
                       PropertyInfo p       => p.PropertyType,
                       DependencyProperty d => d.PropertyType,
                       _                    => null
                   };

        type = type is null ? null : Nullable.GetUnderlyingType(type) ?? type;
        return type is { IsGenericType: true } && type.GetGenericTypeDefinition() == typeof(RuntimeRef<>) ? type : null;
    }
}

/// <summary>
///     Declares a field for a World Type
/// </summary>
public class WorldField : Item, IWorldTypePart
{
    /// <summary>The type name of the .NET type used</summary>
    public string DataType { get; set; } = string.Empty;

    /// <summary>Field-level flags</summary>
    public WorldFieldFlags Flags { get; set; }

    /// <summary>Primary key</summary>
    public int Id { get; set; }


    /// <summary>The ordinal index of the field on the type</summary>
    public int Index { get; set; }

    /// <summary>Human-readable display name</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The native type name</summary>
    public string NativeType { get; set; } = string.Empty;

    /// <summary>Optionally URI to a schema element which defines this</summary>
    public Uri SchemaUri { get; set; } = null!;

    /// <summary>The world that owns the field</summary>
    public World World { get; set; } = null!;

    /// <summary>The type which owns the field</summary>
    public WorldType WorldType { get; set; } = null!;
}

public class FieldMapping
{
    public string            Conversion  { get; set; } = string.Empty;
    public WorldField        Field       { get; set; } = null!;
    public FieldMappingFlags Flags       { get; set; }
    public int               Id          { get; set; }
    public string            MvFieldName { get; set; } = string.Empty;
    public string            MvTypeName  { get; set; } = string.Empty;
}

/// <summary>
///     Values indicating how a field mapping is to behave upon synchronization
/// </summary>
[Flags]
public enum FieldMappingFlags
{
    /// <summary>
    ///     Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    ///     Value will be imported into the Metaverse from
    ///     the world value
    /// </summary>
    Import = 1,

    /// <summary>
    ///     Value will be exported into the World from the
    ///     Metaverse value
    /// </summary>
    Export = 2,

    /// <summary>
    ///     Allow NULL to flow from World to Metaverse
    /// </summary>
    ImportNulls = 4,

    /// <summary>
    ///     Allow NULL to flow from Metaverse to World
    /// </summary>
    ExportNulls = 8,

    /// <summary>
    ///     If set on a multivalued field, each value
    ///     is treated as a keyed sub-field and thus each unique
    ///     element is tracked for adds, removes, and changes.
    /// </summary>
    Reconciled = 0x10
}

/// <summary>
///     Defines a mapping between a <see cref="WorldType" /> and a meta-type (Metaverse type).
/// </summary>
public class WorldMapping : Item, IWorldTypePart
{
    /// <summary>Primary Key</summary>
    public int Id { get; set; }

    /// <summary>Optional rules for auto-joining objects to connectors</summary>
    public JoinRules? Joining { get; set; }

    /// <summary>The meta-type name targeted</summary>
    public string MvTypeName { get; set; } = string.Empty;

    /// <summary>Relative priority used during synchronization</summary>
    public int Priority { get; set; }

    /// <summary>Optional rules for auto-creating connectors for each object</summary>
    public ProjRules? Projection { get; set; }

    /// <summary>Optional rules for custom provisioning</summary>
    public ProvRules? Provisioning { get; set; }

    /// <summary>The world owning the type being mapped</summary>
    public World World { get; set; } = null!;

    /// <summary>The world type being mapped</summary>
    public WorldType WorldType { get; set; } = null!;
}

/// <summary>
///     Defines a synchronization job profile upon which one or all WorldTypes from a World are synchronized
/// </summary>
public class WorldSync : Item, IWorldPart
{
    /// <summary>Specifies how instances are imported</summary>
    public ImportType ImportType { get; set; }

    /// <summary>Relative priority used during synchronization</summary>
    public int Priority { get; set; }

    /// <summary>Specifies the interval upon which this should take place</summary>
    public TimeSpan SyncInterval { get; set; }

    /// <summary>Specifies how instances are synchronized</summary>
    public SyncType SyncType { get; set; }

    /// <summary>Specifies a specific WorldType to sync, if null all mapped types are synced.</summary>
    public WorldType? WorldType { get; set; }

    /// <summary>Specifies the World which will be synced</summary>
    public World World { get; set; } = null!;
}

/// <summary>
///     Values indicating how an import job is to be handled
/// </summary>
public enum ImportType
{
    /// <summary>
    ///     Do not import any data, read it live as it is required
    /// </summary>
    None,

    /// <summary>
    ///     Only cache data locally for performance; do not stage object instance in
    ///     the world database.
    /// </summary>
    Cache,

    /// <summary>
    ///     Cache the data, but do so in the temp database.
    /// </summary>
    CacheTemp,

    /// <summary>
    ///     Import changes, adds, and deletes since the last import (if possible);
    ///     will be upgraded to <see cref="Full" /> if a delta import is not possible.
    /// </summary>
    Delta,

    /// <summary>
    ///     Performs a full import of all qualified objects.
    /// </summary>
    Full
}

/// <summary>
///     Values indicating how imported (or loaded) data is synchronized from
///     world space into the metaverse.
/// </summary>
[Flags]
public enum SyncType
{
    /// <summary>
    ///     The data is not synchronized at all
    /// </summary>
    NoSync = 0,

    /// <summary>
    ///     Sync objects on this sync cycle, by default delta (only changed objects)
    /// </summary>
    Sync = 1,

    /// <summary>
    ///     A full synchronization is forced across all objects
    /// </summary>
    Full = 2,

    /// <summary>
    ///     Whether to enable Provisioning
    /// </summary>
    Provision = 4,

    /// <summary>
    ///     Whether to enable Joins
    /// </summary>
    Join = 8,

    /// <summary>
    ///     Whether to enable Projection
    /// </summary>
    Project = 0x10,

    /// <summary>
    ///     Whether to enable metaverse attribute flow
    /// </summary>
    FlowToMv = 0x20,
    Default  = Sync | Provision | Join | Project | FlowToMv
}

[Flags]
public enum WorldFlags
{
    /// <summary>
    ///     Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    ///     When set, data from this world type will never be mirrored in
    ///     the world db.
    /// </summary>
    NeverMirror = 1,

    /// <summary>
    ///     When set, data from this world type will never be mirrored in
    ///     the world db.
    /// </summary>
    NeverCache = 2,

    /// <summary>
    ///     Indicates that we are authoritative (e.g. this is the true data,
    ///     stored in the Silo -- it's not from someplace else)
    /// </summary>
    Authority = 4
}

[Flags]
public enum WorldTypeFlags
{
    /// <summary>
    ///     Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    ///     When set, data from this world type will never be mirrored in
    ///     the world db.
    /// </summary>
    NeverMirror = 1,

    /// <summary>
    ///     When set, data from this world type will never be cached in the
    ///     world db.
    /// </summary>
    NeverCache = 2,

    /// <summary>
    ///     Flags this (world's view of this data) as the authoritative source
    ///     for the meta-type -- meaning, it is preferred over any other source
    ///     contributing the same instance (object by key) of the same meta-type
    /// </summary>
    AuthoritativeSource = 4,

    /// <summary>
    ///     When set, cached data from this world type will be cached in the
    ///     temp db (which is subject to pruning significantly more frequently).
    /// </summary>
    CacheInTemp = 8,

    /// <summary>
    ///     Flags this world type as a mapping type, which is transient and only
    ///     maps one type to another (usually many-to-many);
    /// </summary>
    Mapping = 0x10
}

[Flags]
public enum WorldFieldFlags
{
    /// <summary>
    ///     Default behavior
    /// </summary>
    None = 0,

    /// <summary>
    ///     The source uses this field as the primary key for the type
    /// </summary>
    Key = 1,

    /// <summary>
    ///     The source indexes this field
    /// </summary>
    Index = 2,

    /// <summary>
    ///     The source includes this field in ANR
    /// </summary>
    Anr = 4,

    /// <summary>
    ///     This field is a reference within the same world
    /// </summary>
    Reference = 8,

    /// <summary>
    ///     This field may contain a referral to an external system/URI;
    ///     can only be combined with <see cref="Reference" />
    /// </summary>
    Referral = 0x10,

    /// <summary>
    ///     This field is the creation date of the object(s)
    /// </summary>
    Creation = 0x20,

    /// <summary>
    ///     This field is the last modified date of the object(s)
    /// </summary>
    ModifyDate = 0x40,

    /// <summary>
    ///     This field contains the absolute path, URI, distinguished name,
    ///     etc. for the object (full native key)
    /// </summary>
    AbsolutePath = 0x80,

    /// <summary>
    ///     This field contains the relative path fragment (name, CN, etc.)
    ///     for the object (partial native key)
    /// </summary>
    RelativePath = 0x100,

    /// <summary>
    ///     The value for this field may be NULL
    /// </summary>
    Nullable = 0x200,

    /// <summary>
    ///     The value for this field can have multiple elements.
    /// </summary>
    Multivalue = 0x400,

    /// <summary>
    ///     This field is required and cannot be null or empty
    /// </summary>
    Required = 0x800,

    /// <summary>
    ///     The field contains the row/item identity (auto-assigned)
    /// </summary>
    Identity = 0x1000,

    /// <summary>
    ///     The value is provided by the database / evaluated
    /// </summary>
    Computed = 0x2000
}