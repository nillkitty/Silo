using Microsoft.Win32;
using Silo.Connectors.File;
using Silo.Connectors.System;
using Silo.ViewModel;

namespace Silo.Tests.Connectors;

public class RegistryModelTests
{
    [Test]
    public void Items_are_told_apart_by_their_path_and_value_name()
    {
        Assert.That(RegistryModel.KindOf("", null), Is.EqualTo(RegistryItemKind.Root));
        Assert.That(RegistryModel.KindOf("HKEY_CURRENT_USER", null), Is.EqualTo(RegistryItemKind.Hive));
        Assert.That(RegistryModel.KindOf(@"HKEY_CURRENT_USER\Software", null), Is.EqualTo(RegistryItemKind.Key));
        Assert.That(RegistryModel.KindOf(@"HKEY_CURRENT_USER\Software", "Name"), Is.EqualTo(RegistryItemKind.Value));
        Assert.That(RegistryModel.KindOf(@"HKEY_CURRENT_USER\Software", ""), Is.EqualTo(RegistryItemKind.Value));
    }

    [Test]
    public void The_label_is_the_name_relative_to_the_key_it_is_in()
    {
        Assert.That(RegistryModel.LabelOf(@"HKEY_CURRENT_USER\Software\Silo", null), Is.EqualTo("Silo"));
        Assert.That(RegistryModel.LabelOf("HKEY_CURRENT_USER", null), Is.EqualTo("HKEY_CURRENT_USER"));
        Assert.That(RegistryModel.LabelOf("", null), Is.EqualTo("Local Registry"));
        Assert.That(RegistryModel.LabelOf(@"HKEY_CURRENT_USER\Software", "AutoConfigUrl"), Is.EqualTo("AutoConfigUrl"));
        Assert.That(RegistryModel.LabelOf(@"HKEY_CURRENT_USER\Software", ""), Is.EqualTo("(Default)"));
    }

    [Test]
    public void Hives_keys_and_values_have_their_own_icons_and_what_is_missing_has_the_missing_icon()
    {
        Assert.That(RegistryModel.IconKeyOf(RegistryItemKind.Root, true), Is.EqualTo("RegHive"));
        Assert.That(RegistryModel.IconKeyOf(RegistryItemKind.Hive, true), Is.EqualTo("RegHive"));
        Assert.That(RegistryModel.IconKeyOf(RegistryItemKind.Key, true), Is.EqualTo("RegKey"));
        Assert.That(RegistryModel.IconKeyOf(RegistryItemKind.Value, true), Is.EqualTo("RegValue"));
        Assert.That(RegistryModel.IconKeyOf(RegistryItemKind.Key, false), Is.EqualTo("FsFileMissing"));
    }

    [Test]
    public void A_key_name_becomes_a_reg_URI_and_a_value_is_its_query()
    {
        Assert.That(RegistryUris.FromKeyName(@"HKEY_CURRENT_USER\Software\My App").AbsoluteUri, Is.EqualTo("reg:///hkey_current_user/Software/My%20App"));
        Assert.That(RegistryUris.FromKeyName(@"HKEY_CURRENT_USER\Software", "Auto Url").AbsoluteUri, Is.EqualTo("reg:///hkey_current_user/Software?Auto%20Url"));
        Assert.That(RegistryUris.FromKeyName("HKLM").AbsoluteUri, Is.EqualTo("reg:///hkey_local_machine"));
        Assert.That(RegistryUris.FromKeyName("").AbsoluteUri, Is.EqualTo("reg:///"));
        Assert.That(Uri.UnescapeDataString(RegistryUris.FromKeyName(@"HKEY_CURRENT_USER\Software", "").Query), Is.EqualTo("?(Default)"));
    }

    [Test]
    public void The_URI_is_read_back_as_the_same_place()
    {
        var provider = new RegistryUriProvider();
        var path     = new RegistryPath(RegistryHive.CurrentUser, @"Software\My App", "Some Value");

        var parsed = provider.ParseUri(RegistryUris.FromPath(path), true);

        Assert.That(parsed, Is.EqualTo(path));
        Assert.That(provider.ParseUri(RegistryUris.FromPath(new RegistryPath(RegistryHive.CurrentUser, "*", null)), true),
                    Is.EqualTo(new RegistryPath(RegistryHive.CurrentUser, "*", null)));
        Assert.That(provider.ParseUri(RegistryUris.FromPath(RegistryPath.LocalRoot), true), Is.EqualTo(RegistryPath.LocalRoot));
    }

    [TestCase("reg:///hkey_local_machine")]
    [TestCase("hklm:///")]
    [TestCase("reg:///HKEY_LOCAL_MACHINE/")]
    public void A_connection_to_the_local_machine_hive_is_that_hive_and_not_the_whole_registry(string uri)
    {
        var parsed = new RegistryUriProvider().ParseUri(new Uri(uri), true)!;

        Assert.That(parsed.IsLocalRoot, Is.False);
        Assert.That(parsed.Hive, Is.EqualTo(RegistryHive.LocalMachine));
        Assert.That(parsed.FullName, Is.EqualTo("HKEY_LOCAL_MACHINE"));
        Assert.That(RegistryModel.KindOf(parsed.FullName, parsed.ValueName), Is.EqualTo(RegistryItemKind.Hive));
        Assert.That(parsed.Name, Is.EqualTo("HKEY_LOCAL_MACHINE"));
    }

