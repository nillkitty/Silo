using System.Linq;
using System.Text;
using Silo.Contracts.Providers.Crypto;
using Silo.Credentials;
using Silo.Crypto;
using Silo.Model;

namespace Silo.Tests.Security;

public class CredentialModelTests
{
    [Test]
    public void A_credential_prints_as_its_name_and_never_its_password()
    {
        var c = new Credential { Name = "Sales server", Username = "sa", Password = "hunter2" };

        Assert.That(c.ToString(), Is.EqualTo("Sales server"));
        Assert.That($"{c}", Does.Not.Contain("hunter2"));
    }

    [Test]
    public void The_account_is_the_user_with_its_domain()
    {
        Assert.That(new Credential { Username = "nill", Domain = "CONTOSO" }.Account, Is.EqualTo("CONTOSO\\nill"));
        Assert.That(new Credential { Username = "sa" }.Account, Is.EqualTo("sa"));
        Assert.That(new Credential { Password = "x" }.Account, Is.Null);
    }

    [Test]
    public void A_clone_is_a_separate_object()
    {
        var c = new Credential { Name = "a", Username = "u" };
        var d = c.Clone();
        d.Name = "b";

        Assert.That(c.Name, Is.EqualTo("a"));
    }
}

public class CredentialRulesTests
{
    private static readonly (int, string)[] Others = [(1, "Sales server"), (2, "Contoso admin")];

    private static Credential Valid(string name = "New one") => new() { Name = name, Username = "u" };

    [Test]
    public void A_good_credential_is_accepted()
    {
        Assert.That(CredentialRules.Check(Valid(), Others), Is.Null);
    }

    [TestCase("")]
    [TestCase("   ")]
    public void A_name_is_required(string name)
    {
        Assert.That(CredentialRules.Check(Valid(name), Others), Does.Contain("name"));
    }

    [Test]
    public void A_name_is_not_taken_twice_whatever_the_case_but_it_may_keep_its_own()
    {
        Assert.That(CredentialRules.Check(Valid("sales SERVER"), Others), Does.Contain("already"));

        var same = Valid("Sales server");
        same.Id = 1;
        Assert.That(CredentialRules.Check(same, Others), Is.Null);
    }

    [Test]
    public void The_name_is_trimmed_before_it_is_compared()
    {
        Assert.That(CredentialRules.Check(Valid("  Sales server "), Others), Does.Contain("already"));
    }

    [Test]
    public void A_name_cannot_look_like_a_reserved_row_or_be_very_long()
    {
        Assert.That(CredentialRules.Check(Valid("$datakey"), Others), Is.Not.Null);
        Assert.That(CredentialRules.Check(Valid(new string('x', CredentialRules.MaxNameLength + 1)), Others), Is.Not.Null);
        Assert.That(CredentialRules.Check(Valid(new string('x', CredentialRules.MaxNameLength)), Others), Is.Null);
    }

    [Test]
    public void It_needs_a_user_name_or_a_password()
    {
        Assert.That(CredentialRules.Check(new Credential { Name = "x" }, Others), Is.Not.Null);
        Assert.That(CredentialRules.Check(new Credential { Name = "x", Password = "p" }, Others), Is.Null);
        Assert.That(CredentialRules.Check(new Credential { Name = "x", Username = "u" }, Others), Is.Null);
    }
}

public class CredentialPayloadTests
{
    [Test]
    public void Everything_but_the_name_round_trips_including_odd_characters()
    {
        var c = new Credential { Name = "n", Username = "nill@nill.dev", Domain = "CONTOSO", Password = "p\"a\\ss\u00e9 \U0001F511\n", Notes = "line 1\nline 2" };

        var back = CredentialPayload.Read(CredentialPayload.Write(c));

        Assert.That(back.Username, Is.EqualTo(c.Username));
        Assert.That(back.Domain,   Is.EqualTo(c.Domain));
        Assert.That(back.Password, Is.EqualTo(c.Password));
        Assert.That(back.Notes,    Is.EqualTo(c.Notes));
    }

