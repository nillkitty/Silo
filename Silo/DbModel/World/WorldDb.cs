using System.Collections;
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
            if (type.IsInstanceOfType(part)                                                                                 &&
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

            if (part.GetType().GetProperty(nameof(Items))?.GetValue(part) is IEnumerable children)
                foreach (var child in children.Cast<object>().Reverse())
                    if (child is not null)
                        pending.Push(child);
        }
    }

    /// <inheritdoc />
    void ISupportInitialize.BeginInit()
    {
    }

    /// <inheritdoc />
    void ISupportInitialize.EndInit()
    {
        BindReferences();
    }
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