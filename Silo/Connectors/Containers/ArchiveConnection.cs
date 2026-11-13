using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32;
using Serilog;
using Silo.Commanding;
using Silo.Connectors.File;
using Silo.Design;
using Silo.Providers.Archive;
using Silo.Tasks;
using Silo.Ui.Windows;
using Silo.ViewModel;
using Telefrag.DI;
using IOFile = System.IO.File;

namespace Silo.Connectors.Containers;

/// <summary>
///     A container file that is open in the Explorer: its format, what is in it (read once and kept until it changes), and the
///     changes made to it, which run one at a time as tasks and then refresh the folders of the tree they touched.
/// </summary>
public sealed class ArchiveFile
{
    /// <summary>One change at a time per file, whichever connection (or tool) makes it.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly object                                        _lock  = new();
    private readonly Dictionary<string, WeakReference<SiloNode>>  _nodes = new(StringComparer.OrdinalIgnoreCase);
    private          IReadOnlyList<ContainerEntry>?                _entries;

    public ArchiveFile(string path, IContainerFormat format)
    {
        Path   = global::System.IO.Path.GetFullPath(path.Required());
        Format = format.Required();
    }

    /// <summary>The container file.</summary>
    public string Path { get; }

    public IContainerFormat Format { get; }

    /// <summary>Whether things can be added to and taken out of it: the format can write, and the file is not read-only.</summary>
    public bool CanWrite
    {
        get
        {
            try
            {
                return Format.CanWrite && !new FileInfo(Path).IsReadOnly;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>What is in the container (read the first time it is asked for, and again after a change or a refresh).</summary>
    public IReadOnlyList<ContainerEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries ??= Format.List(Path);
            }
        }
    }

