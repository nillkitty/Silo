using System.IO;
using Microsoft.Win32;
using Silo.Extensions;
using Silo.Providers;
using Silo.Ui.Windows.Connecting;
using Telefrag.DI;

namespace Silo.Connectors.File;

/// <summary>
///     The filesystem connector allows connections to local and remote UNC files and
///     directories.
/// </summary>
[Singleton(typeof(IConnector))]
public class FileConnector : ConnectorBase<FileModel, FileState>, IUriConnector, IAsyncConnector
{
    public string? Description => "Folders and files on this computer, and shares on the network";

    public IConnectorSetup CreateSetup(ConnectorSetupContext context)
    {
        return new UriFieldSetup(this, text => UriRules.Check(text, [this], UriRules.AnyScope), "Folder or file",
                                 "Examples:  C:\\Data    D:\\Reports\\2026.xlsx    \\\\server\\share\\folder", pickers:
                                 [
                                     new
                                         UriPicker("Browse folder...",
                                                   PickFolder),
                                     new
                                         UriPicker("Browse file...",
                                                   PickFile)
                                 ]);
    }

    /// <summary>
    ///     Looks for the folder or file (may block for as long as the file system takes to answer); null if it is not
    ///     there.
    /// </summary>
    private static FileSystemInfo? Probe(string path)
    {
        if (Directory.Exists(path)) return new DirectoryInfo(path);

        var file = new FileInfo(path);
        return file.Exists ? file : null;
    }

    private static bool IsOnLocalDrive(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;

            return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? PickFolder(Window? owner)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder" };
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) is true ? dialog.FolderName : null;
    }

    private static string? PickFile(Window? owner)
    {
        var dialog = new OpenFileDialog { Title = "Choose a file", CheckFileExists = true };
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) is true ? dialog.FileName : null;
    }

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

    public FileConnector()
    {
        Flags = ConnectorSetupFlags.Connectionless;
    }

    /// <summary>
    ///     The dialog's way in: the path is looked at on a thread-pool thread, so a share that does not answer cannot freeze
    ///     the app. Cancelling stops the wait at once; the file system call itself cannot be interrupted, so it is left to
    ///     time out on its own thread. The path must exist (unlike <see cref="OnCreateConnection" />, which does not check).
    /// </summary>
    public async Task<IConnection> CreateConnectionAsync(ConnectionRequest crq, CancellationToken cancel)
    {
        crq.Required();
        if (crq.RequestedUri is not { } uri) throw new NotSupportedException("The connection request did not contain a target URI");
        if (!uri.IsFile) throw new NotSupportedException("The file connector only accepts file: URIs");

        string path = uri.LocalPath;
        var    info = await Task.Run(() => Probe(path), CancellationToken.None).WaitAsync(cancel);

        // thrown here, not on the pool thread: an exception that leaves a Task.Run thread unhandled makes the debugger stop
        // as "user-unhandled" even though the dialog catches it
        if (info is null) throw new FileNotFoundException($"\"{path}\" was not found, or is not reachable.", path);

        return FileConnection.FromFsi(this, info, crq);
    }

    public UriSchemeScope HandlesScheme(string scheme)
    {
        return string.Equals(scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
                   ? UriSchemeScope.WellKnown
                   : UriSchemeScope.Unknown;
    }

    /// <summary>
    ///     The path must exist. That is only checked for local drives: asking a share that is not answering can take half a
    ///     minute, and this is asked while the person types.
    /// </summary>
    public string? Validate(Uri uri)
    {
        if (!uri.IsFile) return "The file connector only takes file: URIs, or a path such as C:\\Data.";

        string path = uri.LocalPath;
        if (!IsOnLocalDrive(path)) return null;

        return Directory.Exists(path) || new FileInfo(path).Exists ? null : $"\"{path}\" does not exist.";
    }
}

public abstract record FileState(bool IsAvailable, bool IsError, string StateText) : IConnectionState
{
    public Exception? Exception   { get; private set; }
    public bool       IsConnected => IsAvailable;
    public bool       IsDisposed  => false;
    public bool       IsFailed    => IsError;

    public override string ToString()
    {
        return StateText;
    }

    protected FileState(Exception ex) : this(false, true, ex.GetType().ShortDisplayName())
    {
        Exception = ex;
    }
}

public record Available(FileAttributes Attributes) : FileState(true, false, "Available");

public record NoAccess() : FileState(false, true, "Inaccessible");

public record Failed(Exception ex) : FileState(ex);

public record Ignored() : FileState(false, false, "Ignored");