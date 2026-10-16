using Silo.Connectors.Sql;

namespace Silo.Tests.Connectors;

public class SqlStateTests
{
    [Test]
    public void Only_connected_and_busy_are_connected_and_only_busy_is_busy()
    {
        Assert.That(new Disconnected().IsConnected, Is.False);
        Assert.That(new Connecting("dc1:1433", CancellationToken.None).IsConnected, Is.False);
        Assert.That(new Failed(new InvalidOperationException("no route")).IsConnected, Is.False);
        Assert.That(new Connected(new Uri("sqlserver://dc1/")).IsConnected, Is.True);

        var busy = new Busy("Listing databases", CancellationToken.None);
        Assert.That((busy.IsConnected, busy.IsBusy), Is.EqualTo((true, true)));
        Assert.That(new Connected(new Uri("sqlserver://dc1/")).IsBusy, Is.False);
    }

    [Test]
    public void A_failed_state_says_why_and_a_busy_one_says_what()
    {
        Assert.That(new Failed(new InvalidOperationException("no route")).ToString(), Is.EqualTo("Failed: no route"));
        Assert.That(new Busy("Listing databases", CancellationToken.None).ToString(), Does.StartWith("Busy: Listing databases"));
    }
}