    /// <summary>Reads the container again the next time <see cref="Entries" /> is asked for.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _entries = null;
        }
    }

    /// <summary>The entry at a path; null for the top of the container or a path that is not in it.</summary>
    public ContainerEntry? Find(string path)
    {
        return path.Length == 0 ? null : Entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What is in a folder of the container ("" for the top): its folders by name, then its files by name.</summary>
    public IReadOnlyList<ContainerEntry> ChildrenOf(string folder)
    {
        return Entries.Where(e => string.Equals(ContainerPaths.Parent(e.Path), folder, StringComparison.OrdinalIgnoreCase))
                      .OrderBy(e => e.IsDirectory ? 0 : 1)
                      .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                      .ToList();
    }

    /// <summary>Remembers the node that shows a folder, so a change to the folder can refresh it.</summary>
    internal void Track(string folder, SiloNode node)
    {
        lock (_lock)
        {
            _nodes[folder] = new WeakReference<SiloNode>(node);
        }
    }

    private SiloNode? NodeOf(string folder)
    {
        lock (_lock)
        {
            return _nodes.TryGetValue(folder, out var weak) && weak.TryGetTarget(out var node) ? node : null;
        }
    }

    /// <summary>Takes a file, or a folder with everything in it, out into a new temporary folder; returns where it now is.</summary>
    public string Extract(string entry)
    {
        string dir = DropFiles.NewDirectory();
        if (entry.Length == 0)
        {
            Format.ExtractTo(Path, dir);
            return dir;
        }

        Format.ExtractTo(Path, dir, entry);
        return global::System.IO.Path.Combine(dir, entry[(entry.LastIndexOf('/') + 1)..]);
    }

    /// <summary>
    ///     Makes changes to the container as a task (in the Tasks pane), after any change still running on the same file, then
    ///     refreshes the folders they touched. A failure is reported in the status bar and the log, not thrown.
    /// </summary>
    public async Task UpdateAsync(IReadOnlyList<ContainerChange> changes, string what)
    {
        if (changes.Count == 0) return;

        try
        {
            await TaskCenter.Run(what, "Archive", async task =>
                                                  {
                                                      var gate = Gates.GetOrAdd(Path, _ => new SemaphoreSlim(1, 1));
                                                      await gate.WaitAsync(task.Token);
                                                      try
                                                      {
                                                          await Task.Run(() => Format.Update(Path, changes, task.Token), task.Token);
                                                          task.SetResult(changes.Count, FileTasks.SizeOf(Path));
                                                      }
                                                      finally
                                                      {
                                                          Invalidate();
                                                          gate.Release();
                                                      }
                                                  }, true);
        }
        catch (OperationCanceledException)
        {
            App.Status($"{what}: cancelled");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Changing the container {Path} failed", Path);
            App.Status($"{what} failed: {ex.Message}");
        }

        DropFiles.OnUi(() => Reload(changes));
    }

    /// <summary>
    ///     Makes changes to the container now, on this thread (after any change still running on the same file), as a task;
    ///     throws when they fail. For a move, whose original may only go once the copy is surely made.
    /// </summary>
    public void UpdateNow(IReadOnlyList<ContainerChange> changes, string what)
    {
        if (changes.Count == 0) return;

        var task = TaskCenter.Begin(what, "Archive");
        var gate = Gates.GetOrAdd(Path, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            Format.Update(Path, changes);
            task.Succeed(null, changes.Count, FileTasks.SizeOf(Path));
        }
        catch (Exception ex)
        {
            task.Fail(ex);
            throw;
        }
        finally
        {
            Invalidate();
            gate.Release();
            DropFiles.OnUi(() => Reload(changes));
        }
    }

    /// <summary>Refreshes the nodes of the folders the changes were in (the nearest one in the tree, for a folder that is not).</summary>
    private void Reload(IEnumerable<ContainerChange> changes)
    {
        var folders = new HashSet<SiloNode>(ReferenceEqualityComparer.Instance);
        foreach (var change in changes)
        {
            string path = change switch
                          {
                              ContainerChange.AddFile a      => a.Path,
                              ContainerChange.AddDirectory d => d.Path,
                              ContainerChange.Remove r       => r.Path,
                              _                              => string.Empty
                          };
            for (string folder = ContainerPaths.Parent(ContainerPaths.Normalize(path));; folder = ContainerPaths.Parent(folder))
            {
                if (NodeOf(folder) is { } node)
                {
                    folders.Add(node);
                    break;
                }

                if (folder.Length == 0) break;
            }
        }

        foreach (var node in folders)
            _ = node.ReloadChildrenAsync();
    }
}

/// <summary>
///     A file or folder inside a container file -- or, with no entry, the top of the container -- as a node of the Explorer.
///     A folder's node lists what is in it as it is expanded. It can be dragged out (or copied) as files, taken out by a cut
///     or a move, and files and folders dropped or pasted on a folder go into the container there.
/// </summary>
public class ArchiveItemModel : INodeProvider<ContainerEntry>, INodeDetails, INodeViewProvider, ICommandProvider, ICuttable, IPastable
{
    private const string Group = "Archive";

    public ArchiveItemModel(ArchiveFile archive, ContainerEntry? entry)
    {
        Archive = archive.Required();
        Entry   = entry;
    }

    /// <summary>The container file this is in.</summary>
    public ArchiveFile Archive { get; }

    /// <summary>The file or folder; null for the top of the container.</summary>
    public ContainerEntry? Entry { get; }

    /// <summary>Where it is in the container ("" for the top).</summary>
    public string EntryPath => Entry?.Path ?? string.Empty;

    /// <summary>True for a folder or the top of the container.</summary>
    public bool IsFolder => Entry is null || Entry.IsDirectory;

    /// <summary>The text of the node; a connection shows its own name instead.</summary>
    protected virtual string NodeText => Name;

    public ContainerEntry? Data => Entry;

    public string Name => Entry?.Name ?? Path.GetFileName(Archive.Path);

    public string TypeName => Entry is null ? Archive.Format.DisplayName ?? "Archive" : Entry.IsDirectory ? "Folder in Archive" : "File in Archive";

