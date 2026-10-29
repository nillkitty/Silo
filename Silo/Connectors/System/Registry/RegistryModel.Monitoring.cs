using System.Runtime.InteropServices;
using Microsoft.Win32;
using Silo.Providers.Monitoring;

namespace Silo.Connectors.System;

/// <summary>
///     Change monitoring for the registry: passive through <c>RegNotifyChangeKeyValue</c> (which says that something under the
///     key changed, so the key is read again to find what), active by reading the key's subkeys and values on an interval.
/// </summary>
public partial class RegistryModel
{
    private const uint NotifyAll = 0x0F; // name | attributes | last set | security

    /// <summary>Keys and hives can be monitored, both ways; the root, a value, or a key that could not be opened cannot.</summary>
    public MonitorModes SupportedModes =>
        IsContainer && Kind != RegistryItemKind.Root && Data is not null ? MonitorModes.Passive | MonitorModes.Active : MonitorModes.None;

    /// <inheritdoc />
    public IDisposable? StartMonitoring(MonitorModes mode, Action<NodeChange> onChange, bool recursive = false, TimeSpan? interval = null)
    {
        if (mode is not (MonitorModes.Passive or MonitorModes.Active) || !SupportedModes.HasFlag(mode)) return null;

        // the monitor reads through its own handle, so it goes on working when the node's key is closed
        string name = Path;
        var    key  = ReopenKey();
        if (key is null) return null;

        if (mode == MonitorModes.Active) return new Owned(ChangeMonitoring.Poll(() => Snapshot(key, name), UriOf, onChange, interval), key);

        var differ = ChangeMonitoring.Diff(() => Snapshot(key, name), UriOf, onChange, out var look);
        return new Owned(new Notifier(key, recursive, look, onChange, name, Uri, differ), key);
    }

    private RegistryKey? ReopenKey()
    {
        if (Data is null) return null;

        string full = Data.Name;
        int    cut  = full.IndexOf('\\');
        string hive = cut < 0 ? full : full[..cut];
        string sub  = cut < 0 ? string.Empty : full[(cut + 1)..];

        var view = Data.View;
        RegistryHive? baseHive = hive.ToUpperInvariant() switch
                                 {
                                     "HKEY_CLASSES_ROOT"   => RegistryHive.ClassesRoot,
                                     "HKEY_CURRENT_USER"   => RegistryHive.CurrentUser,
                                     "HKEY_LOCAL_MACHINE"  => RegistryHive.LocalMachine,
                                     "HKEY_USERS"          => RegistryHive.Users,
                                     "HKEY_CURRENT_CONFIG" => RegistryHive.CurrentConfig,
                                     _                     => null
                                 };
        if (baseHive is null) return null;

        return TryOpen(() =>
                       {
                           using var root = RegistryKey.OpenBaseKey(baseHive.Value, view);
                           return sub.Length == 0 ? RegistryKey.OpenBaseKey(baseHive.Value, view) : root.OpenSubKey(sub);
                       });
    }

    private static Uri? UriOf(string name)
    {
        int mark = name.IndexOf('?');
        return mark < 0 ? RegistryUris.FromKeyName(name) : RegistryUris.FromKeyName(name[..mark], name[(mark + 1)..]);
    }

    private static IReadOnlyDictionary<string, string> Snapshot(RegistryKey key, string path)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sub in key.GetSubKeyNames())
            snapshot[path + "\\" + sub] = "key";

        foreach (string value in key.GetValueNames())
            snapshot[path + "?" + value] = ValueSignature(key, value, out _, out _);

        return snapshot;
    }

    /// <summary>Disposes a monitor and the key it reads.</summary>
    private sealed class Owned(IDisposable monitor, RegistryKey key) : IDisposable
    {
        public void Dispose()
        {
            monitor.Dispose();
            key.Dispose();
        }
    }

    /// <summary>Waits (on its own thread) for the registry to say the key changed, and then has the key read again.</summary>
    private sealed class Notifier : IDisposable
    {
        private readonly IDisposable      _differ;
        private readonly ManualResetEvent _changed = new(false);
        private readonly ManualResetEvent _stop    = new(false);
        private readonly Thread           _thread;

        public Notifier(RegistryKey key, bool recursive, Action look, Action<NodeChange> onChange, string name, Uri uri, IDisposable differ)
        {
            _differ = differ;
            _thread = new Thread(() => Run(key, recursive, look, onChange, name, uri)) { IsBackground = true, Name = "Registry monitor " + name };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop.Set();
            _thread.Join(TimeSpan.FromSeconds(2));
            _differ.Dispose();
            _changed.Dispose();
            _stop.Dispose();
        }

        private void Run(RegistryKey key, bool recursive, Action look, Action<NodeChange> onChange, string name, Uri uri)
        {
            while (true)
            {
                _changed.Reset();
                if (RegNotifyChangeKeyValue(key.Handle, recursive, NotifyAll, _changed.SafeWaitHandle, true) != 0)
                {
                    onChange(new NodeChange(NodeChangeKind.Reset, name, uri)); // cannot be watched any more (the key was deleted, say)
                    return;
                }

                if (WaitHandle.WaitAny([_changed, _stop]) == 1) return;

                look();
            }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegNotifyChangeKeyValue(SafeHandle key, bool watchSubtree, uint filter, SafeHandle @event, bool asynchronous);
    }
}
