namespace Silo.ExternalTools;

/// <summary>
///     A submenu external tools are put in: In Terminal, In Browser, Other, Copy Hash... Sections are listed in the order
///     <see cref="Order" /> says, whatever order their tools were registered in.
/// </summary>
/// <param name="Id">Identifies the section; part of the ids of its menu entries.</param>
/// <param name="Header">The submenu's text (a leading "_" before a letter makes it the access key).</param>
/// <param name="IconKey">The key of the submenu's icon.</param>
/// <param name="Order">Where the section goes among the others: lower first.</param>
/// <param name="Where">
///     Said after the header of a featured tool, which is offered outside the section ("in Terminal": "Ping in Terminal");
///     empty when nothing needs saying.
/// </param>
public sealed record ExternalToolSection(string Id, string Header, string IconKey, int Order, string Where = "")
{
    public static readonly ExternalToolSection Terminal = new("terminal", "In _Terminal", ExternalToolIcons.Terminal, 10, " in Terminal");
    public static readonly ExternalToolSection Browser  = new("browser",  "In _Browser",  ExternalToolIcons.Browser,  20, " in Browser");
    public static readonly ExternalToolSection Other    = new("other",    "_Other Tools", ExternalToolIcons.Other,    30);
    public static readonly ExternalToolSection CopyHash = new("hash",     "Copy _Hash",   ExternalToolIcons.CopyHash, 40);
}

/// <summary>
///     What running an external tool does: a command in a terminal window, a page in the browser, a program, or work done in
///     Silo. A tool's <see cref="ExternalTool.Run" /> only says which; <see cref="ExternalToolLauncher" /> does it, so what a
///     tool would do can be checked without doing it.
/// </summary>
public abstract record ExternalToolRun
{
    /// <summary>A command line run by <c>cmd.exe</c> in a terminal window that stays open when it is done.</summary>
    /// <param name="Title">The terminal tab's title.</param>
    /// <param name="CommandLine">The command, as typed at a command prompt.</param>
    public sealed record Terminal(string Title, string CommandLine) : ExternalToolRun;

    /// <summary>A PowerShell script run in a terminal window that stays open when it is done.</summary>
    /// <param name="Title">The terminal tab's title.</param>
    /// <param name="Script">The script.</param>
    /// <param name="Elevated">Run as administrator (the person is asked by UAC); for the event logs and the firewall's log.</param>
    public sealed record PowerShell(string Title, string Script, bool Elevated = false) : ExternalToolRun;

    /// <summary>A web page, opened in the default browser.</summary>
    public sealed record Browser(Uri Url) : ExternalToolRun;

    /// <summary>A program started with arguments, with no window of Silo's around it (Remote Desktop, say).</summary>
    public sealed record Program(string Path, string Arguments) : ExternalToolRun;

    /// <summary>Work Silo does itself (computing a hash and copying it, say).</summary>
    /// <param name="Description">What the work is, for the log and error messages.</param>
    /// <param name="Work">The work.</param>
    public sealed record InApp(string Description, Func<Task> Work) : ExternalToolRun;
}

/// <summary>
///     A tool registered for a type of target (<see cref="TargetType" />: <see cref="System.Net.IPAddress" />,
///     <see cref="DomainName" />, <see cref="FileInfo" />...) with <see cref="ExternalTools.Register{T}" />. Whatever the
///     Explorer has selected that stands for such a target (see <see cref="ExternalTools.RegisterTarget{TModel}" />) offers
///     the tool in its Node menu and right-click menu, in the submenu of its <see cref="Section" />.
/// </summary>
/// <param name="Id">Identifies the tool among those of its target type; stable across runs.</param>
/// <param name="Header">The menu entry's text.</param>
/// <param name="IconKey">The key of its icon; no two tools of a target type share one.</param>
/// <param name="Section">The submenu it goes in.</param>
/// <param name="ToolTip">What it does, in a sentence.</param>
/// <param name="TargetType">The type of target it is for.</param>
/// <param name="Run">What it does for a target (always given one of <see cref="TargetType" />).</param>
/// <param name="When">Whether it applies to a target (an IPv4-only tool, say); null: always.</param>
/// <param name="RequiresProgram">
///     A program the tool runs that does not come with Windows ("nmap.exe"): the entry is disabled, and says so, when it is
///     not found on the PATH.
/// </param>
/// <param name="Featured">Also offered at the top of the menu, outside its section, for quick access.</param>
public sealed record ExternalTool(
    string                      Id,
    string                      Header,
    string                      IconKey,
    ExternalToolSection         Section,
    string                      ToolTip,
    Type                        TargetType,
    Func<object, ExternalToolRun> Run,
    Func<object, bool>?         When            = null,
    string?                     RequiresProgram = null,
    bool                        Featured        = false)
{
    /// <summary>Whether the tool applies to <paramref name="target" />: it is of the tool's type and <see cref="When" /> agrees.</summary>
    public bool AppliesTo(object target)
    {
        return TargetType.IsInstanceOfType(target) && (When is null || When(target));
    }

    /// <summary>Whether the program the tool needs, if any, is there to run.</summary>
    public bool IsAvailable => RequiresProgram is null || ExternalToolLauncher.OnPath(RequiresProgram);
}
