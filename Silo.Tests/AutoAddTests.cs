using System.IO;
using Silo.Providers.Monitoring;
using Silo.Providers.Reflection;
using Silo.Roles;
using Silo.TextEditing;
using Silo.Ui.Panes;

namespace Silo.Tests;

public class AutoAddTests
{
    private static ContainerItem Item(string name, string content = "") =>
        new(name, new Uri("file:///" + name.Replace('\\', '/')), false, "sig", () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)), name);

    [Test]
    public void ClassifiesByFileType()
    {
        Assert.Multiple(() =>
                        {
                            Assert.That(ItemClassifier.Classify(Item(@"C:\x\run.ps1"), []).Kind, Is.EqualTo(ItemKind.Script));
                            Assert.That(ItemClassifier.Classify(Item(@"C:\x\tool.exe"), []).Kind, Is.EqualTo(ItemKind.Program));
                            Assert.That(ItemClassifier.Classify(Item(@"C:\x\data.json", "{}"), []).Kind, Is.EqualTo(ItemKind.Data));
                        });
    }

    [Test]
    public void ClassifiesByContentWhenTheNameSaysNothing()
    {
        var binary = new ContainerItem("blob", new Uri("file:///blob"), false, "s", () => new MemoryStream([1, 0, 2, 0, 3]));
        Assert.Multiple(() =>
                        {
                            Assert.That(ItemClassifier.Classify(binary, []).Kind, Is.EqualTo(ItemKind.Binary));
                            Assert.That(ItemClassifier.Classify(Item("run", "#!/bin/sh\necho hi"), []).Kind, Is.EqualTo(ItemKind.Script));
                            Assert.That(ItemClassifier.Classify(Item("notes", "just words"), []).Kind, Is.EqualTo(ItemKind.Document));
                        });
    }

    [Test]
    public void TheMostSpecificHandlingRuleWins()
    {
        var table = new RoleHandlingTable([
                                              new RoleHandlingRule("Script", null,  null, HandlingAction.Skip),
                                              new RoleHandlingRule("Script", "ps1", null, HandlingAction.Add, "Maintenance")
                                          ]);
        Assert.Multiple(() =>
                        {
                            Assert.That(table.Find(new ItemClassification(ItemKind.Script, "ps1", null))!.Role, Is.EqualTo("Maintenance"));
                            Assert.That(table.Find(new ItemClassification(ItemKind.Script, "py",  null))!.Action, Is.EqualTo(HandlingAction.Skip));
                            Assert.That(table.Find(new ItemClassification(ItemKind.Binary, null,  null)), Is.Null);
                        });
    }

    [Test]
    public void CompareFindsCreatedChangedAndDeleted()
    {
        var before = new Dictionary<string, string> { ["a"] = "1", ["b"] = "1", ["c"] = "1" };
        var after  = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2", ["d"] = "1" };

        var changes = ChangeMonitoring.Compare(before, after, _ => null).ToDictionary(c => c.Name, c => c.Kind);

        Assert.That(changes, Is.EqualTo(new Dictionary<string, NodeChangeKind>
                                        {
                                            ["b"] = NodeChangeKind.Changed, ["d"] = NodeChangeKind.Created, ["c"] = NodeChangeKind.Deleted
                                        }));
    }

    [Test]
    public void ActivePollingOfAFolderReportsANewFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "silo-monitor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var model = Silo.Connectors.File.FileConnection.FromPath(new Silo.Connectors.File.FileConnector(), dir);
            using var seen = new ManualResetEventSlim();
            using var monitor = model.StartMonitoring(MonitorModes.Active, c =>
                                                                          {
                                                                              if (c.Kind == NodeChangeKind.Created) seen.Set();
                                                                          }, false, TimeSpan.FromMilliseconds(100));
            Assert.That(monitor, Is.Not.Null);

            File.WriteAllText(Path.Combine(dir, "new.txt"), "hi");
            Assert.That(seen.Wait(TimeSpan.FromSeconds(5)), Is.True);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void DevProblemsAreHiddenBelowTheDeveloperLevel()
    {
        var dev = new Problem(ProblemSeverity.Warning, "x", "doc", null, null, "src", null, ProblemVisibility.Dev);
        Assert.That(ProblemList.IsVisible(dev), Is.EqualTo(Silo.Design.AppDesignLevel.Instance.IsAtLeast(Silo.Design.DesignLevel.Developer)));
    }

    [Test]
    public void ScriptLanguagesAreRecognisedByFileName()
    {
        Assert.Multiple(() =>
                        {
                            Assert.That(TextLanguages.FromFileName("build.ps1"), Is.EqualTo(TextLanguage.PowerShell));
                            Assert.That(TextLanguages.FromFileName("tool.py"),   Is.EqualTo(TextLanguage.Python));
                            Assert.That(TextLanguages.FromFileName("go.bat"),    Is.EqualTo(TextLanguage.Batch));
                            Assert.That(TextLanguages.FromFileName("x.csx"),     Is.EqualTo(TextLanguage.CSharp));
                            Assert.That(TextLanguages.FromFileName("x.fsx"),     Is.EqualTo(TextLanguage.FSharp));
                        });
    }
}
