using System.Security.Principal;
using System.Windows.Controls;
using Serilog;
using Silo.Crypto;
using Silo.Model;
using Silo.Settings;
using AccessEntry = Silo.Settings.AccessEntry;
using AccessLevel = Silo.Settings.AccessLevel;
using PromptDialog = Silo.Ui.Windows.PromptDialog;

namespace Silo.Views.SettingsPanes;

// Settings ▸ Owner Settings: kept in the Silo; only the owner may change them.

/// <summary>Settings ▸ Owner Settings ▸ Access Control.</summary>
public sealed class AccessControlPane() : SiloSettingsPane("Access Control", SettingsAudience.Any)
{
    public override double PreferredWidth => 540;

    protected override bool OwnerOnly => true;

    protected override void BuildFor(OpenSilo silo, Panel body)
    {
        // who the person is, and what the Silo lets them do
        var me = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        me.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        me.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        me.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var face = new TextBlock { Text = "👤", FontSize = 30, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        face.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Accent");
        me.Children.Add(face);

        var labels = new TextBlock { Text = "You:\nRole:\nAccess:", Margin = new Thickness(0, 0, 18, 0) };
        Grid.SetColumn(labels, 1);
        me.Children.Add(labels);

        var values = new TextBlock { Text = $"{You()}\n{RoleOf(silo)}\n{AccessOf(silo)}", TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(values, 2);
        me.Children.Add(values);
        body.Children.Add(me);

        body.Children.Add(Rule());

        body.Children.Add(Heading("Other users:", 0));
        var users = PairList(100, "User or group", "Access", 220);
        foreach (var entry in SiloSettings.Access.Load().Users)
            users.Items.Add(new SettingsPair(entry.Account, entry.Level == AccessLevel.ReadWrite ? "✎  Read/write" : "▶  Read only", entry.Account));
        users.MouseDoubleClick += (_, _) => Change(users);

        body.Children.Add(WithButtons(users,
                                      MakeButton("Change",      () => Change(users), toolTip: "Switch the selected user between read/write and read only."),
                                      MakeButton("Remove",      () => Remove(users)),
                                      MakeButton("Share with…", Share)));
        body.Children.Add(Note("People listed here are kept with the Silo; their access is not enforced when they open it yet."));

        body.Children.Add(Banner("The contents of this Silo are not encrypted",
                                 MakeButton("Go to encryption", GoToEncryption)));
    }

    private static string You()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User is { } sid ? $"{identity.Name}   ({sid.Value})" : identity.Name;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "The current Windows identity could not be read");
            return Environment.UserDomainName + "\\" + Environment.UserName;
        }
    }

    private static string RoleOf(OpenSilo silo)
    {
        var roles = new List<string>();
        if (silo.IsOwner) roles.Add("Owner");
        if (silo.IsAuthorable) roles.Add("Author");
        if (silo.IsDeveloper) roles.Add("Developer");
        if (roles.Count == 0) roles.Add(silo.IsWritable ? "Writer" : silo.IsReadable ? "Reader" : "None");
        return string.Join(", ", roles);
    }

    private static string AccessOf(OpenSilo silo)
    {
        return silo.IsOwner && silo.IsWritable ? "Full Control" :
               silo.IsWritable                 ? "Read/write" :
               silo.IsReadable                 ? "Read only" : "No access";
    }

    private void Change(ListView users)
    {
        if ((users.SelectedItem as SettingsPair)?.Tag is not string account) return;

        var (button, _) = PromptDialog.Show("Access Control", $"What may {account} do in this Silo?", ["Read/write", "Read only", "Cancel"]);
        if (button > 1) return;

        SiloSettings.Access.Update(s =>
                                   {
                                       if (s.Users.FirstOrDefault(u => u.Account.Equals(account, StringComparison.OrdinalIgnoreCase)) is { } entry)
                                           entry.Level = button == 0 ? AccessLevel.ReadWrite : AccessLevel.ReadOnly;
                                   });
        Rebuild();
    }

    private void Remove(ListView users)
    {
        if ((users.SelectedItem as SettingsPair)?.Tag is not string account) return;

        SiloSettings.Access.Update(s => s.Users.RemoveAll(u => u.Account.Equals(account, StringComparison.OrdinalIgnoreCase)));
        Rebuild();
    }

    private void Share()
    {
        if (PromptDialog.Ask("Share with", "The user or group to share this Silo with (DOMAIN\\name):", "Next") is not { } account) return;

        // a name Windows knows is written the way Windows writes it
        try
        {
            account = new NTAccount(account).Translate(typeof(SecurityIdentifier)).Translate(typeof(NTAccount)).Value;
        }
        catch (SystemException) // IdentityNotMappedException, or a name that is not well formed
        {
            if (MessageBox.Show($"Windows does not know \"{account}\". Add it anyway?", "Share with", MessageBoxButton.YesNo, MessageBoxImage.Question) !=
                MessageBoxResult.Yes) return;
        }

        var (button, _) = PromptDialog.Show("Share with", $"What may {account} do in this Silo?", ["Read/write", "Read only", "Cancel"]);
        if (button > 1) return;

        SiloSettings.Access.Update(s =>
                                   {
                                       s.Users.RemoveAll(u => u.Account.Equals(account, StringComparison.OrdinalIgnoreCase));
                                       s.Users.Add(new AccessEntry { Account = account, Level = button == 0 ? AccessLevel.ReadWrite : AccessLevel.ReadOnly });
                                   });
        Rebuild();
    }

    private static void GoToEncryption()
    {
        if (Application.Current?.MainWindow is MainWindow main) main.Navigate("Settings/Owner Settings/Encryption");
    }
}

