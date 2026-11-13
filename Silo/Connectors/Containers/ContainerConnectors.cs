using Microsoft.Win32;
using Silo.Providers;
using Silo.Providers.Archive;
using Silo.Providers.Format;
using Silo.Providers.Format.Containers;
using Silo.TextEditing;
using Silo.Ui.Windows.Connecting;
using Telefrag.DI;
using IOFile = System.IO.File;

namespace Silo.Connectors.Containers;

/// <summary>
///     Opens a container file -- a zip, a tar (or gzipped tar), an ISO disc image, a cabinet -- as a connection: its folders and
///     files in the Explorer, which can be dragged out, and which files and folders can be dragged into. The location is the
///     file's path, or an <c>archive:</c> URI (see <see cref="ContainerUris" />).
/// </summary>
[Singleton(typeof(IConnector))]
public class ArchiveConnector : IConnector, IUriConnector, IConnectorUi
{
    public ConnectorSetupFlags Flags     => ConnectorSetupFlags.Connectionless;
    public Exception?          LastError { get; private set; }
    public List<object>        Metadata  { get; } = [];

    public string? Description => "The files and folders inside a zip, tar, ISO image or cabinet file";

    public IConnection CreateConnection(ConnectionRequest crq)
    {
        string path = ContainerUris.PathOf(crq.Required().RequestedUri) ?? throw new NotSupportedException("The request names no container file.");
        var format = ContainerFormats.For(path) ?? throw new NotSupportedException($"\"{Path.GetFileName(path)}\" is not a container file this connector knows.");
        if (!IOFile.Exists(path)) throw new FileNotFoundException($"\"{path}\" was not found.", path);

        return new ArchiveConnection(this, new ArchiveFile(path, format), crq.DisplayName);
    }

    public Type GetStateType() => typeof(object);

    public IConnectionState GetState(object context) => Connectors.ConnectionState.Default;

    public UriSchemeScope HandlesScheme(string scheme)
    {
        return string.Equals(scheme, ContainerUris.ArchiveScheme, StringComparison.OrdinalIgnoreCase) ? UriSchemeScope.Private :
               string.Equals(scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)           ? UriSchemeScope.AdHoc : UriSchemeScope.Unknown;
    }

    public string? Validate(Uri uri)
    {
        string? path = ContainerUris.PathOf(uri);
        if (path is null) return "That does not name a file.";
        if (ContainerFormats.For(path) is null) return $"\"{Path.GetFileName(path)}\" is not a zip, tar, ISO or cabinet file.";
        return IOFile.Exists(path) ? null : $"\"{path}\" does not exist.";
    }

    public IConnectorSetup CreateSetup(ConnectorSetupContext context)
    {
        return new UriFieldSetup(this, Check, "Container file",
                                 "Examples:  C:\\Data\\backup.zip    D:\\images\\disc.iso    \\\\server\\share\\logs.tar.gz    (New... makes an empty one)",
                                 pickers:
                                 [
                                     new UriPicker("Browse...", owner => Pick(owner, false)),
                                     new UriPicker("New...",    owner => Pick(owner, true))
                                 ]);
    }

    private UriCheck Check(string? text)
    {
        return ContainerUris.Check(text, ContainerUris.ArchiveScheme, this,
                                   path => ContainerFormats.For(path) is null ? $"\"{Path.GetFileName(path)}\" is not a zip, tar, ISO or cabinet file." : null);
    }

