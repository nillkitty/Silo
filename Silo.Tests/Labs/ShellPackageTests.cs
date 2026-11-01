using Microsoft.Win32;
using Silo.Shell;

namespace Silo.Tests.Labs;

public class ShellPackageTests
{
    private const string App = @"C:\Tools\App.exe";

    private static ShellPackage Sample(string name) => new()
                                                       {
                                                           Name = name,
                                                           Entries =
                                                           [
                                                               new ShellEntry { Kind = ShellEntryKind.Protocol, Name = "silotest" + Guid.NewGuid().ToString("N")[..6], Description = "Test", Command = $"\"{App}\" \"%1\"", Icon = App },
                                                               new ShellEntry
                                                               {
                                                                   Kind = ShellEntryKind.Submenu, Target = "SiloTestClass", Name = "Menu", Label = "&Test", Position = ShellPosition.Top,
                                                                   Children = [new ShellEntry { Kind = ShellEntryKind.Verb, Name = "One", Label = "One", Command = $"\"{App}\" \"%1\"" }]
                                                               },
                                                               new ShellEntry { Kind = ShellEntryKind.Verb, Target = ".silotest", Name = "Act", Label = "Act", Command = $"\"{App}\" \"%1\"", MakeDefault = true }
                                                           ]
                                                       };

    [Test]
    public void A_package_survives_JSON()
    {
        var package = Sample("RoundTrip");
        Assert.That(ShellJson.FromJson(ShellJson.ToJson(package), out var read, out var error), Is.True, error);
        Assert.That(read.Entries.Select(e => e.Kind), Is.EqualTo(package.Entries.Select(e => e.Kind)));
        Assert.That(read.Entries[1].Children, Has.Count.EqualTo(1));
        Assert.That(read.Entries[1].Position, Is.EqualTo(ShellPosition.Top));
    }

    [Test]
    public void Compiling_writes_the_values_each_kind_needs()
    {
        var ops = ShellCompiler.Compile(Sample("Compile"));

        Assert.That(ops.Any(o => o.Kind == ShellOpKind.Value && o.Name == "URL Protocol" && o.Path.StartsWith(@"HKCU\Software\Classes\silotest")), Is.True);
        Assert.That(ops.Any(o => o.Path == @"HKCU\Software\Classes\SiloTestClass\shell\Menu" && o.Name == "SubCommands"), Is.True);
        Assert.That(ops.Any(o => o.Path == @"HKCU\Software\Classes\SiloTestClass\shell\Menu\shell\One\command"), Is.True);
        // an extension goes by SystemFileAssociations, and the default verb is recorded on the class
        Assert.That(ops.Any(o => o.Path == @"HKCU\Software\Classes\SystemFileAssociations\.silotest\shell" && o.Data == "Act"), Is.True);
    }

    [Test]
    public void Problems_are_found_before_installing()
    {
        var package = new ShellPackage
                      {
                          Name = "Bad",
                          Entries =
                          [
                              new ShellEntry { Kind = ShellEntryKind.Protocol, Name = "https", Command = "x" },
                              new ShellEntry { Kind = ShellEntryKind.Verb, Name = "V", Label = "V", Command = "x" },
                              new ShellEntry { Kind = ShellEntryKind.Registry, Key = @"HKLM\Software\X", Data = "1" }
                          ]
                      };
        var errors = ShellCheck.Validate(package).Where(p => p.Severity == ShellSeverity.Error).Select(p => p.Text).ToList();

        Assert.That(errors.Any(e => e.Contains("belongs to the system")), Is.True);
        Assert.That(errors.Any(e => e.Contains("no class")), Is.True);
        Assert.That(errors.Any(e => e.Contains("HKLM")), Is.True);
    }

    [Test]
    public void Install_records_what_it_writes_and_uninstall_takes_it_all_away()
    {
        string root = @"Software\SiloShellTests\" + Guid.NewGuid().ToString("N");
        string name = "SiloTest" + Guid.NewGuid().ToString("N")[..8];
        using (var pre = Registry.CurrentUser.CreateSubKey(root + @"\Existing"))
            pre.SetValue("Keep", "before");

        var package = Sample(name);
        package.Entries.Add(new ShellEntry { Kind = ShellEntryKind.Registry, Key = @"HKCU\" + root + @"\Existing", ValueName = "Keep", Data = "after" });
        package.Entries.Add(new ShellEntry { Kind = ShellEntryKind.Registry, Key = @"HKCU\" + root + @"\New\Deeper", ValueName = "Number", ValueKind = ShellValueKind.DWord, Data = "7" });

        try
        {
            var installed = ShellInstaller.Install(package, "test");
            Assert.That(installed.Writes, Is.Not.Empty);
            Assert.That(ShellInstaller.List().Any(p => p.Name == name), Is.True);
            using (var k = Registry.CurrentUser.OpenSubKey(root + @"\Existing"))
                Assert.That(k!.GetValue("Keep"), Is.EqualTo("after"));
            using (var k = Registry.CurrentUser.OpenSubKey(root + @"\New\Deeper"))
                Assert.That(k!.GetValue("Number"), Is.EqualTo(7));

            var result = ShellInstaller.Uninstall(name);

            Assert.That(result.Complete, Is.True, string.Join(";", result.Failed));
            Assert.That(ShellInstaller.Find(name), Is.Null);
            using (var k = Registry.CurrentUser.OpenSubKey(root + @"\Existing"))
                Assert.That(k!.GetValue("Keep"), Is.EqualTo("before"), "a replaced value is put back");
            Assert.That(Registry.CurrentUser.OpenSubKey(root + @"\New"), Is.Null, "keys the package made are removed");
            Assert.That(Registry.CurrentUser.OpenSubKey(@"Software\Classes\SiloTestClass"), Is.Null);
            Assert.That(Registry.CurrentUser.OpenSubKey(@"Software\Classes\SystemFileAssociations\.silotest"), Is.Null);
        }
        finally
        {
            if (ShellInstaller.Find(name) is not null) ShellInstaller.Uninstall(name);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\SiloShellTests", false);
        }
    }
}
