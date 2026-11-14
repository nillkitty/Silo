using Silo.Commanding;
using Silo.Providers.Action;

namespace Silo.ExternalTools;

/// <summary>
///     On a node that stands for a target external tools are registered for (<see cref="ExternalTools.TargetsOf" />), the
///     tools in the Node menu and the Explorer's right-click menu: the featured ones (Ping in Terminal) first, then a submenu
///     per section (In Terminal, In Browser, Other Tools, Copy Hash). A node standing for several targets gets a submenu per
///     target holding those.
/// </summary>
public sealed class ExternalToolActions : IActionProvider<object>
{
    private const string Group = "External Tools";

    public IEnumerable<ProvidedCommand> GetCommands(object node)
    {
        var targets = ExternalTools.TargetsOf(node);
        if (targets.Count == 1) return CommandsFor(targets[0]);

        return targets.Select(t => (Target: t, Commands: CommandsFor(t).ToList()))
                      .Where(t => t.Commands.Count > 0)
                      .Select((t, i) => ProvidedCommand.Submenu($"external.{Kind(t.Target)}.{i}", $"Tools for {t.Target}", t.Commands, Group,
                                                                t.Commands[0].IconKey))
                      .ToList();
    }

    /// <summary>The entries for the tools that apply to one target.</summary>
    public static IEnumerable<ProvidedCommand> CommandsFor(object target)
    {
        var    tools = ExternalTools.For(target);
        string kind  = Kind(target);

        foreach (var tool in tools.Where(t => t.Featured))
            yield return Entry(tool, target, $"external.{kind}.featured.{tool.Id}", tool.Header + tool.Section.Where);

        foreach (var section in tools.GroupBy(t => t.Section).OrderBy(g => g.Key.Order))
            yield return ProvidedCommand.Submenu($"external.{kind}.{section.Key.Id}", section.Key.Header,
                                                 section.Select(t => Entry(t, target, $"external.{kind}.{t.Id}", t.Header)), Group,
                                                 section.Key.IconKey);
    }

    private static ProvidedCommand Entry(ExternalTool tool, object target, string id, string header)
    {
        bool   available = tool.IsAvailable;
        string toolTip   = available ? tool.ToolTip : $"{tool.ToolTip} ({tool.RequiresProgram} was not found on the PATH)";

        return ProvidedCommand.From(id, header, () => _ = ExternalToolLauncher.LaunchAsync(tool, target), tool.IconKey,
                                    available ? null : () => false, toolTip, Group, CommandPlacement.Menu);
    }

    /// <summary>The kind of target, for menu ids: "ipaddress", "domainname", "fileinfo"...</summary>
    private static string Kind(object target) => target.GetType().Name.ToLowerInvariant();
}
