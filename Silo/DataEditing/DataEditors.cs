using Serilog;
using Silo.Commanding;
using Silo.Labs;
using Silo.Labs.Parser;
using Silo.Providers.Action;
using Silo.Providers.Data;

namespace Silo.DataEditing;

/// <summary>
///     Finds the editors for data specifications (<see cref="IDataEditor" />, found in the container so that adding one needs no change here) and
///     opens them for a specification that asks for one (<see cref="IDataSpec.Editor" />).
/// </summary>
public static class DataEditors
{
    /// <summary>Every editor the container knows, by name.</summary>
    public static IReadOnlyList<IDataEditor> All
    {
        get
        {
            try
            {
                return (App.Resolve<IEnumerable<IDataEditor>>() ?? []).OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Looking for the data editors failed");
                return [];
            }
        }
    }

    /// <summary>The editor of this kind.</summary>
    public static IDataEditor? Find(string? kind) =>
        kind is null ? null : All.FirstOrDefault(e => string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase));

    /// <summary>The editor the specification asks for, when there is one that can work with it.</summary>
    public static IDataEditor? For(IDataSpec spec) =>
        spec.Editor is { } want && Find(want.Kind) is { } editor && editor.CanEdit(spec, want) ? editor : null;

    /// <summary>Opens the specification's editor. False when it has none, or none that can work with it.</summary>
    public static bool Open(IDataSpec spec)
    {
        if (spec.Editor is not { } want || Find(want.Kind) is not { } editor) return false;

        editor.Open(spec, want);
        return true;
    }

    /// <summary>
    ///     Keeps a setting of the specification's editor (the files it last had open): in the specification, and in the Silo too when the
    ///     specification is kept there.
    /// </summary>
    public static void Remember(IDataSpec spec, string name, string value)
    {
        if (spec is not ParserDataSpec { Doc.Editor: { } editor } parser) return;
        if (editor.Settings.TryGetValue(name, out string? now) && now == value) return;

        editor.Settings[name] = value;
        try
        {
            if (SpecStore.IsAvailable && SpecStore.Exists(parser.Name)) SpecStore.Save(parser.Doc, SpecStore.LoadBook(parser.Name));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Keeping the editor settings of {Spec} failed", parser.Name);
        }
    }
}

/// <summary>On a node that is a data specification with an editor, an "Edit with ..." entry in the Node menu and the Explorer's right-click menu.</summary>
public sealed class DataEditorActions : IActionProvider<object>
{
    public IEnumerable<ProvidedCommand> GetCommands(object node)
    {
        foreach (var (_, value) in LabHost.LabsAccepting(node))
        {
            if (value is not IDataSpec { Editor: { } want } spec || DataEditors.For(spec) is not { } editor) continue;

            yield return ProvidedCommand.From("dataeditor.open." + editor.Kind, "Edit with " + (want.Title is { Length: > 0 } t ? t : editor.DisplayName), () => DataEditors.Open(spec),
                                              editor.IconKey, null, "Open the editor this data specification asks for", "Analysis", CommandPlacement.Both, MenuGroups.NodeLabCmds);
        }
    }
}
