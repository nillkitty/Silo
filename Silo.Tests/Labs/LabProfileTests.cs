using Silo.Labs;

namespace Silo.Tests.Labs;

[NonParallelizable]
public class LabProfileTests
{
    private string _file = string.Empty;

    [SetUp]
    public void Point()
    {
        _file                  = Path.Combine(Path.GetTempPath(), "silo-labs-" + Guid.NewGuid().ToString("N") + ".json");
        LabProfile.PathOverride = _file;
        LabProfile.Reset();
    }

    [TearDown]
    public void Release()
    {
        LabProfile.PathOverride = null;
        LabProfile.Reset();
        if (File.Exists(_file)) File.Delete(_file);
    }

    [Test]
    public void Recent_values_come_newest_first_and_move_up_when_used_again()
    {
        LabProfile.Remember("web", "one");
        LabProfile.Remember("web", "two");
        LabProfile.Remember("web", "one");
        LabProfile.Remember("qr", "other");

        Assert.That(LabProfile.Recent("web"), Is.EqualTo(new[] { "one", "two" }));
        Assert.That(LabProfile.Recent("qr"), Is.EqualTo(new[] { "other" }));
        Assert.That(LabProfile.Recent("none"), Is.Empty);
    }

    [Test]
    public void Blank_values_are_not_remembered_and_the_list_has_a_limit()
    {
        LabProfile.Remember("web", "  ");
        for (int i = 0; i < LabProfile.RecentLimit + 10; i++) LabProfile.Remember("web", "v" + i);

        var recent = LabProfile.Recent("web");
        Assert.That(recent, Has.Count.EqualTo(LabProfile.RecentLimit));
        Assert.That(recent[0], Is.EqualTo("v" + (LabProfile.RecentLimit + 9)));
    }

    [Test]
    public void Forgetting_and_clearing_remove_values()
    {
        LabProfile.Remember("web", "a");
        LabProfile.Remember("web", "b");
        LabProfile.Forget("web", "a");
        Assert.That(LabProfile.Recent("web"), Is.EqualTo(new[] { "b" }));

        LabProfile.ClearRecent("web");
        Assert.That(LabProfile.Recent("web"), Is.Empty);
    }

    [Test]
    public void Presets_are_kept_by_lab_and_kind_and_survive_a_restart()
    {
        LabProfile.SavePreset("web", "network-filter", "Only failures", new Dictionary<string, string> { ["text"] = "is:failed", ["type"] = "All" });
        LabProfile.SavePreset("web", "network-filter", "API", new Dictionary<string, string> { ["text"] = "type:xhr" });
        LabProfile.SavePreset("web", "console-filter", "Errors", new Dictionary<string, string> { ["hidden"] = "INF,LOG" });

        LabProfile.Reset(); // as if the app was started again: read from the file

        Assert.That(LabProfile.PresetNames("web", "network-filter"), Is.EqualTo(new[] { "API", "Only failures" }));
        Assert.That(LabProfile.PresetNames("web", "console-filter"), Is.EqualTo(new[] { "Errors" }));
        Assert.That(LabProfile.PresetNames("qr", "network-filter"), Is.Empty);
        Assert.That(LabProfile.GetPreset("web", "network-filter", "Only failures")!["text"], Is.EqualTo("is:failed"));
        Assert.That(LabProfile.GetPreset("web", "network-filter", "missing"), Is.Null);
    }

    [Test]
    public void A_preset_needs_a_name_and_can_be_deleted()
    {
        Assert.Throws<InvalidOperationException>(() => LabProfile.SavePreset("web", "k", " ", new Dictionary<string, string>()));

        LabProfile.SavePreset("web", "k", "x", new Dictionary<string, string> { ["a"] = "1" });
        Assert.That(LabProfile.DeletePreset("web", "k", "x"), Is.True);
        Assert.That(LabProfile.DeletePreset("web", "k", "x"), Is.False);
        Assert.That(LabProfile.PresetNames("web", "k"), Is.Empty);
    }

    [Test]
    public void Changes_are_announced()
    {
        int changes = 0;
        void Count() => changes++;
        LabProfile.Changed += Count;
        try
        {
            LabProfile.Remember("web", "a");
            LabProfile.SavePreset("web", "k", "x", new Dictionary<string, string>());
            LabProfile.Remember("web", "a"); // nothing changed
        }
        finally
        {
            LabProfile.Changed -= Count;
        }

        Assert.That(changes, Is.EqualTo(2));
    }
}
