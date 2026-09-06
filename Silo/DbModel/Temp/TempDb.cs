using System;
using System.Collections.Generic;
using System.Text;
using Silo.DbModel.Global;

namespace Silo.DbModel.Temp;

public class TempDb
{
}

/// <summary>
/// Tracks a user variable
/// </summary>
public record Scalar(int Id, User User, string Name, string Type, string Value)
    : TimedUserEntity(User);

/// <summary>
/// Tracks a named table variable
/// </summary>
public record Table(int Id, User User, string Name) : TimedUserEntity(User);

/// <summary>
/// Tracks a named function variable
/// </summary>
public record Function(int Id, User User, string Name) : TimedUserEntity(User);

/// <summary>
/// Tracks a named alias
/// </summary>
public record Synonym(int Id, User User, Uri Target) : TimedUserEntity(User);

/// <summary>
/// Tracks data pasted in
/// </summary>
public record Pasted(int Id, User User, string Source, string Type, byte[] Data)
    : TimedUserEntity(User);

/// <summary>
/// Tracks push/pop of the current Repl location (as a Uri)
/// </summary>
public record ReplStack(int Id, User User, Uri Uri) : TimedUserEntity(User);

/// <summary>
/// Tracks files which were previously open
/// </summary>
public record OpenFile(int Id, User User, Uri FileUri, string ProviderType)
    : TimedUserEntity(User);

/// <summary>
/// Tracks most recently used open files
/// </summary>
public record OpenFileMru(int Id, User User, Uri FileUri)
    : TimedUserEntity(User);

/// <summary>
/// Tracks most recently used variable names
/// </summary>
public record VariableMru(int Id, User User, string VariableName)
    : TimedUserEntity(User);

/// <summary>
/// Tracks most recently loaded assemblies
/// </summary>
public record AssemblyMru(int Id, User User, string AssemblyName, Uri SourceUri)
    : TimedUserEntity(User);

/// <summary>
/// Tracks most recently used colors
/// </summary>
public record ColorMru(
    int  Id,
    User User,
    int  Red,
    int  Blue,
    int  Green,
    int  Alpha) : TimedUserEntity(User);

/// <summary>
/// Tracks most recently used text snippets for various functions
/// </summary>
public record TextMru(int Id, string Kind, User User, string Text)
    : TimedUserEntity(User);

/// <summary>
/// Tracks most recently referenced people
/// </summary>
public record PeopleMru(int Id, User User, string PersonEid)
    : TimedUserEntity(User);