    [Test]
    public void Missing_fields_stay_missing()
    {
        var back = CredentialPayload.Read(CredentialPayload.Write(new Credential { Username = "u" }));

        Assert.That(back.Password, Is.Null);
        Assert.That(back.Notes, Is.Null);
    }

    [Test]
    public void Fields_a_later_version_added_are_ignored()
    {
        var back = CredentialPayload.Read(Encoding.UTF8.GetBytes("{\"v\":2,\"user\":\"u\",\"token\":\"t\"}"));

        Assert.That(back.Username, Is.EqualTo("u"));
    }

    [Test]
    public void Damaged_bytes_are_a_secret_exception_not_a_json_one()
    {
        Assert.Throws<SecretException>(() => CredentialPayload.Read(Encoding.UTF8.GetBytes("not json")));
        Assert.Throws<SecretException>(() => CredentialPayload.Read(Encoding.UTF8.GetBytes("null")));
    }
}

public class CredentialServiceTests
{
    private InMemorySecretStore _store = null!;
    private SiloEncryption      _encryption = null!;
    private CredentialService   _service = null!;

    [SetUp]
    public void SetUp()
    {
        _store      = new InMemorySecretStore();
        _encryption = new SiloEncryption(_store, [new ScramblingProtector("scramble-a", 0x5A)], "scramble-a");
        _service    = new CredentialService(_store, _encryption);
    }

    [TearDown]
    public void TearDown() => _encryption.Dispose();

    private Credential Save(string name, string user = "u", string? password = "p") =>
        _service.Save(new Credential { Name = name, Username = user, Password = password });

    [Test]
    public void A_saved_credential_comes_back_with_its_password()
    {
        var saved = _service.Save(new Credential { Name = "Sales", Username = "sa", Domain = "D", Password = "hunter2", Notes = "n" });

        var got = _service.Get(saved.Id)!;
        Assert.That(saved.Id, Is.Not.Zero);
        Assert.That((got.Name, got.Username, got.Domain, got.Password, got.Notes), Is.EqualTo(("Sales", "sa", "D", "hunter2", "n")));
    }

    [Test]
    public void What_is_stored_has_the_name_in_the_clear_and_nothing_else_readable()
    {
        Save("Sales", "sa-account", "hunter2");

        var row  = _store.GetSecrets(CredentialService.Kind).Single();
        var text = Encoding.UTF8.GetString(row.Data);
        Assert.That(row.Key, Is.EqualTo("Sales"));
        Assert.That(text, Does.Not.Contain("hunter2"));
        Assert.That(text, Does.Not.Contain("sa-account"));
    }

    [Test]
    public void The_data_key_is_not_listed_as_a_credential()
    {
        Save("Sales");

        Assert.That(_store.GetSecrets(), Has.Count.EqualTo(2)); // the credential and the data key
        Assert.That(_service.List().Select(c => c.Name), Is.EqualTo(new[] { "Sales" }));
    }

    [Test]
    public void The_list_is_by_name_and_shows_the_account_but_not_the_password()
    {
        Save("b-server", "bob");
        _service.Save(new Credential { Name = "A-server", Username = "alice", Domain = "D", Password = "secret" });

        var list = _service.List();

        Assert.That(list.Select(c => c.Name), Is.EqualTo(new[] { "A-server", "b-server" }));
        Assert.That(list[0].Account, Is.EqualTo("D\\alice"));
        Assert.That(list.All(c => c.IsReadable), Is.True);
        Assert.That(list[0].ToString(), Does.Not.Contain("secret"));
    }

    [Test]
    public void Saving_again_replaces_the_credential_and_keeps_when_it_was_made()
    {
        var first = Save("Sales", "sa", "old");
        var edit  = _service.Get(first.Id)!;
        edit.Password = "new";
        edit.Name     = "Sales (new)";

        var second = _service.Save(edit);

        Assert.That(_service.List(), Has.Count.EqualTo(1));
        Assert.That(second.Id, Is.EqualTo(first.Id));
        Assert.That(second.Created, Is.EqualTo(first.Created));
        Assert.That(second.Modified, Is.GreaterThanOrEqualTo(first.Modified));
        Assert.That(_service.Get(first.Id)!.Password, Is.EqualTo("new"));
        Assert.That(_service.Get(first.Id)!.Name, Is.EqualTo("Sales (new)"));
    }