    public Uri Uri => ContainerUris.For(ContainerUris.ArchiveScheme, Archive.Path, EntryPath);

    public IconRef? Icon => IconRef.FromKey(Entry is null ? FileTypeIcons.For(Path.GetExtension(Archive.Path), "Archive") :
                                            Entry.IsDirectory ? "Folder17" : FileTypeIcons.For(Path.GetExtension(Entry.Name), "Document"));

    public virtual SiloNode? GetNode()
    {
        return CreateNode(0);
    }

    protected SiloNode CreateNode(int? expand)
    {
        var node = new ArchiveNode(this, NodeText, expand);
        if (IsFolder) Archive.Track(EntryPath, node);
        return node;
    }

    internal async Task<IReadOnlyList<SiloNode>> LoadChildNodesAsync(CancellationToken cancel)
    {
        if (Entry is null) Archive.Invalidate(); // the top is read afresh: a Refresh there sees changes made by others
        var entries = await Task.Run(() => Archive.ChildrenOf(EntryPath), cancel);
        return entries.Select(e => new ArchiveItemModel(Archive, e).CreateNode(0)).ToList();
    }

    public IEnumerable<(string Name, string Value)> GetDetails()
    {
        yield return ("Archive", Archive.Path);
        yield return ("Format", Archive.Format.DisplayName ?? Archive.Format.ContentType);
        if (Entry is null)
        {
            yield return ("Size", $"{FileTasks.SizeOf(Archive.Path) ?? 0:N0} bytes");
            yield return ("Can change", Archive.CanWrite ? "Yes" : "No (the format or the file is read-only)");
            yield break;
        }

        yield return ("Path", Entry.Path);
        yield return ("Type", TypeName);
        if (!Entry.IsDirectory) yield return ("Size", $"{Entry.Size:N0} bytes");
        if (Entry.Modified is { } modified) yield return ("Modified", modified.LocalDateTime.ToString("g"));
    }

    public UIElement? BuildView()
    {
        return null;
    }

    // ---- commands -----------------------------------------------------------------------------------------------

    public IEnumerable<ProvidedCommand> GetCommands()
    {
        if (!IsFolder)
            yield return ProvidedCommand.From("archive.open", "_Open", Open, "Document", toolTip: "Takes the file out and opens it with its program", group: Group) with { IsDefault = true };

        yield return ProvidedCommand.From("archive.extract", "E_xtract To...", ExtractTo, "TbExport", toolTip: "Takes this out of the archive into a folder", group: Group);

        if (IsFolder)
        {
            yield return ProvidedCommand.From("archive.add", "Add _Files...", AddFiles, null, () => Archive.CanWrite, "Puts copies of files in this folder of the archive", Group);
            yield return ProvidedCommand.From("archive.newfolder", "New _Folder...", NewFolder, null, () => Archive.CanWrite, "Makes a folder here in the archive", Group);
        }

        if (Entry is not null)
            yield return new ProvidedCommand("archive.delete", "_Delete", new DelegateCommand(_ => Delete(), _ => Archive.CanWrite), null, null,
                                             "Takes this out of the archive", Group, CommandPlacement.Menu, "Delete", IsDestructive: true);
    }

    private void Open()
    {
        try
        {
            string file = Archive.Extract(EntryPath);
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening {Entry} from {Archive} failed", EntryPath, Archive.Path);
            App.Status($"{Name} could not be opened: {ex.Message}");
        }
    }

    private void ExtractTo()
    {
        var dialog = new OpenFolderDialog { Title = $"Extract {Name} to" };
        if (dialog.ShowDialog() != true) return;

        string folder = dialog.FolderName;
        string? entry = EntryPath.Length == 0 ? null : EntryPath;
        _ = FileTasks.RunAsync($"Extracting {Name}", "Archive", task => Task.Run(() => Archive.Format.ExtractTo(Archive.Path, folder, entry, task.Token), task.Token), true)
                     .ContinueWith(t => Log.Warning(t.Exception, "Extracting from {Archive} failed", Archive.Path), TaskContinuationOptions.OnlyOnFaulted);
    }

