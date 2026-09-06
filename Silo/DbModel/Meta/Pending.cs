using Newtonsoft.Json.Linq;

namespace Silo.DbModel.Meta;

/// <summary>
/// Defines a pending delete for a connected object
/// </summary>
public class PendingDelete
{
    public bool Delete { get; set; }
}

/// <summary>
/// Defines a pending update for a connected object
/// </summary>
public class PendingUpdate
{
    public JObject? Properties { get; set; }
}

/// <summary>
/// Defines a pending creation for a newly provisioned object
/// </summary>
public class PendingCreate
{
    public string?  ContainerKey { get; set; }
    public string?  NativeType   { get; set; }
    public string?  AbsolutePath { get; set; }
    public string?  PartialPath  { get; set; }
    public JObject? Properties   { get; set; }
}