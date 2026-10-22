using Telefrag.DI;

namespace Silo.Labs.Menus;

public static class MenuLabParameters
{
    public const string WrapXaml = "wrapXaml";
    public const string LogLines = "logLines";
}

/// <summary>
///     Makes menus the way ResEdit made MENU resources: write the entries (text, shortcut, check mark, icon, submenus, lines), try the menu out as it will be, and take it away as one
///     resource. The menu is kept as JSON, and the XAML is made from that.
/// </summary>
[Singleton(typeof(ILab))]
public class MenuLab : LabBase<MenuDoc>
{
    public override string Description => "Create and edit menus: entries with text, shortcut, icon, check mark and submenus. Try the menu out in the Lab (clicks and shortcuts are reported), " +
                                          "keep it in the Silo as JSON, and copy it out as XAML, one resource for a resource dictionary.";

    public override string IconKey => "Menu";

    public override string Id => "menu";

    public override string Name => "Menu Lab";

    public override int Order => 50;

    protected override IEnumerable<LabParameter> DefineParameters()
    {
        yield return new LabParameter(MenuLabParameters.WrapXaml, "Wrap the XAML in a resource dictionary", LabParameterKind.Bool, true,
                                      "Make the XAML a whole ResourceDictionary (ready to be a file of its own); otherwise only the menu element, to paste into one that exists.", "XAML");
        yield return new LabParameter(MenuLabParameters.LogLines, "Lines of the test log", LabParameterKind.Text, "200", "How many of the last clicks and shortcuts the test pane remembers.", "Testing", hint: "200");
    }

    public override LabSession<MenuDoc> CreateSession()
    {
        return new MenuLabSession(this);
    }

    internal bool WrapXaml => Parameters[MenuLabParameters.WrapXaml].Bool;

    internal int LogLines => int.TryParse(Parameters[MenuLabParameters.LogLines].Text, out int n) ? Math.Clamp(n, 10, 5000) : 200;

    protected override bool TryConvert(object? candidate, out MenuDoc? value)
    {
        switch (candidate)
        {
            case MenuDoc doc:
                value = doc;
                return true;
            // asked of every node when a menu is built: only the menu:Name way of referring to a kept menu is taken
            case string text when text.StartsWith("menu:", StringComparison.OrdinalIgnoreCase) && text.Length > 5:
                value = MenuLabSession.Kept.Load(text[5..].Trim());
                return value is not null;
            default:
                value = null;
                return false;
        }
    }

    /// <summary>A text is the menu's JSON, or the name of a kept menu.</summary>
    public override bool TryParse(string text, out MenuDoc? value)
    {
        if (text.TrimStart().StartsWith('{') && MenuJson.TryFromJson(text, out var doc, out _))
        {
            value = doc;
            return true;
        }

        value = MenuLabSession.Kept.Load(text.Trim());
        return value is not null;
    }
}
