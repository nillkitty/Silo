using System.Net.Http;
using System.Runtime.CompilerServices;
using Serilog;
using Silo.Connectors;
using Silo.Connectors.Web;
using Silo.Labs;
using Silo.Model;
using Silo.Providers.Action;
using Silo.Ui;
using Silo.Ui.Documents;

namespace Silo.TextEditing;

/// <summary>
///     Opens text in text editor tabs (<see cref="TextEditorView" />): files, values kept in the Silo, web responses and
///     new
///     documents. A text opens as a document tab of the main window; opening the same thing again brings its tab forward.
/// </summary>
public static class TextEditors
{
    private static readonly ConditionalWeakTable<OpenSilo, StrongBox<int>> SiloKeys = new();
    private static readonly List<TextEditorView>                           Editors  = [];
    private static          int                                            _nextSiloKey;
    private static          int                                            _untitled;

    /// <summary>The text editors that are open.</summary>
    public static IReadOnlyList<TextEditorView> OpenEditors => Editors.ToList();

    private static DocumentTabs? Tabs => (Application.Current?.MainWindow as MainWindow)?.Documents;

    /// <summary>A short name for a Silo that is unique while the app runs: part of the tab key of the texts kept in it.</summary>
    public static string SiloKey(OpenSilo? silo)
    {
        return silo is null ? "none" : SiloKeys.GetValue(silo, _ => new StrongBox<int>(++_nextSiloKey)).Value.ToString();
    }

    // ---- opening -------------------------------------------------------------------------------

    /// <summary>
    ///     Opens <paramref name="resource" /> in a text editor tab (a permanent one unless <paramref name="pin" /> is
    ///     false).
    /// </summary>
    public static void Open(TextResource resource, bool pin = true)
    {
        if (Tabs is not { } tabs)
        {
            App.Status("The text editor needs the main window");
            return;
        }

        tabs.Open(resource.Key, resource.Title, () => Build(resource), Released, pin);
    }

    private static UIElement Build(TextResource resource)
    {
        var view = new TextEditorView(resource);
        Editors.Add(view);
        return view;
    }

    private static void Released(UIElement element)
    {
        if (element is not TextEditorView view) return;

        view.Release();
        Editors.Remove(view);
    }

    /// <summary>Brings the tab of an open editor forward (and keeps it, if it was the preview tab).</summary>
    public static void Reveal(TextEditorView view)
    {
        Tabs?.Open(view.Resource.Key, view.Resource.Title, () => view, Released, true);
    }

    /// <summary>The editor saved its text somewhere else and now edits that: its tab is known by the new resource's key.</summary>
    public static void Rekey(TextEditorView view, string oldKey)
    {
        Tabs?.Rekey(oldKey, view.Resource.Key);
    }

    /// <summary>
    ///     A new, empty (or given) document in <paramref name="language" />: File → New Text Document, and the result of
    ///     Convert.
    /// </summary>
    public static void OpenNew(TextLanguage language, string? text = null)
    {
        Open(new ScratchTextResource(language, ++_untitled, text));
    }

