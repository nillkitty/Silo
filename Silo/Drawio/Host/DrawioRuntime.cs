using System.Linq;
using System.Net.Http;
using System.Threading;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using Silo.Drawio.Options;

namespace Silo.Drawio.Host;

/// <summary>
///     Where to get the draw.io web app from and which release to pin. Persisted to
///     <c>%AppData%\Silo\drawio-runtime.json</c>; every field is meant to be editable (the Developer
///     level of the options window exposes them).
/// </summary>
public sealed class DrawioRuntimeSettings
{
    /// <summary>
    ///     Download the runtime automatically the first time a pane needs it, instead of showing a
    ///     "Download draw.io" button first.
    /// </summary>
    public bool AutoInstall { get; set; }

    /// <summary>
    ///     Expected SHA-256 (hex) of the downloaded archive. When empty the download is accepted and
    ///     its hash logged, so it can be pasted here to pin the release (trust on first use).
    /// </summary>
    public string Sha256 { get; set; } = "";

    /// <summary>
    ///     Fallback archive of the whole repository ({0} = <see cref="Version" />); only the
    ///     <c>src/main/webapp</c> folder inside it is extracted.
    /// </summary>
    public string SourceUrl { get; set; } = "https://github.com/jgraph/drawio/archive/refs/tags/v{0}.zip";

    /// <summary>The draw.io release (without the leading "v") to install.</summary>
    public string Version { get; set; } = "32.3.0";

    /// <summary>The release's <c>draw.war</c> asset ({0} = <see cref="Version" />). A war is a zip of the web app.</summary>
    public string WarUrl { get; set; } = "https://github.com/jgraph/drawio/releases/download/v{0}/draw.war";
}

/// <summary>
///     Downloads, verifies and unpacks a pinned draw.io release (Apache-2.0) into
///     <c>%AppData%\Silo\drawio\v&lt;version&gt;</c>, and writes the small host page that bridges
///     draw.io's <c>postMessage</c> embed protocol to WebView2. After the one-off download everything
///     is served locally from that folder via a WebView2 virtual host name, so panes work offline.
/// </summary>
public static class DrawioRuntime
{
    /// <summary>Name of the host page that wraps the editor in an iframe and relays messages to WebView2.</summary>
    public const string HostPage = "silo-host.html";

    /// <summary>The virtual host name the runtime folder is served under.</summary>
    public const string VirtualHost = "drawio.silo.local";

