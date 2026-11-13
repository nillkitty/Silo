using Silo.Labs;
using Silo.Toolbar;

namespace Silo.Tests.Labs;

[Apartment(System.Threading.ApartmentState.STA)]
public class LabIconScopeTests
{
    /// <summary>The app's icon set (NavIcons.xaml), as the running app has it.</summary>
    private static void LoadIcons()
    {
        var app = System.Windows.Application.Current ?? new System.Windows.Application();
        if (ToolbarIcons.Find("Refresh") is not null) return;

        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/Silo;component/Resources/NavIcons.xaml") });
    }

    [Test]
    public void Different_actions_never_share_an_icon_and_the_same_action_keeps_its_own()
    {
        LoadIcons();
        Assume.That(ToolbarIcons.Find("Refresh"), Is.Not.Null, "the app's icon set cannot be read here");

        // the State Lab's actions, with the icons their authors gave (several the same)
        var scope = new LabIconScope();
        (string Text, string? Icon)[] actions =
        [
            ("_Add State", "Plus"), ("_Connect", "FlowDiagram"), ("Add Any Transition", "Plus"), ("Set _Initial", "Check"),
            ("_Delete Selected", "TbDelete"), ("_Update", "Refresh"), ("Run to _Rest", "Refresh"), ("Reset Run", "Refresh"),
            ("_Save Machine", "TbSave"), ("Copy as _JSON", "TbCopy"), ("Copy as C#", "TbCopy"), ("_Delete Saved Machine", "TbDelete"),
            ("Make Initial", "Check"), ("Delete", "TbDelete"), ("Delete Transition", "TbDelete")
        ];

        var icons = actions.Select(a => scope.For(a.Text, a.Icon)).ToList();
        Assert.That(icons, Is.Unique);
        Assert.That(icons, Has.All.Matches<string>(k => ToolbarIcons.Find(k) is not null));

        // the first to ask keeps the icon it was given; the same action asked again (any spelling of its text) gets the same one
        Assert.That(icons[0], Is.EqualTo("Plus"));
        Assert.That(icons[5], Is.EqualTo("Refresh"));
        Assert.That(scope.For("Run to Rest", "Refresh"), Is.EqualTo(icons[6]));
    }

    [Test]
    public void The_words_of_an_action_give_alternates_that_fit_it()
    {
        var updates = LabIcons.Candidates("Reset Run", "Refresh").Take(3).ToList();
        Assert.That(updates[0], Is.EqualTo("Refresh"));
        Assert.That(updates[1], Is.EqualTo("Restore"));

        Assert.That(LabIcons.Candidates("Copy as JSON", null).First(), Is.EqualTo("Code"));
        Assert.That(LabIcons.Candidates("_Delete Selected", null).First(), Is.EqualTo("TbDelete"));
    }
}
