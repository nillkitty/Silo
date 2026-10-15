using Diagram = Silo.DbModel.Global.Diagram;
using Silo.Model;
using Silo.Tools;
using Silo.Ui.Windows;

namespace Silo.Drawio;

/// <summary>
///     What can be done to the diagrams of a Silo from outside the canvas tool (the Explorer's menus): open, create, rename,
///     delete. Editing itself stays in <see cref="DrawioCanvasTool" />.
/// </summary>
public static class DiagramActions
{
    /// <summary>The diagram store of a Silo, or null (with the reason shown) when it cannot hold diagrams.</summary>
    public static IDiagramStore? StoreOf(OpenSilo? silo, bool needWritable = true)
    {
        if (silo is null)
        {
            App.Status("Open a Silo first");
            return null;
        }

        if (silo.Data is not IDiagramStore store)
        {
            App.Status("This Silo cannot hold diagrams");
            return null;
        }

        if (needWritable && !silo.IsWritable)
        {
            App.Status("This Silo is read-only");
            return null;
        }

        return store;
    }

    /// <summary>Opens a diagram in the Diagram Canvas (making its Silo the active one).</summary>
    public static void Open(OpenSilo? silo, int id)
    {
        if (DrawioCanvasTool.Current is not { } tool)
        {
            App.Status("The diagram canvas is not available");
            return;
        }

        if (silo is not null && App.Instance is { } app && !ReferenceEquals(app.ActiveSilo, silo))
            app.ActiveSilo = silo;

        tool.ShowDiagram(id);
    }

    /// <summary>Creates an empty diagram (asking for its name) and opens it.</summary>
    public static void New(OpenSilo? silo)
    {
        if (StoreOf(silo) is not { } store) return;

        string? name = PromptDialog.Ask("New diagram", "Name:", "Create", $"Diagram {store.ListDiagrams().Count + 1}");
        if (name is null) return;

        Diagram created;
        try
        {
            created = store.SaveDiagram(new Diagram { Name = name.Trim(), Kind = "canvas", Profile = DrawioProfiles.Canvas, Xml = DrawioDocument.EmptyXml });
        }
        catch (Exception ex)
        {
            App.Status("Could not create the diagram: " + ex.Message);
            return;
        }

        DiagramEvents.RaiseChanged();
        Open(silo, created.Id);
    }

    public static void Rename(OpenSilo? silo, int id)
    {
        if (StoreOf(silo) is not { } store) return;
        if (store.GetDiagram(id) is not { } diagram)
        {
            App.Status("That diagram no longer exists");
            return;
        }

        string? name = PromptDialog.Ask("Rename diagram", "Name:", "Rename", diagram.Name);
        if (name is null || name.Trim() == diagram.Name) return;

        var renamed = diagram.Clone();
        renamed.Name     = name.Trim();
        renamed.Modified = DateTime.UtcNow;
        try
        {
            store.SaveDiagram(renamed);
        }
        catch (Exception ex)
        {
            App.Status("Could not rename the diagram: " + ex.Message);
            return;
        }

        DiagramEvents.RaiseChanged();
    }

    public static void Delete(OpenSilo? silo, int id)
    {
        if (StoreOf(silo) is not { } store) return;

        string name = store.GetDiagram(id)?.Name ?? "this diagram";
        var answer = MessageBox.Show($"Delete the diagram \"{name}\" from this Silo?\n\nThis cannot be undone (other than by not saving the Silo).",
                                     AppIdentity.AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            store.DeleteDiagram(id);
        }
        catch (Exception ex)
        {
            App.Status("Could not delete the diagram: " + ex.Message);
            return;
        }

        DiagramEvents.RaiseChanged();
    }
}