    [Test]
    public void A_name_already_taken_is_refused_and_nothing_is_stored()
    {
        Save("Sales");

        Assert.Throws<InvalidOperationException>(() => Save("sales"));
        Assert.That(_service.List(), Has.Count.EqualTo(1));
    }

    [Test]
    public void Validate_says_what_Save_would_refuse()
    {
        Save("Sales");

        Assert.That(_service.Validate(new Credential { Name = "Sales", Username = "u" }), Does.Contain("already"));
        Assert.That(_service.Validate(new Credential { Name = "Other", Username = "u" }), Is.Null);
    }

    [Test]
    public void Find_looks_a_credential_up_by_name_ignoring_case()
    {
        Save("Sales", "sa", "p");

        Assert.That(_service.Find("SALES")!.Username, Is.EqualTo("sa"));
        Assert.That(_service.Find("nope"), Is.Null);
    }

    [Test]
    public void Delete_removes_it_and_a_second_delete_says_there_was_nothing()
    {
        var saved = Save("Sales");

        Assert.That(_service.Delete(saved.Id), Is.True);
        Assert.That(_service.List(), Is.Empty);
        Assert.That(_service.Delete(saved.Id), Is.False);
    }

    [Test]
    public void A_data_key_is_not_a_credential_and_cannot_be_read_or_deleted_as_one()
    {
        Save("Sales");
        int keyId = _store.GetSecrets(SiloEncryption.KeyKind).Single().Id;

        Assert.That(_service.Get(keyId), Is.Null);
        Assert.That(_service.Delete(keyId), Is.False);
        Assert.That(_store.GetSecret(keyId), Is.Not.Null);
    }

    [Test]
    public void A_credential_that_cannot_be_opened_here_is_listed_as_such_and_the_rest_still_work()
    {
        Save("Fine");
        _store.SaveSecret(new SecretRecord(0, "Foreign", CredentialService.Kind, string.Empty,
                                           SecretEnvelope.Seal(SecretEnvelope.NewKey(), 77, [1, 2, 3], CredentialPayload.Context), DateTime.UtcNow, DateTime.UtcNow));

        var list = _service.List();

        Assert.That(list.Single(c => c.Name == "Fine").IsReadable, Is.True);
        var foreign = list.Single(c => c.Name == "Foreign");
        Assert.That(foreign.IsReadable, Is.False);
        Assert.That(foreign.Account, Is.Null);
        Assert.Throws<SecretException>(() => _service.Get(foreign.Id));
    }

    [Test]
    public void Changed_is_raised_after_a_save_and_after_a_delete()
    {
        int raised = 0;
        EventHandler handler = (_, _) => raised++;
        CredentialService.Changed += handler;
        try
        {
            var saved = Save("Sales");
            _service.Delete(saved.Id);
        }
        finally
        {
            CredentialService.Changed -= handler;
        }

        Assert.That(raised, Is.EqualTo(2));
    }

    [Test]
    public void Moving_the_protector_keeps_every_credential_readable()
    {
        var other = new ScramblingProtector("scramble-b", 0x33);
        using var enc = new SiloEncryption(_store, [new ScramblingProtector("scramble-a", 0x5A), other], "scramble-a");
        var service = new CredentialService(_store, enc);
        service.Save(new Credential { Name = "Sales", Username = "u", Password = "p" });

        enc.ChangeProvider("scramble-b");

        using var fresh = new SiloEncryption(_store, [new ScramblingProtector("scramble-a", 0x5A), other], "scramble-a");
        Assert.That(new CredentialService(_store, fresh).Find("Sales")!.Password, Is.EqualTo("p"));
    }
}