    /// <summary>Asks for a container file to open, or (<paramref name="create" />) where to make a new, empty one, and makes it.</summary>
    private static string? Pick(Window? owner, bool create)
    {
        if (!create)
        {
            var open = new OpenFileDialog { Title = "Open a container file", Filter = ContainerFormats.Filter(false), CheckFileExists = true };
            return (owner is null ? open.ShowDialog() : open.ShowDialog(owner)) is true ? open.FileName : null;
        }

        var writable = ContainerFormats.Writable;
        var save     = new SaveFileDialog { Title = "Make a new container file", Filter = Formats.Filter(writable), FileName = "New Archive", AddExtension = true };
        if ((owner is null ? save.ShowDialog() : save.ShowDialog(owner)) is not true) return null;

        var format = ContainerFormats.For(save.FileName) ?? writable[Math.Clamp(save.FilterIndex - 1, 0, writable.Count - 1)];
        string path = ContainerFormats.For(save.FileName) is null ? save.FileName + "." + format.FileExtensions.First().Trim() : save.FileName;
        try
        {
            format.Create(path);
            return path;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Making {Path} failed", path);
            App.Status($"{Path.GetFileName(path)} could not be made: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
///     Opens a structured data file -- JSON, YAML or XML, or a dialect of one (.csproj, .svg, .geojson...) -- as a connection: its
///     objects, arrays and values (or elements, attributes and text) in the Explorer, which can be dragged out and into. The
///     location is the file's path, or a <c>datafile:</c> URI (see <see cref="ContainerUris" />).
/// </summary>
[Singleton(typeof(IConnector))]
public class DataFileConnector : IConnector, IUriConnector, IConnectorUi
{
    public ConnectorSetupFlags Flags     => ConnectorSetupFlags.Connectionless;
    public Exception?          LastError { get; private set; }
    public List<object>        Metadata  { get; } = [];

    public string DisplayName => "Data File";

    public string? Description => "The objects, arrays and values of a JSON or YAML file, or the elements of an XML file";

    public IConnection CreateConnection(ConnectionRequest crq)
    {
        string path = ContainerUris.PathOf(crq.Required().RequestedUri) ?? throw new NotSupportedException("The request names no data file.");
        var language = ContainerFormats.DataLanguageOf(path) ?? throw new NotSupportedException($"\"{Path.GetFileName(path)}\" is not a JSON, YAML or XML file.");
        if (!IOFile.Exists(path)) throw new FileNotFoundException($"\"{path}\" was not found.", path);

        return new DataFileConnection(this, new DataDocument(path, language), crq.DisplayName);
    }

    public Type GetStateType() => typeof(object);

    public IConnectionState GetState(object context) => Connectors.ConnectionState.Default;

    public UriSchemeScope HandlesScheme(string scheme)
    {
        return string.Equals(scheme, ContainerUris.DataFileScheme, StringComparison.OrdinalIgnoreCase) ? UriSchemeScope.Private :
               string.Equals(scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)            ? UriSchemeScope.AdHoc : UriSchemeScope.Unknown;
    }

    public string? Validate(Uri uri)
    {
        string? path = ContainerUris.PathOf(uri);
        if (path is null) return "That does not name a file.";
        if (!ContainerFormats.IsDataFile(path)) return $"\"{Path.GetFileName(path)}\" is not a JSON, YAML or XML file.";
        return IOFile.Exists(path) ? null : $"\"{path}\" does not exist.";
    }

    public IConnectorSetup CreateSetup(ConnectorSetupContext context)
    {
        return new UriFieldSetup(this, Check, "Data file",
                                 "Examples:  C:\\Data\\settings.json    D:\\config\\app.yaml    C:\\src\\App.csproj    (New... makes an empty one)",
                                 pickers:
                                 [
                                     new UriPicker("Browse...", owner => Pick(owner, false)),
                                     new UriPicker("New...",    owner => Pick(owner, true))
                                 ]);
    }

    private UriCheck Check(string? text)
    {
        return ContainerUris.Check(text, ContainerUris.DataFileScheme, this,
                                   path => ContainerFormats.IsDataFile(path) ? null : $"\"{Path.GetFileName(path)}\" is not a JSON, YAML or XML file.");
    }

    private static readonly TextLanguage[] Creatable = [TextLanguage.Json, TextLanguage.Yaml, TextLanguage.Xml];

    private static string? Pick(Window? owner, bool create)
    {
        string filter = string.Join('|', Creatable.Select(l => l.FileFilter().Replace("|" + Formats.AllFiles, string.Empty)));
        if (!create)
        {
            var open = new OpenFileDialog { Title = "Open a data file", Filter = filter + "|" + Formats.AllFiles, CheckFileExists = true };
            return (owner is null ? open.ShowDialog() : open.ShowDialog(owner)) is true ? open.FileName : null;
        }

        var save = new SaveFileDialog { Title = "Make a new data file", Filter = filter, FileName = "New Data", AddExtension = true };
        if ((owner is null ? save.ShowDialog() : save.ShowDialog(owner)) is not true) return null;

        var language = ContainerFormats.DataLanguageOf(save.FileName) ?? Creatable[Math.Clamp(save.FilterIndex - 1, 0, Creatable.Length - 1)];
        try
        {
            IOFile.WriteAllText(save.FileName, DataDocument.EmptyText(language));
            return save.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Status($"{Path.GetFileName(save.FileName)} could not be made: {ex.Message}");
            return null;
        }
    }
}
