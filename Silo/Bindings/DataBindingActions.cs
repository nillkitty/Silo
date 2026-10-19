using Silo.Model;
using DataBinding = Silo.DbModel.Global.DataBinding;

namespace Silo.Bindings;

/// <summary>
///     Creating, editing and deleting a Silo's data bindings (the Explorer's menus).
/// </summary>
public static class DataBindingActions
{
    /// <summary>The binding store of a Silo, or null (with the reason shown) when it cannot hold bindings.</summary>
    public static IDataBindingStore? StoreOf(OpenSilo? silo, bool needWritable = true)
    {
        if (silo is null)
        {
            App.Status("Open a Silo first");
            return null;
        }

        if (silo.Data is not IDataBindingStore store)
        {
            App.Status("This Silo cannot hold data bindings");
            return null;
        }

        if (needWritable && !silo.IsWritable)
        {
            App.Status("This Silo is read-only");
            return null;
        }

        return store;
    }

    public static void New(OpenSilo? silo)
    {
        if (StoreOf(silo) is not { } store) return;

        var existing = store.ListDataBindings();
        var draft = new DataBinding
                    {
                        Name   = $"Value{existing.Count + 1}",
                        Source = BindingSources.All.SelectMany(s => s.Examples).FirstOrDefault() ?? ""
                    };

        if (DataBindingWindow.Edit(draft, name => existing.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) is not { } made) return;
        Save(store, made);
    }

    public static void Edit(OpenSilo? silo, int id)
    {
        if (StoreOf(silo) is not { } store) return;
        if (store.GetDataBinding(id) is not { } binding)
        {
            App.Status("That data binding no longer exists");
            return;
        }

        var others = store.ListDataBindings().Where(b => b.Id != id).ToList();
        if (DataBindingWindow.Edit(binding, name => others.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) is not { } edited) return;
        Save(store, edited);
    }

    public static void Delete(OpenSilo? silo, int id)
    {
        if (StoreOf(silo) is not { } store) return;

        string name = store.GetDataBinding(id)?.Name ?? "this data binding";
        var answer = MessageBox.Show($"Delete the data binding \"{name}\" from this Silo?\n\nDiagram cells that show {{bind:{name}}} will show ? instead.",
                                     AppIdentity.AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            store.DeleteDataBinding(id);
        }
        catch (Exception ex)
        {
            App.Status("Could not delete the data binding: " + ex.Message);
            return;
        }

        DataBindingEvents.RaiseChanged();
    }

    /// <summary>Puts <c>{bind:Name}</c> on the clipboard, ready for a cell's <c>silo-label</c>.</summary>
    public static void CopyToken(string name)
    {
        Clipboard.SetText("{bind:" + name + "}");
        App.Status($"Copied {{bind:{name}}}");
    }

    private static void Save(IDataBindingStore store, DataBinding binding)
    {
        try
        {
            store.SaveDataBinding(binding);
        }
        catch (Exception ex)
        {
            App.Status("Could not save the data binding: " + ex.Message);
            return;
        }

        DataBindingEvents.RaiseChanged();
    }
}
