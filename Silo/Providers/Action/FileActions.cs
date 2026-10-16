using Silo.Commanding;
using Silo.Connectors.File;

namespace Silo.Providers.Action;

/// <summary>
///     Not the interface to implement -- see <see cref="IActionProvider{TNodeType}" />. This is what lets
///     <see cref="ActionProviders" /> ask a provider without knowing its type argument.
/// </summary>
public interface IActionProvider
{
    /// <summary>The commands for <paramref name="node" />; none when it is not the provider's type.</summary>
    IEnumerable<ProvidedCommand> GetCommands(object node);
}

/// <summary>
///     Service plugin to register action commands on nodes of type
///     <typeparam name="TNodeType" />
///     <para>
///         <see cref="ActionProviders" /> finds the public classes that implement it (with a parameterless constructor) and, for the model of
///         the selected Explorer node, asks every provider of its type, its base types and its interfaces, most specific first.
///         The commands appear in the Node menu and the Explorer's right-click menu, and -- those placed there -- on the toolbar.
///         <see cref="ProvidedCommand.Group" /> names the section of the menu a command goes in.
///     </para>
/// </summary>
/// <typeparam name="TNodeType">Base type upon which actions are registered</typeparam>
public interface IActionProvider<in TNodeType> : IActionProvider
{
    IEnumerable<ProvidedCommand> GetCommands(TNodeType node);

    IEnumerable<ProvidedCommand> IActionProvider.GetCommands(object node)
    {
        return node is TNodeType typed ? GetCommands(typed) : [];
    }
}

/// <summary>
///     The commands of a file system item (<see cref="FileModel" />): sections Directory or File or Link, then Contents, then
///     Location, as a folder, a file or a link needs them. The commands themselves are in
///     <c>Commanding/FileCommands.cs</c>.
/// </summary>
public class FileActions : IActionProvider<FileModel>
{
    private const string DirectoryGroup = "Directory";
    private const string FileGroup      = "File";
    private const string LinkGroup      = "Link";
    private const string ContentsGroup  = "Contents";
    private const string LocationGroup  = "Location";
    private const string MeaningGroup   = "Meaning";
    private const string SiloGroup      = "Silo";

    public IEnumerable<ProvidedCommand> GetCommands(FileModel f)
    {
        bool isLink = f.IsLink;
        bool folder = f.IsContainer;

        if (isLink)
        {
            yield return ProvidedCommand.Of("file.link.reveal", new RevealLinkTargetCommand(f), null, LinkGroup, CommandPlacement.Both, isDefault: true);
            if (f.LinkTarget is { Length: > 0 } target)
                yield return LinkDestination(target);
            yield return ProvidedCommand.Of("file.refresh", new RefreshFileCommand(f, false), null, LinkGroup, gesture: "F5");
        }

        if (folder)
        {
            if (!isLink)
                yield return ProvidedCommand.Of("file.reveal", new RevealFileCommand(f), null, DirectoryGroup, CommandPlacement.Both, isDefault: true);
            yield return ProvidedCommand.Of("file.terminal", new OpenTerminalFileCommand(f), null, DirectoryGroup);
            yield return ProvidedCommand.Of("file.refresh.contents", new RefreshFileCommand(f, true), null, ContentsGroup, gesture: "F5");
        }
        else if (!isLink)
        {
            yield return ProvidedCommand.Of("file.launch", new LaunchFileCommand(f), null, FileGroup, CommandPlacement.Both, isDefault: true);
            yield return ProvidedCommand.Of("file.openwith", new OpenWithFileCommand(f), null, FileGroup);
            yield return ProvidedCommand.Of("file.reveal", new RevealFileCommand(f), null, FileGroup);
            yield return ProvidedCommand.Of("file.refresh", new RefreshFileCommand(f, false), null, FileGroup, gesture: "F5");
        }

        foreach (var planned in Planned(f, folder))
            yield return planned;

        yield return ProvidedCommand.Of("file.copypath", new CopyPathFileCommand(f), null, LocationGroup, gesture: "Ctrl+Shift+C");
        yield return ProvidedCommand.Of("file.copyuri", new CopyUriFileCommand(f), null, LocationGroup, gesture: "Ctrl+C");
    }

    /// <summary>
    ///     Disabled placeholders for the menu items that have no feature behind them yet (<see cref="PlannedFileCommand" />). Each
    ///     is replaced by a real command here when its feature is built.
    /// </summary>
    private static IEnumerable<ProvidedCommand> Planned(FileModel f, bool folder)
    {
        if (folder)
        {
            yield return Plan(f, "file.go.terminal", "_Go here in Terminal", "Opens a terminal in this folder and keeps it following the Explorer selection", DirectoryGroup);
            yield return Plan(f, "file.find", "_Find...", "Searches this folder's contents by name, type and text", DirectoryGroup);
        }

        yield return Plan(f, "file.preview", "_Preview Contents", "Shows the item's contents in the preview pane without opening a document", ContentsGroup);
        yield return Plan(f, "file.watch", "_Watch for Changes", "Keeps the Explorer and open documents up to date as the item changes on disk", ContentsGroup, isChecked: () => false);

        yield return Plan(f, "file.treatas", "_Treat as Type...", "Reads the item as a chosen type instead of the one its extension suggests", MeaningGroup);
        yield return Plan(f, "file.role", "R_ole", "Shows the role the item plays in the Silo", MeaningGroup);
        yield return Plan(f, "file.applyrole", "_Apply Role...", "Gives the item a role, so the Silo names and joins it by what it is for", MeaningGroup);

        yield return Plan(f, "file.copytosilo", "Copy to _Silo", "Brings a copy of the item into the Silo's own storage", SiloGroup);
        yield return Plan(f, "file.import", "_Import...", "Imports the item's contents into the Silo as tables or documents", SiloGroup);
        yield return Plan(f, "file.exportas", "E_xport As...", "Saves the item's contents in another format", SiloGroup);
        yield return Plan(f, "file.bookmark", "_Bookmark Location", "Keeps a shortcut to this place in the Silo", SiloGroup);
        yield return Plan(f, "file.tag", "_Tag...", "Labels the item with your own tags", SiloGroup);

        yield return Plan(f, "file.properties", "Pro_perties", "Shows the item's details, type, role and tags", ProvidedCommand.LastGroup, "Alt+Enter");
    }

    private static ProvidedCommand Plan(FileModel f, string id, string header, string plan, string? group, string? gesture = null,
                                        Func<bool>? isChecked = null)
    {
        return ProvidedCommand.Of(id, new PlannedFileCommand(f, header, plan), null, group, gesture: gesture, isChecked: isChecked);
    }

    /// <summary>The Link Destination submenu: what can be done with the place the link points to.</summary>
    private static ProvidedCommand LinkDestination(string target)
    {
        var destination = new FileModel(target);
        return ProvidedCommand.Submenu("file.link.destination", "Link _Destination",
                                       [
                                           ProvidedCommand.Of("file.link.destination.reveal", new RevealFileCommand(destination)),
                                           ProvidedCommand.Of("file.link.destination.copypath", new CopyPathFileCommand(destination)),
                                           ProvidedCommand.Of("file.link.destination.copyuri", new CopyUriFileCommand(destination))
                                       ], LinkGroup, toolTip: "The place the link points to");
    }
}
