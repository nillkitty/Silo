using System.ComponentModel;
using Silo.DbModel.Global;

namespace Silo.DbModel.Temp;

/// <summary>
/// Tracks a user variable
/// </summary>
public class Scalar : TimedUserEntity
{
    public int    Id    { get; set; }
    public string Name  { get; set; } = string.Empty;
    public string Type  { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Tracks a named table variable
/// </summary>
public class Table : TimedUserEntity
{
    public int    Id   { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Tracks a named function variable
/// </summary>
public class Function : TimedUserEntity
{
    public int    Id   { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Tracks a named alias
/// </summary>
public class Synonym : TimedUserEntity
{
    public int Id     { get; set; }
    public Uri Target { get; set; } = null!;
}

/// <summary>
/// Tracks data pasted in
/// </summary>
public class Pasted : TimedUserEntity
{
    public int    Id     { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Type   { get; set; } = string.Empty;

    [TypeConverter(typeof(ByteArrayConverter))]
    public byte[] Data { get; set; } = [];
}

/// <summary>
/// Tracks push/pop of the current Repl location (as a Uri)
/// </summary>
public class ReplStack : TimedUserEntity
{
    public int Id  { get; set; }
    public Uri Uri { get; set; } = null!;
}

/// <summary>
/// Tracks files which were previously open
/// </summary>
public class OpenFile : TimedUserEntity
{
    public int    Id           { get; set; }
    public Uri    FileUri      { get; set; } = null!;
    public string ProviderType { get; set; } = string.Empty;
}

/// <summary>
/// Tracks most recently used open files
/// </summary>
public class OpenFileMru : TimedUserEntity
{
    public int Id      { get; set; }
    public Uri FileUri { get; set; } = null!;
}

/// <summary>
/// Tracks most recently used variable names
/// </summary>
public class VariableMru : TimedUserEntity
{
    public int    Id           { get; set; }
    public string VariableName { get; set; } = string.Empty;
}

/// <summary>
/// Tracks most recently loaded assemblies
/// </summary>
public class AssemblyMru : TimedUserEntity
{
    public int    Id           { get; set; }
    public string AssemblyName { get; set; } = string.Empty;
    public Uri    SourceUri    { get; set; } = null!;
}

/// <summary>
/// Tracks most recently used colors
/// </summary>
public class ColorMru : TimedUserEntity
{
    public int Id    { get; set; }
    public int Red   { get; set; }
    public int Blue  { get; set; }
    public int Green { get; set; }
    public int Alpha { get; set; }
}

/// <summary>
/// Tracks most recently used text snippets for various functions
/// </summary>
public class TextMru : TimedUserEntity
{
    public int    Id   { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Tracks most recently referenced people
/// </summary>
public class PeopleMru : TimedUserEntity
{
    public int    Id        { get; set; }
    public string PersonEid { get; set; } = string.Empty;
}