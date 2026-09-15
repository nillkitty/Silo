using System.ComponentModel;
using System.Security.RightsManagement;
using System.Windows.Automation.Provider;
using Silo.Model;

namespace Silo.Connectors;

/// <summary>
/// ViewModel for a silo's preferences
/// </summary>
public class Preferences(OpenSilo s) : ModelBase
{
    public OpenSilo      Silo                { get; }      = s.Required();
    public TypeSelection DefaultConnector    { get; set; } = TypeSelection.Prompt<IConnector>();
    public TypeSelection DefaultFileFormat   { get; set; } = TypeSelection.Prompt<IFormatProvider>();
    public TypeSelection DefaultTerminal     { get; set; } = TypeSelection.Prompt<ITerminalProvider>();
    public TypeSelection DefaultScriptFormat { get; set; } = TypeSelection.Prompt<IScriptProvider>();
    public TypeSelection DefaultInspector    { get; set; } = TypeSelection.Prompt<IInspector>();
    public TypeSelection DefaultEditor       { get; set; } = TypeSelection.Prompt<IEditorProvider>();
    public TypeSelection DefaultVisualizer   { get; set; } = TypeSelection.Prompt<IVisualizer>();
    public TypeSelection DefaultSummarizer   { get; set; } = TypeSelection.Prompt<ISummarizer>();
    public TypeSelection DefaultDetailer     { get; set; } = TypeSelection.Prompt<IDetailer>();
}

public class TypeSelection<TType> : TypeSelection
{
    public bool SaveSelection { get; }
    public bool Prompt        { get; set; }

    public TypeSelection(bool prompt, bool saveSelection = true, Type? defaultType = null) : base(typeof(TType), defaultType)
    {
        Prompt        = prompt;
        DefaultType   = defaultType;
        SaveSelection = saveSelection;
    }

    public override Type? GetSelectedType()
    {
        if (SelectedType is null)
        {
            var p = PromptForType();
            if (p != null && SaveSelection)
            {
                SelectedType = p;
            }

            return p;
        }

        return SelectedType;
    }

    public Type? PromptForType()
    {
        var r  = App.Require<IReflectionProvider>();
        var tt = r.GetConstructable<TType>();
        if (tt is []) return null;
        if (tt is [Type t]) return t;

        var p = App.Require<IPrompter>();
        p.Text         = "Select a provider for this operation:";
        p.ItemSource   = tt;
        p.SelectedItem = base.GetSelectedType() ?? DefaultType;
        if (p.ShowPrompt() is true)
        {
            return p.SelectedItem as Type ?? DefaultType;
        }

        return null;
    }
}

public interface IPrompter
{
    string     Text         { get; set; }
    List<Type> ItemSource   { get; set; }
    object?    SelectedItem { get; set; }
    object     ShowPrompt();
}

public class TypeSelection(Type contractType, Type? defaultType = null, Type? initialType = null)
{
    public Type  ContractType { get; }      = contractType ?? typeof(object);
    public Type? SelectedType { get; set; } = initialType;
    public Type? DefaultType  { get; set; } = defaultType;

    public virtual Type? GetSelectedType() => SelectedType ?? DefaultType;

    public static TypeSelection<TType> Prompt<TType>(bool saveSelection = true)
    {
        return new TypeSelection<TType>(true, saveSelection);
    }
}