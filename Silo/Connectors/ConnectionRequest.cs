using Silo.DbModel.Global;

namespace Silo.Connectors;

public class ConnectionRequest
{
    public SiloNode? ParentNode       { get; set; }
    public Uri?      RequestedUri     { get; set; }
    public string?   SerializedConfig { get; set; }
}