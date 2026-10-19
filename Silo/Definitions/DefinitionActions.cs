using EnumType = Silo.DbModel.Meta.EnumType;
using Silo.Model;
using Silo.Providers.Definitions;
using Silo.Ui.Windows;

namespace Silo.Definitions;

/// <summary>
///     What can be done to the enums and syntaxes of a Silo from the screens (the lists and the Explorer's menus): create, edit, delete, and for enums
///     add, change and remove a single value. A change that cannot be made says why in the status bar or a message.
/// </summary>
public static class DefinitionActions
{
    private const string EnumTitle   = "Enums";
    private const string SyntaxTitle = "Syntaxes";

    /// <summary>The enum store of a Silo, or null (with the reason shown) when it cannot hold enums.</summary>
    public static IEnumStore? EnumsOf(OpenSilo? silo, bool needWritable = true)
    {
        if (silo is null)
        {
            App.Status("Open a Silo first");
            return null;
        }

        if (silo.Data is not IEnumStore store)
        {
            App.Status("This Silo cannot hold enums");
            return null;
        }

        if (needWritable && !silo.IsWritable)
        {
            App.Status("This Silo is read-only");
            return null;
        }

        return store;
    }

    /// <summary>The syntax store of a Silo, or null (with the reason shown) when it cannot hold syntaxes.</summary>
    public static ISyntaxStore? SyntaxesOf(OpenSilo? silo, bool needWritable = true)
    {
        if (silo is null)
        {
            App.Status("Open a Silo first");
            return null;
        }

        if (silo.Data is not ISyntaxStore store)
        {
            App.Status("This Silo cannot hold syntaxes");
            return null;
        }

        if (needWritable && !silo.IsWritable)
        {
            App.Status("This Silo is read-only");
            return null;
        }

        return store;
    }

