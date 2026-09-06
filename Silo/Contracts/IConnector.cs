namespace Silo.Connectors;

public interface IConnector
{
    bool                IsConnected { get; }
    Exception           LastError   { get; }
    ConnectorSetupFlags Flags       { get; }
}