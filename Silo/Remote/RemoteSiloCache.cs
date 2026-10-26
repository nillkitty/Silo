using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Serilog;
using Silo.Providers.Format;

namespace Silo.Remote;

/// <summary>What is remembered about the last sync of one remote Silo.</summary>
public sealed class RemoteState
{
    public string?   Url          { get; set; }
    public string?   ETag         { get; set; }
    public DateTime? LastModified { get; set; }
    public DateTime? SyncedUtc    { get; set; }

    /// <summary>The local file holds saved changes that were not uploaded.</summary>
    public bool Unpushed { get; set; }
}

/// <summary>
///     The on-disk cache of one remote Silo: <c>%LOCALAPPDATA%\Silo\Remote\&lt;hash of URL&gt;\&lt;name&gt;.silo</c> plus a
///     small <c>remote.json</c> with the sync state (ETag, last sync, whether a save is waiting to be uploaded). The cached
///     file is an ordinary local Silo -- the provider opens it, working-copies it and saves it like any other -- the
///     remote layer only fills it on open and uploads it after a save.
/// </summary>
public sealed class RemoteSiloCache
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private RemoteSiloCache(Uri uri, string directory)
    {
        Uri       = uri;
        Directory = directory;
        File      = Path.Combine(directory, FileNameFor(uri));
        StateFile = Path.Combine(directory, "remote.json");
        State     = Load();
    }

    public Uri         Uri       { get; }
    public string      Directory { get; }

    /// <summary>The cached Silo file; named after the last segment of the URL so the title bar reads naturally.</summary>
    public string File { get; }

    public string      StateFile { get; }
    public RemoteState State     { get; private set; }

    public bool HasLocalCopy => System.IO.File.Exists(File);

    public static RemoteSiloCache For(Uri uri)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri.ToLowerInvariant())))[..16];
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Silo", "Remote", key);
        System.IO.Directory.CreateDirectory(dir);
        return new RemoteSiloCache(uri, dir);
    }

    private static string FileNameFor(Uri uri)
    {
        var name = Path.GetFileName(uri.AbsolutePath.TrimEnd('/'));
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "remote";
        return Formats.Of<SiloFileFormat>().WithExtension(name);
    }

    private RemoteState Load()
    {
        try
        {
            if (System.IO.File.Exists(StateFile) && JsonSerializer.Deserialize<RemoteState>(System.IO.File.ReadAllText(StateFile)) is { } s)
                return s;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Remote sync state '{File}' is unreadable; starting from scratch", StateFile);
        }

        return new RemoteState { Url = Uri.AbsoluteUri };
    }

    public void Save()
    {
        State.Url = Uri.AbsoluteUri;
        try
        {
            System.IO.File.WriteAllText(StateFile, JsonSerializer.Serialize(State, Json));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save the remote sync state '{File}'", StateFile);
        }
    }
}