    [Test]
    public void Only_reg_with_nothing_after_it_is_the_whole_registry()
    {
        var provider = new RegistryUriProvider();

        Assert.That(provider.ParseUri(new Uri("reg:///"), true)!.IsLocalRoot, Is.True);
        Assert.That(RegistryPath.LocalRoot.Name, Is.EqualTo("Local Registry"));
        Assert.That(RegistryPath.LocalRoot, Is.Not.EqualTo(new RegistryPath(RegistryHive.LocalMachine, "*", null)));
    }

    [Test]
    public void A_path_has_a_full_name_and_the_local_root_has_none()
    {
        Assert.That(new RegistryPath(RegistryHive.CurrentUser, @"Software\Silo", null).FullName, Is.EqualTo(@"HKEY_CURRENT_USER\Software\Silo"));
        Assert.That(new RegistryPath(RegistryHive.Users, "*", null).FullName, Is.EqualTo("HKEY_USERS"));
        Assert.That(RegistryPath.LocalRoot.FullName, Is.Empty);
        Assert.That(RegistryPath.LocalRoot.IsLocalRoot, Is.True);
        Assert.That(new RegistryPath(RegistryHive.LocalMachine, "*", null).IsLocalRoot, Is.False);
        Assert.That(new RegistryPath(RegistryHive.LocalMachine, "*", null).FullName, Is.EqualTo("HKEY_LOCAL_MACHINE"));
    }

    [Test]
    public void A_model_takes_its_label_icon_URI_and_type_from_where_it_is()
    {
        var model = new RegistryModel(null, @"HKEY_CURRENT_USER\Software\Silo");

        Assert.That(model.NodeLabel, Is.EqualTo("Silo"));
        Assert.That(model.Name, Is.EqualTo("Silo"));
        Assert.That(model.TypeName, Is.EqualTo("Registry Key"));
        Assert.That(model.Uri.AbsoluteUri, Is.EqualTo("reg:///hkey_current_user/Software/Silo"));
        Assert.That(model.Data, Is.Null);
    }

    [Test]
    public void A_key_that_cannot_be_opened_gets_a_blocked_node_that_keeps_the_model()
    {
        var model = new RegistryModel(null, @"HKEY_CURRENT_USER\Software\Silo");

        var node = model.GetNode()!;

        Assert.That(node, Is.InstanceOf<BlockedNode>());
        Assert.That(node.DataContext, Is.SameAs(model));
        Assert.That(node.Text, Is.EqualTo("Silo (inaccessible)"));
        Assert.That(node.Icon!.ToString(), Is.EqualTo("FsFileMissing"));
    }

    [Test]
    public void The_root_is_a_node_that_lists_the_hives_as_it_is_expanded()
    {
        var model = new RegistryModel(null, "");

        var node = model.GetNode()!;

        Assert.That(node, Is.InstanceOf<KeyNode>());
        Assert.That(node.Text, Is.EqualTo("Local Registry"));
        Assert.That(node.Icon!.ToString(), Is.EqualTo("RegHive"));
        Assert.That(node.IsExpanded, Is.True);
        Assert.That(node.ChildLoader, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    public async Task Expanding_a_key_lists_its_subkeys_then_its_values()
    {
        string name = "SiloTests-" + Guid.NewGuid().ToString("N");
        string path = @"Software\" + name;
        using (var key = Registry.CurrentUser.CreateSubKey(path))
        {
            key.CreateSubKey("sub");
            key.SetValue("Alpha", "one");
            key.SetValue("", "unnamed");
        }

        try
        {
            var location = new RegistryPath(RegistryHive.CurrentUser, path, null);
            var model    = new RegistryModel(location.Open(), location.FullName);
            var root     = model.GetNode()!;
            await root.LoadChildrenAsync();

            Assert.That(root.Text, Is.EqualTo(name));
            Assert.That(root.Children.Select(c => c.Text), Is.EqualTo(new[] { "sub", "(Default)", "Alpha" }));
            Assert.That(root.Children.Select(c => c.Icon!.ToString()), Is.EqualTo(new[] { "RegKey", "RegValue", "RegValue" }));
            Assert.That(root.Children.All(c => !c.IsExpanded), Is.True);
            Assert.That(root.Children[2].Uri!.Query, Is.EqualTo("?Alpha"));
            Assert.That(((INodeDetails)root.Children[2].DataContext).GetDetails().ToDictionary(r => r.Name, r => r.Value)["Data"], Is.EqualTo("one"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
        }
    }

    [Test]
    [Platform("Win")]
    public void The_local_root_lists_the_hives()
    {
        var hives = new RegistryModel(null, "").ListChildren();

        Assert.That(hives.Select(h => h.NodeLabel), Is.EqualTo(new[] { "HKEY_CLASSES_ROOT", "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE", "HKEY_USERS", "HKEY_CURRENT_CONFIG" }));
    }
}
