using Silo.Connectors;
using Silo.Connectors.File;
using Silo.Providers.Data;

namespace Silo.Tests.Connectors;

public class ConnectionRestoreTests
{
    [Test]
    public void The_first_retry_waits_the_first_delay_and_each_later_one_twice_as_long()
    {
        Assert.That(ConnectionRestore.RetryDelay(0), Is.EqualTo(ConnectionRestore.FirstRetry));
        Assert.That(ConnectionRestore.RetryDelay(1), Is.EqualTo(ConnectionRestore.FirstRetry * 2));
        Assert.That(ConnectionRestore.RetryDelay(2), Is.EqualTo(ConnectionRestore.FirstRetry * 4));
    }

    [Test]
    public void The_wait_never_exceeds_the_longest()
    {
        for (int attempt = 0; attempt < 100; attempt++)
            Assert.That(ConnectionRestore.RetryDelay(attempt), Is.LessThanOrEqualTo(ConnectionRestore.LongestRetry));

        Assert.That(ConnectionRestore.RetryDelay(50),           Is.EqualTo(ConnectionRestore.LongestRetry));
        Assert.That(ConnectionRestore.RetryDelay(int.MaxValue), Is.EqualTo(ConnectionRestore.LongestRetry));
    }

    [Test]
    public void A_negative_attempt_is_the_first()
    {
        Assert.That(ConnectionRestore.RetryDelay(-3), Is.EqualTo(ConnectionRestore.FirstRetry));
    }

    [Test]
    public void The_request_carries_the_saved_destination_connector_and_name()
    {
        var uri     = new Uri("file:///C:/data");
        var request = ConnectionRestore.RequestFor(new ConnectionRecord(3, "My data", uri), new FileConnector());

        Assert.That(request.DisplayName, Is.EqualTo("My data"));
        Assert.That(request.Connector,   Is.InstanceOf<FileConnector>());
    }

    [Test]
    public void A_blank_saved_name_is_left_for_the_connector_to_choose()
    {
        var request = ConnectionRestore.RequestFor(new ConnectionRecord(3, "  ", new Uri("file:///C:/data")), new FileConnector());

        Assert.That(request.DisplayName, Is.Null);
    }

    [Test]
    public void A_record_without_a_destination_cannot_be_recreated()
    {
        Assert.Throws<ArgumentException>(() => ConnectionRestore.RequestFor(new ConnectionRecord(3, "x", null), new FileConnector()));
    }

    [Test]
    public void The_file_connector_takes_a_file_destination()
    {
        var found = ConnectionRestore.FindConnector(new Uri("file:///C:/data"), [new FileConnector()]);

        Assert.That(found, Is.InstanceOf<FileConnector>());
    }

    [Test]
    public void A_destination_no_connector_handles_finds_none()
    {
        Assert.That(ConnectionRestore.FindConnector(new Uri("nosuchscheme://host/x"), [new FileConnector()]), Is.Null);
    }

    [Test]
    public void A_connection_that_is_not_back_says_so_and_offers_to_try_again_and_to_forget()
    {
        var record  = new ConnectionRecord(5, "Server", new Uri("file:///C:/data"));
        int retried = 0, forgotten = 0;
        var pending = new PendingConnection(record, "unreachable", true, () => retried++, () => forgotten++);

        Assert.That(pending.Label,                            Is.EqualTo("Server (not connected)"));
        Assert.That(pending.Failed,                           Is.True);
        Assert.That(pending.GetDetails().Select(d => d.Name), Does.Contain("Problem"));

        var commands = pending.GetCommands().ToList();
        Assert.That(commands.Select(c => c.Id), Is.EqualTo(new[] { "connection.retry", "connection.forget" }));
        Assert.That(commands[1].IsDestructive,  Is.True);

        commands[0].Command.Execute(null);
        commands[1].Command.Execute(null);
        Assert.That((retried, forgotten), Is.EqualTo((1, 1)));
    }

    [Test]
    public void A_connection_still_being_tried_says_so_and_cannot_be_retried()
    {
        var pending = new PendingConnection(new ConnectionRecord(5, "Server", null), null, false, () => { }, () => { });

        Assert.That(pending.Label,                                          Is.EqualTo("Server (connecting...)"));
        Assert.That(pending.Failed,                                         Is.False);
        Assert.That(pending.GetCommands().First().Command.CanExecute(null), Is.False);
    }

    [Test]
    public void A_connection_without_a_name_is_shown_by_its_destination()
    {
        var pending = new PendingConnection(new ConnectionRecord(5, "", new Uri("file:///C:/data")), null, false, () => { }, () => { });

        Assert.That(pending.Name, Does.Contain("C:/data"));
    }
}