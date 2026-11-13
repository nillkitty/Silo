using Serilog;
using Silo.Connectors.File;
using Silo.Providers.Action;
using Silo.Providers.Archive;
using Silo.Providers.Format.Containers;
using Silo.TextEditing;
using Silo.Ui.Windows.Connecting;
using IOFile = System.IO.File;

namespace Silo.Connectors.Containers;

/// <summary>Opens container files and data files as connections of the active Silo, and makes new ones.</summary>
public static class ContainerOpen
{
    /// <summary>Whether the file can be opened as a connection here (a container file or a data file).</summary>
    public static bool CanOpen(string? path) => ContainerFormats.IsContainer(path) || ContainerFormats.IsDataFile(path);

    /// <summary>
    ///     Adds a connection for the file to the active Silo (a container file as an archive, a data file as data) and returns it;
    ///     null, with the reason in the status bar, when there is no Silo open or the file cannot be opened.
    /// </summary>
    public static IConnection? Open(string path)
    {
        if (App.Instance?.ActiveSilo is not { } silo)
        {
            App.Status("Open or create a Silo first: the file opens as one of its connections.");
            return null;
        }

        try
        {
            bool archive = ContainerFormats.IsContainer(path);
            if (!archive && !ContainerFormats.IsDataFile(path)) throw new NotSupportedException($"{Path.GetFileName(path)} is neither a container file nor a data file.");

            var connectors = ConnectorCatalog.Connectors();
            IConnector connector = archive
                                       ? connectors.OfType<ArchiveConnector>().FirstOrDefault() ?? new ArchiveConnector()
                                       : connectors.OfType<DataFileConnector>().FirstOrDefault() ?? new DataFileConnector();
            var uri        = ContainerUris.For(archive ? ContainerUris.ArchiveScheme : ContainerUris.DataFileScheme, path);
            var connection = connector.CreateConnection(new ConnectionRequest(uri) { Connector = connector });
            silo.AddConnection(new ConnectionResult(connection, Path.GetFileName(path), uri));
            App.Status($"Opened {Path.GetFileName(path)}");
            return connection;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening {Path} as a connection failed", path);
            App.Status($"{Path.GetFileName(path)} could not be opened: {ex.Message}");
            return null;
        }
    }

    /// <summary>Makes an empty container file in <paramref name="folder" /> (named so it is new there) and returns its path.</summary>
    public static string CreateContainer(string folder, IContainerFormat format)
    {
        string name = DropFiles.Unique(ContainerFormats.FileName(format, "New " + Noun(format)), n => IOFile.Exists(Path.Combine(folder, n)));
        string path = Path.Combine(folder, name);
        format.Create(path);
        return path;
    }

    /// <summary>Makes an empty data file in <paramref name="folder" /> and returns its path.</summary>
    public static string CreateDataFile(string folder, TextLanguage language)
    {
        string name = DropFiles.Unique($"New {language.DisplayName()} File.{language.DefaultExtension().TrimStart('.')}", n => IOFile.Exists(Path.Combine(folder, n)));
        string path = Path.Combine(folder, name);
        IOFile.WriteAllText(path, DataDocument.EmptyText(language));
        return path;
    }

    private static string Noun(IContainerFormat format)
    {
        string name = format.DisplayName ?? string.Empty;
        return name.EndsWith(" Archive", StringComparison.Ordinal) ? "Archive" : name.EndsWith(" Image", StringComparison.Ordinal) ? "Disc Image" : "Cabinet";
    }
}

/// <summary>
///     The commands of a file system item for container and data files: a zip, tar, ISO or cabinet opens as an archive
///     connection, a JSON, YAML or XML file as a data connection, and a folder can have new ones made in it.
/// </summary>
public class ContainerFileActions : IActionProvider<FileModel>
{
    private const string FileGroup      = "File";
    private const string DirectoryGroup = "Directory";

    private static readonly TextLanguage[] DataLanguages = [TextLanguage.Json, TextLanguage.Yaml, TextLanguage.Xml];

    public IEnumerable<ProvidedCommand> GetCommands(FileModel f)
    {
        if (f.Kind == FileKind.File)
        {
            if (ContainerFormats.IsContainer(f.Path))
                yield return ProvidedCommand.From("file.open.archive", "Open as _Archive Connection", () => ContainerOpen.Open(f.Path), "Archive",
                                                  toolTip: "Shows the files and folders inside it in the Explorer, to drag out of and into", group: FileGroup,
                                                  placement: CommandPlacement.Menu);
            else if (ContainerFormats.IsDataFile(f.Path))
                yield return ProvidedCommand.From("file.open.data", "Open as _Data Connection", () => ContainerOpen.Open(f.Path), "DataStructure",
                                                  toolTip: "Shows its contents as a tree in the Explorer, to drag out of and into", group: FileGroup,
                                                  placement: CommandPlacement.Menu);
            yield break;
        }

        if (!f.IsContainer || f.IsLink) yield break;

        var items = new List<ProvidedCommand>();
        foreach (var format in ContainerFormats.Writable)
        {
            var fmt = format;
            items.Add(ProvidedCommand.From("file.new." + fmt.FileExtensions.First().Trim(), fmt.DisplayName ?? fmt.FileExtension, () => Make(f, () => ContainerOpen.CreateContainer(f.Path, fmt)),
                                           "Archive", toolTip: $"Makes an empty {fmt.DisplayName} here and opens it", placement: CommandPlacement.Menu));
        }

        foreach (var language in DataLanguages)
        {
            var lang = language;
            items.Add(ProvidedCommand.From("file.new." + lang.ToString().ToLowerInvariant(), lang.DisplayName() + " File", () => Make(f, () => ContainerOpen.CreateDataFile(f.Path, lang)),
                                           "DataStructure", toolTip: $"Makes an empty {lang.DisplayName()} file here and opens it", group: "Data", placement: CommandPlacement.Menu));
        }

        yield return ProvidedCommand.Submenu("file.new", "Ne_w", items, DirectoryGroup, "Archive", "Makes a new archive, disc image or data file in this folder");
    }

    private static void Make(FileModel folder, Func<string> create)
    {
        try
        {
            string path = create();
            _ = folder.RefreshAsync();
            ContainerOpen.Open(path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Making a file in {Folder} failed", folder.Path);
            App.Status($"The file could not be made: {ex.Message}");
        }
    }
}
