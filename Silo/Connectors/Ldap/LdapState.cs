using Silo.Contracts.ConnectorServices;

namespace Silo.Connectors.Ldap;

public abstract record LdapState(string DisplayName) : IDisplayName
{
    /// <summary>
    ///     How long it has been since the state was entered
    /// </summary>
    public TimeSpan Duration => DateTime.Now - Time;

    /// <summary>The key of the state's icon (see <c>Resources/NavIcons.xaml</c>), from its <see cref="IconAttribute" />.</summary>
    public string IconKey => IconAttribute.KeyOf(GetType()) ?? "Plug";

    /// <summary>
    ///     When the state was entered
    /// </summary>
    public DateTime Time { get; } = DateTime.Now;

    [Icon("StateDisconnected")]
    public record Disconnected() : LdapState(nameof(Disconnected));

    [Icon("StateConnecting")]
    public record Connecting(Uri Target) : LdapState($"Connecting to {Target.Host}:{Target.Port}");

    [Icon("StateBusy")]
    public record Busy(Connected Inner, string Operation, CancellationToken Cancel) : LdapState($"Busy - {Operation}"), IDisplayName
    {
        string IDisplayName.DisplayName => ToString();

        public override string ToString()
        {
            return $"Busy - {Operation} ({Duration})";
        }
    }

    [Icon("StateConnected")]
    public record Connected() : LdapState(nameof(Connected));

    [Icon("StateFailed")]
    public record Failed(Exception Exception) : LdapState($"Failed: {Exception.Message}");

    public record Aborting(Busy Inner) : LdapState("Aborting Operation")
    {
        public override string ToString()
        {
            return $"Aborting ({Inner.Operation}) ({Duration})";
        }
    }
}