    private static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "drawio");

    private static readonly string SettingsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Silo", "drawio-runtime.json");

    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    private static DrawioRuntimeSettings? _settings;

    /// <summary>
    ///     The editor iframe's address is <c>index.html</c> plus the query forwarded from the host
    ///     page's own address, so the page itself needs no per-pane customisation. Messages are
    ///     relayed as strings both ways (draw.io's JSON protocol is already JSON text).
    /// </summary>
    private const string HostPageHtml = """
                                        <!DOCTYPE html>
                                        <html>
                                        <head>
                                        <meta charset="utf-8">
                                        <title>Silo diagram</title>
                                        <style>
                                        html, body { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: transparent; }
                                        iframe { border: 0; width: 100%; height: 100%; display: block; }
                                        </style>
                                        </head>
                                        <body>
                                        <iframe id="editor"></iframe>
                                        <script>
                                        (function () {
                                          var frame = document.getElementById('editor');
                                          var host = window.chrome && window.chrome.webview;
                                          if (!host) { document.body.textContent = 'Not running inside WebView2.'; return; }

                                          // editor -> Silo
                                          window.addEventListener('message', function (e) {
                                            if (e.source !== frame.contentWindow) return;
                                            host.postMessage(typeof e.data === 'string' ? e.data : JSON.stringify(e.data));
                                          });

                                          // Silo -> editor. Messages marked "siloPlugin":true are for the Silo plugin running inside the
                                          // editor (see Drawio/Plugin/silo-plugin.js); draw.io itself would answer them with unknownMessage.
                                          host.addEventListener('message', function (e) {
                                            var text = typeof e.data === 'string' ? e.data : JSON.stringify(e.data);
                                            if (text.indexOf('"siloBootstrap"') >= 0) { injectPlugin(text); return; }
                                            if (text.indexOf('"siloPlugin":true') >= 0) {
                                              var request = null;
                                              try { request = JSON.parse(text); } catch (err) { return; }
                                              var plugin = frame.contentWindow && frame.contentWindow.SiloPlugin;
                                              if (plugin) { plugin.receive(request); }
                                              else {
                                                host.postMessage(JSON.stringify({ event: 'silo.result', requestId: request.requestId, ok: false, error: 'The Silo plugin is not loaded.' }));
                                              }
                                              return;
                                            }
                                            frame.contentWindow.postMessage(text, '*');
                                          });

                                        // The Silo plugin normally arrives with the WebView2 document-created script; if the editor frame did not get it,
                                        // ask Silo for it and add it here (it waits for Draw.loadPlugin, so it works before or after draw.io starts).
                                        function injectPlugin(text) {
                                          try {
                                            var w = frame.contentWindow;
                                            if (!w || w.SiloPlugin) return;
                                            var s = w.document.createElement('script');
                                            s.text = JSON.parse(text).siloBootstrap;
                                            (w.document.head || w.document.documentElement).appendChild(s);
                                          } catch (err) {
                                            host.postMessage(JSON.stringify({ event: 'silo.error', section: 'inject', message: String(err) }));
                                          }
                                        }

                                        // Once the editor page has loaded: make sure the plugin is in it, and let Ctrl+right-click open Silo's design menu
                                        // instead of draw.io's own context menu (the editor is a same-origin iframe, so the capture listener can sit on its document).
                                        frame.addEventListener('load', function () {
                                          try {
                                            if (!frame.contentWindow.SiloPlugin) host.postMessage(JSON.stringify({ event: 'silo.needPlugin' }));
                                          } catch (err) { }
                                          try {
                                            frame.contentWindow.document.addEventListener('contextmenu', function (e) {
                                              if (!e.ctrlKey) return;
                                              e.preventDefault();
                                              e.stopPropagation();
                                              host.postMessage(JSON.stringify({ event: 'siloDesignMenu' }));
                                            }, true);
                                          } catch (err) { }
                                        });

                                        frame.src = 'index.html' + window.location.search;
                                        })();
                                        </script>
                                        </body>
                                        </html>
                                        """;

    public static DrawioRuntimeSettings Settings => _settings ??= LoadSettings();

    /// <summary>Resource name of the Silo plugin built into the assembly (<c>Drawio/Plugin/silo-plugin.js</c>).</summary>
    public const string PluginResourceName = "silo-plugin.js";

    /// <summary>
    ///     The script injected into every document the editor's web view creates: the plugin's options as
    ///     <c>window.__SILO_PLUGIN_OPTIONS__</c>, then the plugin source -- from <paramref name="scriptPath" /> when that
    ///     file exists (developer override), otherwise the copy embedded in Silo.
    /// </summary>
    public static string BuildPluginBootstrap(JsonObject options, string? scriptPath)
    {
        string source = "";

        if (!string.IsNullOrWhiteSpace(scriptPath) && File.Exists(scriptPath))
        {
            source = File.ReadAllText(scriptPath);
        }
        else
        {
            var assembly = typeof(DrawioRuntime).Assembly;
            using var stream = assembly.GetManifestResourceStream(PluginResourceName);
            if (stream is null)
                throw new InvalidOperationException($"The embedded resource '{PluginResourceName}' is missing. Add <EmbeddedResource Include=\"Drawio\\Plugin\\silo-plugin.js\" LogicalName=\"silo-plugin.js\" /> to Silo.csproj.");

            using var reader = new StreamReader(stream);
            source = reader.ReadToEnd();
        }

        return "window.__SILO_PLUGIN_OPTIONS__ = " + options.ToJsonString() + ";\n" + source;
    }

    /// <summary>The folder the configured release is (or will be) unpacked into.</summary>
    public static string InstallDirectory => Path.Combine(Root, "v" + Settings.Version);

    public static bool IsInstalled => File.Exists(Path.Combine(InstallDirectory, "index.html"));

    /// <summary>Address of the host page for a given query string (see <see cref="DrawioOptions.ToQueryString" />).</summary>
    public static string HostUrl(string query)
    {
        return $"https://{VirtualHost}/{HostPage}?{query}";
    }

    public static void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not save draw.io runtime settings");
        }
    }

    private static DrawioRuntimeSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<DrawioRuntimeSettings>(File.ReadAllText(SettingsFile)) is { } s)
                return s;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read draw.io runtime settings; using defaults");
        }

        return new DrawioRuntimeSettings();
    }

    /// <summary>
    ///     Makes sure the configured release is unpacked and the host page is current. Safe to call
    ///     from several panes at once: installs are serialized and the second caller finds the first's
    ///     result.
    /// </summary>
    public static async Task EnsureInstalledAsync(IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        await InstallGate.WaitAsync(cancel);
        try
        {
            if (!IsInstalled) await InstallAsync(progress, cancel);
            WriteHostPage();
        }
        finally
        {
            InstallGate.Release();
        }
    }

    /// <summary>(Re)writes the host page into the install folder if it is missing or different.</summary>
    public static void WriteHostPage()
    {
        string path = Path.Combine(InstallDirectory, HostPage);
        if (File.Exists(path) && File.ReadAllText(path) == HostPageHtml) return;

        Directory.CreateDirectory(InstallDirectory);
        File.WriteAllText(path, HostPageHtml);
    }

    private static async Task InstallAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        var    settings = Settings;
        string version  = settings.Version;
        string temp     = Path.Combine(Path.GetTempPath(), $"silo-drawio-{Guid.NewGuid():N}.zip");

        try
        {
            string  prefix = "";
            string? sha;

            try
            {
                progress?.Report($"Downloading draw.io v{version}…");
                sha = await DownloadAsync(string.Format(settings.WarUrl, version), temp, progress, cancel);
            }
            catch (HttpRequestException ex)
            {
                Log.Warning(ex, "draw.war download failed; falling back to the source archive");
                progress?.Report($"Downloading draw.io v{version} (source archive)…");
                sha    = await DownloadAsync(string.Format(settings.SourceUrl, version), temp, progress, cancel);
                prefix = "src/main/webapp/";
            }

            if (settings.Sha256.Length > 0 && !string.Equals(sha, settings.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The downloaded draw.io archive has SHA-256 {sha}, which does not match the pinned {settings.Sha256}.");

            Log.Information("draw.io v{version} archive SHA-256: {sha}", version, sha);

            progress?.Report("Unpacking…");
            string staging = InstallDirectory + ".tmp";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            int count = await Task.Run(() => Extract(temp, staging, prefix), cancel);
            if (count == 0 || !File.Exists(Path.Combine(staging, "index.html")))
                throw new InvalidDataException("The draw.io archive did not contain a web app (no index.html).");

            if (Directory.Exists(InstallDirectory)) Directory.Delete(InstallDirectory, true);
            Directory.Move(staging, InstallDirectory);
            progress?.Report("draw.io is ready.");
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                /* temp file cleanup is best-effort */
            }
        }
    }

    private static async Task<string> DownloadAsync(string url, string destination, IProgress<string>? progress, CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Silo-DrawioRuntime/1.0");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancel);
        await using var target = File.Create(destination);
        using var       hash   = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var  buffer = new byte[81920];
        long done   = 0;
        long lastReported = 0;
        int  read;
        while ((read = await source.ReadAsync(buffer, cancel)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancel);
            hash.AppendData(buffer, 0, read);
            done += read;

            if (done - lastReported >= 1_000_000)
            {
                lastReported = done;
                progress?.Report(total is { } t ? $"Downloading… {done / 1_000_000} / {t / 1_000_000} MB" : $"Downloading… {done / 1_000_000} MB");
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    ///     Unpacks the web app. <paramref name="prefix" /> is the folder (relative to the archive root's
    ///     single top-level folder, for a source archive) that holds it; an empty prefix means the archive
    ///     root itself (a war). Server-side folders and anything that would escape the target are skipped.
    /// </summary>
    private static int Extract(string zipPath, string targetDir, string prefix)
    {
        string fullTarget = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        int    count      = 0;

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');

            if (prefix.Length > 0)
            {
                // "drawio-32.3.0/src/main/webapp/index.html" -> "index.html"
                int at = name.IndexOf('/' + prefix, StringComparison.Ordinal);
                if (at < 0) continue;
                name = name[(at + 1 + prefix.Length)..];
            }

            if (name.Length == 0 || name.EndsWith('/')) continue;
            if (name.StartsWith("WEB-INF/", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
                continue;

            string dest = Path.GetFullPath(Path.Combine(targetDir, name));
            if (!dest.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, true);
            count++;
        }

        return count;
    }
}