    private static void Problem(string title, string text) => MessageBox.Show(text, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    // ---- enums ---------------------------------------------------------------------------------------------------

    public static void NewEnum(OpenSilo? silo)
    {
        if (EnumsOf(silo) is not { } store) return;
        if (EnumWindow.Prompt(store, null) is not null) DefinitionEvents.RaiseChanged();
    }

    public static void EditEnum(OpenSilo? silo, int id)
    {
        if (EnumsOf(silo) is not { } store) return;
        if (store.GetEnum(id) is null)
        {
            App.Status("That enum no longer exists");
            return;
        }

        if (EnumWindow.Prompt(store, id) is not null) DefinitionEvents.RaiseChanged();
    }

    public static void DeleteEnum(OpenSilo? silo, int id)
    {
        if (EnumsOf(silo) is not { } store) return;

        var e = store.GetEnum(id);
        string name = e?.Name ?? "this enum";
        string values = e is { Values.Count: > 0 } ? $" and its {e.Values.Count} value{(e.Values.Count == 1 ? string.Empty : "s")}" : string.Empty;
        if (MessageBox.Show($"Delete the enum \"{name}\"{values} from this Silo?\n\nAnything that refers to enum:{name} will no longer find it.", "Delete enum",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            store.DeleteEnum(id);
        }
        catch (Exception ex)
        {
            App.Status("Could not delete the enum: " + ex.Message);
            return;
        }

        DefinitionEvents.RaiseChanged();
    }

    /// <summary>Asks for the label and the value of a new value of the enum (the next value is suggested) and adds it.</summary>
    public static void AddValue(OpenSilo? silo, int enumId)
    {
        if (EnumsOf(silo) is not { } store) return;
        if (store.GetEnum(enumId) is not { } e)
        {
            App.Status("That enum no longer exists");
            return;
        }

        string? label = PromptDialog.Ask("Add value", $"Label of the new value of \"{e.Name}\":", "Next");
        if (label is null) return;

        string? value = PromptDialog.Ask("Add value", $"Value of \"{label}\" ({ValueHint(e.Type)}):", "Add", EnumRules.SuggestNextValue(e));
        if (value is null) return;

        SaveValue(store, enumId, new EnumValueRecord(0, label, value));
    }

    /// <summary>Asks for a new label and value for one value of the enum and saves them.</summary>
    public static void EditValue(OpenSilo? silo, int enumId, int valueId)
    {
        if (EnumsOf(silo) is not { } store) return;
        if (store.GetEnum(enumId) is not { } e || e.Values.FirstOrDefault(v => v.Id == valueId) is not { } current)
        {
            App.Status("That value no longer exists");
            return;
        }

        string? label = PromptDialog.Ask("Edit value", $"Label of this value of \"{e.Name}\":", "Next", current.Name);
        if (label is null) return;

        string? value = PromptDialog.Ask("Edit value", $"Value of \"{label}\" ({ValueHint(e.Type)}):", "Save", current.Value ?? string.Empty);
        if (value is null) return;

        SaveValue(store, enumId, current with { Name = label, Value = value });
    }

    private static void SaveValue(IEnumStore store, int enumId, EnumValueRecord value)
    {
        try
        {
            store.SaveEnumValue(enumId, value);
        }
        catch (DefinitionException ex)
        {
            Problem(EnumTitle, ex.Message);
            return;
        }
        catch (Exception ex)
        {
            App.Status("Could not save the value: " + ex.Message);
            return;
        }

        DefinitionEvents.RaiseChanged();
    }

    public static void DeleteValue(OpenSilo? silo, int enumId, int valueId)
    {
        if (EnumsOf(silo) is not { } store) return;

        var value = store.GetEnum(enumId)?.Values.FirstOrDefault(v => v.Id == valueId);
        if (MessageBox.Show($"Delete the value \"{value?.Name ?? "this value"}\" from this enum?", "Delete value", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            store.DeleteEnumValue(valueId);
        }
        catch (Exception ex)
        {
            App.Status("Could not delete the value: " + ex.Message);
            return;
        }

        DefinitionEvents.RaiseChanged();
    }

    /// <summary>What a value of the enum type looks like, short enough to follow a question.</summary>
    public static string ValueHint(EnumType type) => type switch
    {
        EnumType.Integral => "a whole number, or hex like 0x1F",
        EnumType.Signed   => "a whole number",
        EnumType.Float    => "a number",
        EnumType.Byte     => "0 to 255",
        EnumType.Bool     => "true or false",
        EnumType.Guid     => "a guid",
        _                 => "text"
    };

    // ---- syntaxes ------------------------------------------------------------------------------------------------

    public static void NewSyntax(OpenSilo? silo)
    {
        if (SyntaxesOf(silo) is not { } store) return;
        if (SyntaxWindow.Prompt(store, null) is not null) DefinitionEvents.RaiseChanged();
    }

    public static void EditSyntax(OpenSilo? silo, int id)
    {
        if (SyntaxesOf(silo) is not { } store) return;
        if (store.GetSyntax(id) is null)
        {
            App.Status("That syntax no longer exists");
            return;
        }

        if (SyntaxWindow.Prompt(store, id) is not null) DefinitionEvents.RaiseChanged();
    }

    public static void DeleteSyntax(OpenSilo? silo, int id)
    {
        if (SyntaxesOf(silo) is not { } store) return;

        var s = store.GetSyntax(id);
        string name = s?.Name ?? "this syntax";
        string predefined = s is { IsSeeded: true } ? "\n\nIt is one of the predefined syntaxes; Restore Defaults brings it back." : string.Empty;
        if (MessageBox.Show($"Delete the syntax \"{name}\" from this Silo?\n\nAnything that refers to syntax:{name} will no longer find it.{predefined}", "Delete syntax",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            store.DeleteSyntax(id);
        }
        catch (Exception ex)
        {
            App.Status("Could not delete the syntax: " + ex.Message);
            return;
        }

        DefinitionEvents.RaiseChanged();
    }

    /// <summary>Adds back the predefined syntaxes that were deleted (or renamed away), and says how many.</summary>
    public static void RestoreDefaults(OpenSilo? silo)
    {
        if (SyntaxesOf(silo) is not { } store) return;

        int added;
        try
        {
            added = store.RestoreDefaults();
        }
        catch (Exception ex)
        {
            App.Status("Could not restore the predefined syntaxes: " + ex.Message);
            return;
        }

        App.Status(added == 0 ? "All the predefined syntaxes are already there" : $"Restored {added} predefined {(added == 1 ? "syntax" : "syntaxes")}");
        if (added > 0) DefinitionEvents.RaiseChanged();
    }
}
