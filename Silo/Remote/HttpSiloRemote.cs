using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Serilog;
using Silo.Contracts.Providers.Data;
using Silo.DbModel.Sqlite;

namespace Silo.Remote;

/// <summary>
///     A Silo stored at an http(s) URL: <b>pulled</b> when opened (conditional GET -- <c>If-None-Match</c> with the saved
///     ETag, so an unchanged file costs one tiny request) and <b>pushed</b> after each save (PUT).
/// </summary>
/// <remarks>
///     The server only has to speak plain HTTP: GET returns the .silo bytes (ideally with an <c>ETag</c> or
///     <c>Last-Modified</c>), PUT replaces them (any 2xx is success). If the server supports it, a strong ETag is sent
///     back as <c>If-Match</c> so a concurrent change is rejected with 412; for servers that don't, a HEAD before the PUT
///     compares the validators. Either way a change by someone else surfaces as <see cref="RemoteConflictException" />.
///     Authentication is Windows integrated authentication only for now (no stored credentials).
/// </remarks>
public sealed class HttpSiloRemote : ISiloRemote
{
    private static readonly HttpClient Client = CreateClient();

    private readonly RemoteSiloCache _cache;

    public HttpSiloRemote(RemoteSiloCache cache)
    {
        _cache = cache;
    }

    public Uri    Uri  => _cache.Uri;
    public string Kind => "HTTP";

    public bool HasUnpushedChanges
    {
        get => _cache.State.Unpushed;
        set
        {
            if (_cache.State.Unpushed == value) return;
            _cache.State.Unpushed = value;
            _cache.Save();
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
                      {
                          UseDefaultCredentials  = true,
                          AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                      };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Silo/1.0");
        return client;
    }

    /// <summary>
    ///     Downloads the remote file into the cache if it changed since the last sync. Returns true if the cache was
    ///     updated, false if it was already current.
    /// </summary>
    public async Task<bool> PullAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Uri);
        if (_cache.HasLocalCopy)
        {
            if (_cache.State.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
                request.Headers.IfNoneMatch.Add(tag);
            else if (_cache.State.LastModified is { } lm)
                request.Headers.IfModifiedSince = new DateTimeOffset(DateTime.SpecifyKind(lm, DateTimeKind.Utc));
        }

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            _cache.State.SyncedUtc = DateTime.UtcNow;
            _cache.Save();
            return false;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException($"There is no Silo at {Uri}.");

        response.EnsureSuccessStatusCode();

        var tmp = _cache.File + ".download";
        await using (var body = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await body.CopyToAsync(file, ct);
        }

        if (!SiloProbe.Probe(tmp).Exists || new FileInfo(tmp).Length == 0)
        {
            System.IO.File.Delete(tmp);
            throw new InvalidDataException($"{Uri} did not return a Silo file.");
        }

        System.IO.File.Move(tmp, _cache.File, true);
        _cache.State.ETag         = response.Headers.ETag?.ToString();
        _cache.State.LastModified = response.Content.Headers.LastModified?.UtcDateTime;
        _cache.State.SyncedUtc    = DateTime.UtcNow;
        _cache.State.Unpushed     = false;
        _cache.Save();
        Log.Information("Pulled {Uri} into {File}", Uri, _cache.File);
        return true;
    }

    /// <summary>True if the remote copy is different from the one this cache last synced with.</summary>
    public async Task<bool> HasRemoteChangedAsync(CancellationToken ct = default)
    {
        using var request  = new HttpRequestMessage(HttpMethod.Head, Uri);
        using var response = await Client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return _cache.State.ETag is not null || _cache.State.LastModified is not null;

        response.EnsureSuccessStatusCode();

        if (_cache.State.ETag is { } known && response.Headers.ETag is { } current) return known != current.ToString();
        if (_cache.State.LastModified is { } lm && response.Content.Headers.LastModified is { } rlm) return rlm.UtcDateTime > lm.AddSeconds(1);

        return false; // the server gives us nothing to compare; assume unchanged
    }

    public async Task PushAsync(string localFile, bool force, CancellationToken ct = default)
    {
        if (!force && await HasRemoteChangedAsync(ct))
            throw new RemoteConflictException($"{Uri} was changed by someone else since this Silo last synced with it.");

        await using var file = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var       content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var request = new HttpRequestMessage(HttpMethod.Put, Uri) { Content = content };
        if (!force && _cache.State.ETag is { } etag && !etag.StartsWith("W/", StringComparison.Ordinal) &&
            EntityTagHeaderValue.TryParse(etag, out var tag))
            request.Headers.IfMatch.Add(tag);

        using var response = await Client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            throw new RemoteConflictException($"{Uri} was changed by someone else since this Silo last synced with it.");

        response.EnsureSuccessStatusCode();

        // Learn the validators of what we just uploaded, so the next push compares against it.
        _cache.State.ETag         = response.Headers.ETag?.ToString();
        _cache.State.LastModified = response.Content.Headers.LastModified?.UtcDateTime;
        if (_cache.State.ETag is null && _cache.State.LastModified is null)
        {
            try
            {
                using var head     = new HttpRequestMessage(HttpMethod.Head, Uri);
                using var headResp = await Client.SendAsync(head, ct);
                if (headResp.IsSuccessStatusCode)
                {
                    _cache.State.ETag         = headResp.Headers.ETag?.ToString();
                    _cache.State.LastModified = headResp.Content.Headers.LastModified?.UtcDateTime;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                /* the upload itself succeeded; validators are a nicety */
            }
        }

        _cache.State.SyncedUtc = DateTime.UtcNow;
        _cache.State.Unpushed  = false;
        _cache.Save();
        Log.Information("Pushed {File} to {Uri}", localFile, Uri);
    }
}