/// <summary>Settings ▸ Owner Settings ▸ Encryption: the protector of the Silo's secrets, and its keys.</summary>
public sealed class EncryptionPane() : SiloSettingsPane("Encryption", SettingsAudience.Any)
{
    public override double PreferredWidth => 540;

    protected override bool OwnerOnly => true;

    protected override void BuildFor(OpenSilo silo, Panel body)
    {
        var store = silo.Data as ISecretStore;

        body.Children.Add(Heading("Encrypt Silo", 0));
        var encrypt = Check("Encrypt the contents of this Silo…", false, _ => { });
        encrypt.IsEnabled = false;
        encrypt.ToolTip   = "Encrypting the Silo file itself is not available yet. Its secrets (credentials) are always encrypted.";
        ToolTipService.SetShowOnDisabled(encrypt, true);
        body.Children.Add(WithButtons(encrypt,
                                      store is null
                                          ? MakeButton("Settings…", null, "This Silo's storage cannot keep secrets.")
                                          : MakeButton("Settings…", () => ChooseProtector(silo), toolTip: "Choose who can unlock this Silo's secrets.")));

        body.Children.Add(Heading("Encryption Keys"));
        if (store is null)
        {
            body.Children.Add(Note("This Silo's storage cannot keep secrets."));
            return;
        }

        var protectors = SecretProtectors.Available().ToDictionary(p => p.Id, p => p.DisplayName);
        var keys       = PairList(90, "Key", "Protection", 220);
        foreach (var row in store.GetSecrets(SiloEncryption.KeyKind))
            keys.Items.Add(new SettingsPair($"Data key {row.Id}   ({row.Created.ToLocalTime():d})",
                                            "🔒  " + (protectors.TryGetValue(row.Provider, out string? name) ? name : row.Provider + " (not available here)")));
        body.Children.Add(keys);

        if (keys.Items.Count == 0)
            body.Children.Add(Note($"No key yet. One is made the first time a secret is saved, protected by {NameOf(silo, protectors)}."));
        else
            body.Children.Add(Note($"New secrets are protected by {NameOf(silo, protectors)}."));
    }

    private static string NameOf(OpenSilo silo, IReadOnlyDictionary<string, string> protectors)
    {
        string id = silo.Encryption.ProviderId;
        return protectors.TryGetValue(id, out string? name) ? name : id;
    }

    private void ChooseProtector(OpenSilo silo)
    {
        var available = SecretProtectors.Available();
        string current = silo.Encryption.ProviderId;

        var message = new StringBuilder("Choose who can unlock this Silo's secrets:\n");
        foreach (var p in available) message.Append($"\n• {p.DisplayName}{(p.Id == current ? " (now)" : string.Empty)}: {p.Description}");

        var (button, _) = PromptDialog.Show("Encryption", message.ToString(), [.. available.Select(p => p.DisplayName), "Cancel"]);
        if (button >= available.Count || available[button].Id == current) return;

        int moved = silo.Encryption.ChangeProvider(available[button].Id);
        App.Alert(moved == 0
                      ? $"New secrets will be protected by {available[button].DisplayName}."
                      : $"{moved} key(s) are now protected by {available[button].DisplayName}.", "Encryption");
        Rebuild();
    }
}
