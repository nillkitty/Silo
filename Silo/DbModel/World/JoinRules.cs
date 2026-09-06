using System;
using System.Collections.Generic;
using System.Text;

namespace Silo.DbModel.World;

/// <summary>
/// Defines how auto-joining is configured between a WorldType and a metaverse type.
/// </summary>
public class JoinRules
{
    public string?     MvFieldName  { get; set; }
    public string?     ProviderName { get; set; }
    public WorldField? WorldField   { get; set; }
    public JoinType    Type         { get; set; }
}

/// <summary>
/// Values indicating how auto-join is configured
/// </summary>
public enum JoinType
{
    /// <summary>
    /// No auto-joining is configured
    /// </summary>
    NoJoin,

    /// <summary>
    /// The object will join a metaverse object if the values of the metaverse field and
    /// world field are identical
    /// </summary>
    FieldValueEquals,

    /// <summary>
    /// The object will join a metaverse object if the values of the metaverse field and
    /// world field are references which point to the same object
    /// </summary>
    FieldReferenceEquals,

    /// <summary>
    /// The object will join a metaverse object if the values of the metaverse field and
    /// world field are references and point to the same object, or the metaverse object
    /// is a parent of the referenced item('s metaverse connector).
    /// </summary>
    FieldReferenceEqualsOrParent,

    /// <summary>
    /// The object will join a metaverse object if the values of the metaverse field and
    /// world field are references which point to the same object, or the metaverse object
    /// is a child of the referenced item('s metaverse connector).
    /// </summary>
    FieldReferenceEqualsOrChild,

    /// <summary>
    /// A join provider is being used to handle the join.
    /// </summary>
    Extension
}