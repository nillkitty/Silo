namespace Silo.Providers.Data.Shape;

public interface IType
{
    /// <summary>
    ///     Gets a value indicating the typing method which this type represents
    /// </summary>
    DataKind Kind { get; }

    /// <summary>
    ///     Gets the unqualified name of the type
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Gets the private URI of the namespace the type exists in
    /// </summary>
    Uri Namespace { get; }

    /// <summary>
    ///     Gets the Silo prefix (e.g. 'clr') required to reference the type
    /// </summary>
    string Prefix { get; }

    /// <summary>
    ///     Gets a value indicating the shape/arity of the type
    /// </summary>
    DataShape Shape { get; }

    /// <summary>
    ///     Gets the private URI of the type itself.
    /// </summary>
    Uri Uri { get; }
}

public enum DataKind
{
    Unknown,

    /// <summary>
    ///     The type has one or more unbound type arguments and is not a usable nominal type until
    ///     the arguments are supplied
    /// </summary>
    Open,

    /// <summary>
    ///     The data is typed by name, and instances cannot be compared, replaced, or used interchangably
    ///     with data with a different nominal type (unless covariant)
    /// </summary>
    Nominal,

    /// <summary>
    ///     The data is not typed
    /// </summary>
    Dynamic,

    /// <summary>
    ///     The data is a string accompanied by a <see cref="ISyntax" /> syntax specifier that can
    ///     (de)serialize between the string version and a more useful object type
    /// </summary>
    Syntax,

    /// <summary>
    ///     The type represents data defined by a structural definition (duck typing)
    /// </summary>
    Structural
}

public enum DataShape
{
    Unknown,

    /// <summary>
    ///     The shape this type represents is dynamic and changes from value to value
    /// </summary>
    Dynamic,

    /// <summary>
    ///     The type represents scalar (single) values (1x1x1)
    /// </summary>
    Scalar,

    /// <summary>
    ///     The type represents columnar (tuples/structs) values or a row in a table (1xNx1)
    /// </summary>
    Columnar,

    /// <summary>
    ///     The type represents an array/list-like vector (1x1xN)
    /// </summary>
    Linear,

    /// <summary>
    ///     The type represents tabular data (1xNxN) -- many instances of columnar structures
    /// </summary>
    Tabular,

    /// <summary>
    ///     The type represents a data set which includes one or more tabular sources
    /// </summary>
    Multitabular,

    /// <summary>
    ///     The type represents an object with zero or more key/value properties
    /// </summary>
    Object,

    /// <summary>
    ///     The type represents an subset of objects which contain other objects (i.e. are enumerable)
    /// </summary>
    Container
}