    private void AddFiles()
    {
        var dialog = new OpenFileDialog { Title = $"Add files to {Name}", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog() == true) _ = AddAsync(dialog.FileNames);
    }

    private void NewFolder()
    {
        if (PromptDialog.Ask("New Folder", $"The name of the new folder in {Name}:", "Create", "New Folder") is not { } name) return;

        string unique = DropFiles.Unique(DropFiles.SafeName(name), Taken);
        _ = Archive.UpdateAsync([new ContainerChange.AddDirectory(ContainerPaths.Combine(EntryPath, unique))], $"Making the folder {unique} in {Path.GetFileName(Archive.Path)}");
    }

    private void Delete()
    {
        var (button, _) = PromptDialog.Show("Delete", $"Take {Name} out of {Path.GetFileName(Archive.Path)}?" + (IsFolder ? " Everything in the folder goes with it." : string.Empty),
                                            ["Delete", "Cancel"]);
        if (button == 0) _ = RemoveFromArchive();
    }

    private Task RemoveFromArchive()
    {
        return Archive.UpdateAsync([new ContainerChange.Remove(EntryPath)], $"Taking {Name} out of {Path.GetFileName(Archive.Path)}");
    }

    private bool Taken(string name)
    {
        return Archive.ChildrenOf(EntryPath).Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Puts copies of files and folders (from the file system) in this folder of the container, renaming any whose name is taken.</summary>
    public Task AddAsync(IReadOnlyList<string> paths)
    {
        var (changes, what) = ChangesToAdd(paths);
        return Archive.UpdateAsync(changes, what);
    }

    private (IReadOnlyList<ContainerChange> Changes, string What) ChangesToAdd(IReadOnlyList<string> paths)
    {
        var changes = new List<ContainerChange>();
        var names   = new HashSet<string>(Archive.ChildrenOf(EntryPath).Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        foreach (string source in paths)
        {
            string name   = DropFiles.Unique(Path.GetFileName(Path.TrimEndingDirectorySeparator(source)), names.Contains);
            string target = ContainerPaths.Combine(EntryPath, name);
            names.Add(name);

            if (Directory.Exists(source))
            {
                changes.Add(new ContainerChange.AddDirectory(target));
                changes.AddRange(ContainerPaths.ChangesToAdd(source, target));
            }
            else if (IOFile.Exists(source))
            {
                changes.Add(new ContainerChange.AddFile(target, source));
            }
        }

        string what = paths.Count == 1 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0])) : $"{paths.Count} items";
        return (changes, $"Adding {what} to {Path.GetFileName(Archive.Path)}");
    }

    // ---- clipboard, and drag and drop -----------------------------------------------------------------------------

    public IReadOnlyCollection<string> CopyFormats => [ClipboardFormats.Files, ClipboardFormats.Text];

    /// <summary>The item as files (taken out of the container only when a paste or drop asks for them) and its name as text.</summary>
    public void Copy(IClipboardWriter clipboard)
    {
        if (Entry is null) clipboard.Set(ClipboardFormats.Files, new[] { Archive.Path });
        else clipboard.SetLazy(ClipboardFormats.Files, () => new[] { Archive.Extract(EntryPath) });

        clipboard.Set(ClipboardFormats.Text, Entry is null ? Archive.Path : EntryPath);
    }

    /// <summary>Only what is inside the container can be cut (the container itself is a file of the file system).</summary>
    public bool CanCut => Entry is not null && Archive.CanWrite;

    /// <summary>A move took a copy: the original comes out of the container.</summary>
    public void CutCompleted(PasteResult result)
    {
        if (result.Effect == PasteEffect.Copied) _ = RemoveFromArchive();
    }

    public IReadOnlyList<string> PasteFormats => [ClipboardFormats.Files];

    public bool CanPaste(IClipboardData data, PasteMode mode)
    {
        if (!IsFolder || !Archive.CanWrite || !DropFiles.Offers(data)) return false;
        if (mode == PasteMode.Move && data.Operation != ClipboardOperation.Cut) return false;

        foreach (var source in data.Sources)
        {
            // the container file itself, or (for a move) a folder of the same container into itself
            if (source is FileModel f && string.Equals(Path.GetFullPath(f.Path), Archive.Path, StringComparison.OrdinalIgnoreCase)) return false;
            if (mode == PasteMode.Move && source is ArchiveItemModel a && a.Entry is not null &&
                string.Equals(a.Archive.Path, Archive.Path, StringComparison.OrdinalIgnoreCase) &&
                (ContainerPaths.IsSameOrInside(EntryPath, a.EntryPath) || string.Equals(ContainerPaths.Parent(a.EntryPath), EntryPath, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Copies the files into this folder of the container: in the background for a copy; for a move, before returning, so
    ///     the originals only remove themselves once their copies are surely in.
    /// </summary>
    public PasteResult Paste(IClipboardData data, PasteMode mode)
    {
        if (!CanPaste(data, mode)) return PasteResult.None;

        var paths = DropFiles.Read(data);
        if (paths.Count == 0) return PasteResult.None;

        if (mode == PasteMode.Duplicate)
        {
            _ = AddAsync(paths);
            return PasteResult.Copied(paths.Cast<object>().ToArray());
        }

        try
        {
            var (changes, what) = ChangesToAdd(paths);
            Archive.UpdateNow(changes, what);
            return PasteResult.Copied(paths.Cast<object>().ToArray());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Moving into {Archive} failed", Archive.Path);
            App.Status($"Moving into {Path.GetFileName(Archive.Path)} failed: {ex.Message}");
            return PasteResult.None;
        }
    }
}

/// <summary>A file or folder of a container file in the Explorer; a folder's children are read as it is expanded.</summary>
public record ArchiveNode : SiloNode
{
    public ArchiveNode(ArchiveItemModel model, string text, int? expand) : base(model, text, model.Uri, model.Icon, expand)
    {
        TypeName    = model.TypeName;
        ChildLoader = model.IsFolder ? model.LoadChildNodesAsync : null;
    }
}

/// <summary>A container file opened as a connection: the top of the container, expanded, with what is in it below.</summary>
public class ArchiveConnection : ArchiveItemModel, IConnection
{
    public ArchiveConnection(IConnector connector, ArchiveFile archive, string? displayText) : base(archive, null)
    {
        Connector   = connector.Required();
        DisplayText = displayText;
    }

    protected override string NodeText => DisplayText is { Length: > 0 } name ? name : base.NodeText;

    public bool?        ConnectionState  => null;
    public IConnector   Connector        { get; }
    public string?      DisplayText      { get; set; }
    public bool         IsInTree         => Node?.IsVisible ?? false;
    public List<object> Metadata         { get; } = [];
    public SiloNode?    Node             { get; private set; }
    public string?      PersistentConfig => null;
    public object?      State            => null;
    public string?      TempConfig       => null;
    public Container    Components       { get; } = new(Guid.NewGuid().ToString());

    public override SiloNode? GetNode()
    {
        return Node ??= CreateNode(1);
    }

    public string? GetHostname()
    {
        return Uri.TryCreate(Archive.Path, UriKind.Absolute, out var uri) && uri.IsUnc ? uri.Host : ".";
    }

    public Task<bool> Connect(CancellationToken cancel = default) => Task.FromResult(true);

    public Task<bool> Disconnect() => Task.FromResult(false);

    public Task<bool> Remove() => Task.FromResult(true);

    public Window? BuildWindow() => null;

    public Task InitAsync(ConnectContext context) => Task.CompletedTask;

    public void InitProvider(IReceiver<Container> receiver)
    {
        receiver.Required().Receiver(Components);
    }
}
