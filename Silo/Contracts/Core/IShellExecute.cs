namespace Silo.Contracts.Core;

/// <summary>
///     Hands things to the operating system's shell, as double-clicking them in Windows Explorer would. A failure (nothing
///     is associated with the file type, the file is gone, access denied) is reported to the person with
///     <c>App.Alert</c> and logged; it is not thrown. A person who cancels the "Run as administrator" prompt is not an
///     error.
/// </summary>
[RequireRegistration]
public interface IShellExecute
{
    /// <summary>
    ///     Opens a file with the program Windows has for its type, a folder in Windows Explorer, or a program (or URL) by
    ///     running it.
    /// </summary>
    Task ShellExecute(string path);

    /// <summary>
    ///     Does one of the shell's verbs to <paramref name="path" />: <c>open</c>, <c>openas</c> (the "Open with" chooser),
    ///     <c>properties</c> (the Windows properties dialog), <c>runas</c>, <c>edit</c>, <c>print</c>, <c>explore</c>...
    /// </summary>
    Task ShellExecute(string path, string verb, string? arguments = null);

    /// <summary>
    ///     Opens a shell object by its id: a known folder (Downloads, Documents... see the <c>FOLDERID_</c> constants) opens
    ///     as that folder, and any other id as a shell namespace object (<c>shell:::{id}</c> -- the Control Panel, This
    ///     PC...).
    /// </summary>
    Task ShellExecute(Guid shellObject);

    /// <summary>
    ///     Shows <paramref name="path" /> in a Windows Explorer window: a folder is opened, anything else is selected in
    ///     its folder.
    /// </summary>
    Task Reveal(string path);

    /// <summary>Starts a program (a terminal, say) without going through file associations.</summary>
    Task Start(string program, string? arguments = null, string? workingDirectory = null);
}