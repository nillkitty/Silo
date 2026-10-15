using System.IO;
using System.Net;
using Silo.Connectors;
using Silo.Connectors.Discovery;
using Silo.Settings;

namespace Silo.Tests.Connectors;

public class ConnectionHistoryTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void A_destination_used_again_moves_to_the_front_instead_of_repeating()
    {
        var list = ConnectionHistory.PromoteDestination([], "file:///C:/a", "a", Now, 5);
        list = ConnectionHistory.PromoteDestination(list, "file:///C:/b", "b", Now, 5);
        list = ConnectionHistory.PromoteDestination(list, "FILE:///C:/A", "a again", Now, 5);

        Assert.That(list.Select(d => d.Name), Is.EqualTo(new[] { "a again", "b" }));
    }

    [Test]
    public void The_destination_list_is_trimmed_to_its_limit_dropping_the_oldest()
    {
        var list = new List<RecentDestination>();
        for (int i = 0; i < 6; i++)
            list = ConnectionHistory.PromoteDestination(list, $"file:///C:/{i}", $"d{i}", Now, 4);

        Assert.That(list.Select(d => d.Name), Is.EqualTo(new[] { "d5", "d4", "d3", "d2" }));
    }

    [Test]
    public void A_destination_without_a_name_is_listed_by_its_uri()
    {
        var list = ConnectionHistory.PromoteDestination([], "reg:///hkey_users", " ", Now, 5);

        Assert.That(list[0].Name, Is.EqualTo("reg:///hkey_users"));
    }

    [Test]
    public void Typed_uris_are_most_recent_first_without_case_duplicates_and_ignore_blanks()
    {
        var list = ConnectionHistory.PromoteText([], "reg:///hkey_users", 3);
        list = ConnectionHistory.PromoteText(list, "C:\\Data", 3);
        list = ConnectionHistory.PromoteText(list, "  ", 3);
        list = ConnectionHistory.PromoteText(list, "REG:///HKEY_USERS", 3);

        Assert.That(list, Is.EqualTo(new[] { "REG:///HKEY_USERS", "C:\\Data" }));
    }

    [Test]
    public void What_was_recorded_is_still_there_after_the_file_is_read_again()
    {
        string file = Path.Combine(Path.GetTempPath(), $"silo-history-{Guid.NewGuid():N}.json");
        try
        {
            var first = new ConnectionHistory(file);
            first.RecordDestination(new Uri("file:///C:/Data"), "Data");
            first.RecordUri("reg:///hkey_current_user");

            var second = new ConnectionHistory(file);
            Assert.That(second.Destinations.Select(d => d.Name), Is.EqualTo(new[] { "Data" }));
            Assert.That(second.Uris, Is.EqualTo(new[] { "reg:///hkey_current_user" }));

            second.RemoveDestination(second.Destinations[0]);
            Assert.That(new ConnectionHistory(file).Destinations, Is.Empty);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public void A_missing_or_damaged_file_leaves_the_history_empty()
    {
        string file = Path.Combine(Path.GetTempPath(), $"silo-history-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(file, "{ not json");
            var history = new ConnectionHistory(file);

            Assert.That(history.Destinations, Is.Empty);
            Assert.That(history.Uris, Is.Empty);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class DiscoveryTests
{
    [TestCase(0, "0 bytes")]
    [TestCase(1023, "1023 bytes")]
    [TestCase(1536, "1.5 KB")]
    [TestCase(5L * 1024 * 1024 * 1024, "5 GB")]
    [SetCulture("en-US")]
    public void Drive_sizes_are_shown_in_the_largest_fitting_unit(long bytes, string expected)
    {
        Assert.That(DriveDiscovery.FormatSize(bytes), Is.EqualTo(expected));
    }

    [Test]
    public void A_joined_domain_is_listed_as_an_ldap_destination()
    {
        var entry = DomainDiscovery.EntryFor(" corp.example.com. ");

        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.Uri.AbsoluteUri, Is.EqualTo("ldap://corp.example.com/"));
        Assert.That(entry.Name, Is.EqualTo("Domain corp.example.com"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(" . ")]
    public void No_domain_gives_no_destination(string? name)
    {
        Assert.That(DomainDiscovery.EntryFor(name), Is.Null);
    }

    [Test]
    public async Task The_registry_hives_are_all_listed_with_reg_uris()
    {
        var found = new List<string>();
        await foreach (var d in new RegistryHiveDiscovery().DiscoverAsync(CancellationToken.None))
            found.Add(d.Uri.AbsoluteUri);

        Assert.That(found, Is.EqualTo(new[]
                                      {
                                          "reg:///hkey_current_user", "reg:///hkey_local_machine", "reg:///hkey_classes_root",
                                          "reg:///hkey_users", "reg:///hkey_current_config"
                                      }));
    }
}

public class DestinationTests
{
    [Test]
    public void The_address_follows_the_uri_and_the_uri_follows_the_address()
    {
        var destination = new Destination(new Uri("tcp://10.1.2.3:99"));
        Assert.That(destination.Address?.ToString(), Is.EqualTo("10.1.2.3"));

        destination.Address = IPAddress.Parse("::1");
        Assert.That(destination.Uri!.Host, Is.EqualTo("[::1]"));
        Assert.That(destination.Address?.ToString(), Is.EqualTo("::1"));

        destination.Address = null;
        Assert.That(destination.Uri, Is.Null);
    }

    [Test]
    public void A_host_name_has_no_address()
    {
        Assert.That(new Destination(new Uri("https://example.com")).Address, Is.Null);
    }

    [Test]
    public void A_request_for_a_uri_carries_the_uri_and_a_destination()
    {
        var uri     = new Uri("file:///C:/Data");
        var request = new ConnectionRequest(uri);

        Assert.That(request.RequestedUri, Is.EqualTo(uri));
        Assert.That(request.Destination?.Uri, Is.EqualTo(uri));
    }
}
