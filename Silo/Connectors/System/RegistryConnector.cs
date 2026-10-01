using Microsoft.Win32;

namespace Silo.Connectors.System;

/// <summary>
///     Holds a reference to a native registry hive and native path (e.g.
///     'Software\Microsoft\Windows\CurrentVersion'.
/// </summary>
/// <param name="Hive">The registry hive the path is a child of</param>
/// <param name="Path">The \-separated path of registry keys</param>
/// <param name="ValueName">Optional name of the value being read/written</param>
public record RegistryPath(RegistryHive Hive, string Path, string? ValueName)
{
    public static RegistryPath LocalRoot => new(RegistryHive.LocalMachine, "*", null);

    /// <summary>
    ///     Returns the key name (e.g. 'CurrentVersion') if a sub key,
    ///     hive name (e.g. 'HKEY_LOCAL_MACHINE') if a root key,
    ///     or "Local Registry" if hklm:///*
    /// </summary>
    public string Name
    {
        get
        {
            if (Path == "*" || Path.Nill())
                return Hive == RegistryHive.LocalMachine ? "Local Registry" : HiveDisplayName(Hive);

            var segments = Path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length > 0 ? segments[^1] : HiveDisplayName(Hive);
        }
    }

    private static string HiveDisplayName(RegistryHive hive) => hive switch
    {
        RegistryHive.ClassesRoot     => "HKEY_CLASSES_ROOT",
        RegistryHive.CurrentUser     => "HKEY_CURRENT_USER",
        RegistryHive.LocalMachine    => "HKEY_LOCAL_MACHINE",
        RegistryHive.Users           => "HKEY_USERS",
        RegistryHive.CurrentConfig   => "HKEY_CURRENT_CONFIG",
        RegistryHive.PerformanceData => "HKEY_PERFORMANCE_DATA",
        _                            => hive.ToString()
    };
}

public class RegistryConnector : IConnector
{
    public ConnectorSetupFlags Flags     { get; } = ConnectorSetupFlags.Connectionless;
    public Exception?          LastError { get; private set; }

    public List<object> Metadata { get; } = [];

    public IConnection CreateConnection(ConnectionRequest crq)
    {
        Uri  def             = new("reg:///", UriKind.Absolute);
        Uri? uri             = null;
        if (crq != null) uri = crq.RequestedUri;

        uri ??= def;

        var urip = App.Require<IUriProvider<RegistryPath>>();
        var path = urip.ParseUri(uri, true);

        return new RegistryConnection(this, crq?.Destination, crq?.ParentNode, path);
    }

    public Type GetStateType()
    {
        return typeof(RegistryConnectionState);
    }
}

public abstract record RegistryConnectionState
{
    public record Online(RegistryKey Key) : RegistryConnectionState;

    public record ReadOnly(RegistryKey Key) : RegistryConnectionState;

    public record Inaccessible(Exception? Error) : RegistryConnectionState;

    public record Unloaded : RegistryConnectionState;
}
