using System.IO;
using System.Net.Http;
using Serilog;
using Silo.Providers.Data;
using Silo.Settings;

namespace Silo.Remote;

/// <summary>The result of syncing a remote Silo on open: the local file to open, and the remote to keep it in sync with.</summary>
/// <param name="LocalFile">The cache file (an ordinary local Silo).</param>
/// <param name="Remote">Pushed to after each save.</param>
/// <param name="Offline">The remote could not be reached; the cached copy is being used as it was.</param>
/// <param name="Notice">Something worth telling the person (e.g. "opened the cached copy").</param>
public sealed record RemoteOpenResult(string LocalFile, ISiloRemote Remote, bool Offline, string? Notice);

/// <summary>
///     "Sync on open" for a Silo that comes from a URL:
///     <list type="number">
///         <item>No local copy yet: download it.</item>
///         <item>Local copy, nothing waiting to upload: conditional GET; only a changed file is downloaded.</item>
///         <item>
///             Local copy with saved-but-not-uploaded changes (an earlier upload failed, or the app was closed offline):
///             if the remote has not changed, upload now; if it has, it is a conflict -- ask (or follow the stored
///             policy).
///         </item>
///         <item>Remote unreachable but a cached copy exists: open the cached copy and say so.</item>
///     </list>
/// </summary>
public static class RemoteSiloOpener
{
    public static async Task<RemoteOpenResult> OpenAsync(Uri uri, ISaveConflictResolver? resolver, CancellationToken ct = default)
    {
        switch (SiloLocation.Classify(uri.AbsoluteUri))
        {
            case SiloLocationKind.Git:
                throw new NotSupportedException(GitSiloRemote.NotImplementedMessage);
            case SiloLocationKind.Local:
                throw new ArgumentException("Not a remote location.", nameof(uri));
        }

        var     cache   = RemoteSiloCache.For(uri);
        var     remote  = new HttpSiloRemote(cache);
        var     hadCopy = cache.HasLocalCopy;
        string? notice  = null;

        try
        {
            if (hadCopy && cache.State.Unpushed)
            {
                if (!await remote.HasRemoteChangedAsync(ct))
                {
                    await remote.PushAsync(cache.File, false, ct);
                    notice = "Uploaded the changes saved earlier.";
                }
                else
                {
                    var conflict = new SaveConflict(SaveConflictKind.RemoteChanged, uri.AbsoluteUri, null, cache.State.SyncedUtc);
                    switch (await SaveConflictPolicy.DecideAsync(resolver, conflict))
                    {
                        case SaveConflictChoice.Overwrite:
                            await remote.PushAsync(cache.File, true, ct);
                            notice = "Replaced the remote copy with the one saved on this computer.";
                            break;

                        case SaveConflictChoice.SaveCopy:
                            var mine = Path.Combine(cache.Directory,
                                                    $"{Path.GetFileNameWithoutExtension(cache.File)} (my changes {DateTime.Now:yyyy-MM-dd HHmmss}).silo");
                            File.Move(cache.File, mine);
                            cache.State.Unpushed = false;
                            await remote.PullAsync(ct);
                            notice = $"Opened the remote version. Your unsent changes are kept in {mine}";
                            break;

                        default:
                            throw new
                                OperationCanceledException("Open cancelled: the remote copy changed while changes here were not uploaded.");
                    }
                }
            }
            else
            {
                var changed = await remote.PullAsync(ct);
                if (!changed && hadCopy) Log.Debug("{Uri} is unchanged since the last sync", uri);
            }
        }
        catch (Exception ex) when (hadCopy && ex is HttpRequestException or TaskCanceledException)
        {
            Log.Warning(ex, "Could not reach {Uri}; opening the cached copy", uri);
            return new RemoteOpenResult(cache.File, remote, true,
                                        $"{uri.Host} could not be reached; opened the copy from your last visit.");
        }

        return new RemoteOpenResult(cache.File, remote, false, notice);
    }
}