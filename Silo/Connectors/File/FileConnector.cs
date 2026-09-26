using System.IO;

namespace Silo.Connectors.File;

/// <summary>
///     The filesystem connector allows connections to local and remote UNC files and
///     directories.
/// </summary>
public class FileConnector : ConnectorBase
{
    protected override IConnection OnCreateConnection(ConnectionRequest crq)
    {
        crq.Required();
        if (crq.RequestedUri is Uri u)
        {
            if (!u.IsFile)
                throw new NotSupportedException("The file connector only accepts file: URIs");

            var f = u.LocalPath;
            return FileConnection.FromPath(this, f, crq);
        }

        throw new NotSupportedException("The connection request did not contain a target URI");
    }

    public override Type GetStateType()
    {
        return typeof(FileState);
    }
}

public abstract record FileState(bool IsAvailable, string StateText);

public record Available(FileAttributes Attributes) : FileState(true, "Available");

public record NoAccess() : FileState(false, "Inaccessible");

public record Ignored() : FileState(false, "Ignored");