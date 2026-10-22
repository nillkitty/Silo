using Silo.Contracts.ConnectorServices;
using Silo.ViewModel;

namespace Silo.Connectors.Ldap;

public abstract record LdapState(bool IsConnected, bool IsFailed, string DisplayName) : IDisplayName, IConnectionState
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

    public bool IsDisposed => false;

    [Icon("StateDisconnected")]
    public record Disconnected() : LdapState(false, false, nameof(Disconnected));

    [Icon("StateConnecting")]
    public record Connecting(Uri Target) : LdapState(false, false, $"Connecting to {Target.Host}:{Target.Port}");

    [Icon("StateBusy")]
    public record Busy(Connected Inner, string Operation, CancellationToken Cancel)
        : LdapState(true, false, $"Busy - {Operation}"), IDisplayName
    {
        string IDisplayName.DisplayName => ToString();

        public override string ToString()
        {
            return $"Busy - {Operation} ({Duration})";
        }
    }

    [Icon("StateConnected")]
    public record Connected() : LdapState(true, false, nameof(Connected));

    [Icon("StateFailed")]
    public record Failed(Exception Exception) : LdapState(false, true, $"Failed: {Exception.Message}");

    // winding down the operation it was busy with: the busy icon
    [Icon("StateBusy")]
    public record Aborting(Busy Inner) : LdapState(true, false, "Aborting Operation")
    {
        public override string ToString()
        {
            return $"Aborting ({Inner.Operation}) ({Duration})";
        }
    }
}