    /// <summary>Opens a file on disk (asking first when it is very large).</summary>
    public static void OpenFile(string path, bool pin = true)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                App.Status("There is no file at " + path);
                return;
            }

            if (info.Length > FileTextResource.LargeFile &&
                !LabUi.Confirm($"{info.Name} is {info.Length / (1024 * 1024):N0} MB. Large files are slow to edit, and checking and folding are turned off for them. Open it anyway?",
                               "Open Text File"))
                return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            App.Status(ex.Message);
            return;
        }

        Open(new FileTextResource(path), pin);
    }

    /// <summary>File → Open Text File: asks for one or more files and opens each.</summary>
    public static void OpenFileDialog()
    {
        foreach (string path in Dialogs.OpenFiles("Open Text File", TextLanguages.OpenFilter) ?? [])
            OpenFile(path);
    }

    /// <summary>Opens text already in hand (a response body) as a document that can be edited and saved elsewhere.</summary>
    public static void OpenText(string key, string title, string location, string text, string? fileName = null, string? contentType = null)
    {
        Open(DetachedTextResource.Of(key, title, location, text, fileName, contentType));
    }

    /// <summary>Fetches <paramref name="address" /> (a GET) with <paramref name="client" /> and opens the body of the answer.</summary>
    public static void OpenWebResponse(Uri address, HttpClient client)
    {
        string name  = Path.GetFileName(address.AbsolutePath.TrimEnd('/'));
        string title = name.Length > 0 ? name : address.Host;
        Open(new DetachedTextResource("text:web:" + address.AbsoluteUri, title, address.AbsoluteUri, async cancel =>
                                                                                                     {
                                                                                                         using var response =
                                                                                                             await client.GetAsync(address,
                                                                                                                                   cancel);
                                                                                                         byte[] bytes =
                                                                                                             await response.Content
                                                                                                                           .ReadAsByteArrayAsync(cancel);
                                                                                                         string? type = response.Content
                                                                                                                                .Headers
                                                                                                                                .ContentType
                                                                                                                               ?.MediaType;
                                                                                                         if (!response.IsSuccessStatusCode)
                                                                                                             App
                                                                                                                .Status($"{address.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}");

                                                                                                         var content =
                                                                                                             TextFiles.Decode(bytes);
                                                                                                         return content with
                                                                                                                {
                                                                                                                    Language =
                                                                                                                    TextLanguages
                                                                                                                       .Detect(address
                                                                                                                                  .AbsolutePath,
                                                                                                                               type,
                                                                                                                               content
                                                                                                                                  .Text)
                                                                                                                };
                                                                                                     }));
    }

    // ---- closing -------------------------------------------------------------------------------

    /// <summary>
    ///     The window is closing: asks about each editor with unsaved changes that is not kept in a Silo (those are written
    ///     into
    ///     their Silo as it closes, and the Silo asks). False when the person cancelled.
    /// </summary>
    public static bool ConfirmCloseAll()
    {
        foreach (var editor in Editors.ToList())
        {
            if (!editor.IsDirty || editor.Resource.Silo is not null) continue;

            Reveal(editor);
            if (!editor.ConfirmClose()) return false;
        }

        return true;
    }

    /// <summary>
    ///     A Silo is closing: the tabs of the texts kept in it close too (their changes were written into it as it was
    ///     saved).
    /// </summary>
    public static void CloseFor(OpenSilo silo)
    {
        foreach (var editor in Editors.Where(e => ReferenceEquals(e.Resource.Silo, silo)).ToList())
            Tabs?.Close(editor.Resource.Key);
    }

    /// <summary>The Silo a store belongs to, among the open ones.</summary>
    internal static OpenSilo? SiloOf(object? store)
    {
        return store is null ? null : OpenSilo.OpenSilos.FirstOrDefault(s => ReferenceEquals(s.Data, store));
    }
}

// ---- the commands that open things in the editor (Node menu and the Explorer's right-click menu) -------------------------------

/// <summary>"Open in Text Editor" on a value kept in the Silo (Saved Objects, Saved Scalars).</summary>
public sealed class SavedItemTextActions : IActionProvider<SavedItem>
{
    public IEnumerable<ProvidedCommand> GetCommands(SavedItem node)
    {
        if (node.Store is not { } store) yield break;

        var item = node;
        yield return ProvidedCommand.From("stored.texteditor", "Open in _Text Editor",
                                          () => TextEditors.Open(new StoredTextResource(store, item.Record.Key, item.Record.Scope,
                                                                                        TextEditors.SiloOf(store))), "SourceCode",
                                          toolTip: "Edit the stored value as text (JSON)", group: "Edit", placement: CommandPlacement.Menu);
    }
}

/// <summary>"Open in Text Editor" on a preference kept in the Silo.</summary>
public sealed class PreferenceTextActions : IActionProvider<PreferenceDetails>
{
    public IEnumerable<ProvidedCommand> GetCommands(PreferenceDetails node)
    {
        if (node.Store is not { } store) yield break;

        var item = node;
        yield return ProvidedCommand.From("pref.texteditor", "Open in _Text Editor",
                                          () => TextEditors.Open(new PreferenceTextResource(store, item.Name, TextEditors.SiloOf(store))),
                                          "SourceCode", toolTip: "Edit the preference's stored text", group: "Edit",
                                          placement: CommandPlacement.Menu);
    }
}

/// <summary>"Open Response in Text Editor" on a web connection: fetches its address and opens what comes back.</summary>
public sealed class WebTextActions : IActionProvider<WebConnection>
{
    public IEnumerable<ProvidedCommand> GetCommands(WebConnection node)
    {
        if (node.Destination?.Uri is not { } address || node.Data is not { } client) yield break;

        yield return ProvidedCommand.From("web.texteditor", "Open Response in _Text Editor", () =>
                                                                                             {
                                                                                                 try
                                                                                                 {
                                                                                                     TextEditors.OpenWebResponse(address,
                                                                                                                                 client);
                                                                                                 }
                                                                                                 catch (Exception ex)
                                                                                                 {
                                                                                                     Log.Warning(ex,
                                                                                                                 "Opening {Address} in the text editor failed",
                                                                                                                 address);
                                                                                                     App.Status(ex.Message);
                                                                                                 }
                                                                                             }, "SourceCode",
                                          toolTip: "Fetch the address (GET) and open the body of the answer as text", group: "Web",
                                          placement: CommandPlacement.Menu);
    }
}