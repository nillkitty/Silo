using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using Newtonsoft.Json;
using Serilog;
using Silo.Connectors.File;
using Silo.Connectors.System;
using Silo.Providers.Monitoring;
using Silo.Providers.Reflection;
using Silo.Storage;
using Silo.Model;

namespace Silo.Roles;

/// <summary>
///     A standing instruction: keep the items found in a container (a folder, a drive, a registry key) added to the Silo with a
///     role.
/// </summary>
/// <param name="Id">Names the rule (a hash of the container's URI, so a container has one).</param>
/// <param name="Container">Where the container is: a file: URI, or a reg: URI for a key (the items are reached through the path).</param>
/// <param name="Role">The role new items get (unless a <see cref="RoleHandlingRule" /> says another).</param>
/// <param name="Recursive">Also take the items in the folders or keys inside.</param>
/// <param name="Monitor">How to find out about changes: <see cref="MonitorModes.None" /> only adds what is there when it is set up or scanned.</param>
/// <param name="UpdateChanged">Whether an item that changed is updated (its record, and the role when the handling rules now say another).</param>
public sealed record AutoAddRule(
    string       Id,
    Uri          Container,
    string       Role,
    bool         Recursive,
    MonitorModes Monitor,
    bool         UpdateChanged = true);

/// <summary>An item auto-add has added.</summary>
/// <param name="Uri">Where it is.</param>
/// <param name="Name">The path or value name.</param>
/// <param name="Role">Its role.</param>
/// <param name="Kind">The <see cref="ItemKind" /> it was classified as.</param>
/// <param name="Format">The file type that decided it.</param>
/// <param name="Spec">The data spec that matched its content.</param>
/// <param name="Signature">What it looked like when last read (see <see cref="ContainerItem.Signature" />): a different one means it changed.</param>
/// <param name="Added">When it was added (UTC).</param>
/// <param name="Updated">When it was last updated (UTC).</param>
/// <param name="RuleId">The rule that found it.</param>
public sealed record AutoAddedItem(
    Uri      Uri,
    string   Name,
    string   Role,
    string   Kind,
    string?  Format,
    string?  Spec,
    string   Signature,
    DateTime Added,
    DateTime Updated,
    string   RuleId);

/// <summary>What one scan did.</summary>
public sealed record AutoAddResult(int Added, int Updated, int Skipped, int Unchanged)
{
    /// <summary>Whether the scan changed the Silo.</summary>
    public bool Changed => Added + Updated > 0;

    public override string ToString()
    {
        return $"{Added} added, {Updated} updated, {Skipped} skipped, {Unchanged} unchanged";
    }
}

/// <summary>
///     Adds the new items of a container to the Silo, and updates the ones that changed, with a role: classifies each item
///     (<see cref="ItemClassifier" />), asks the handling settings (<see cref="RoleHandlingTable" />) what to do with that
///     combination, and keeps an <see cref="AutoAddedItem" /> for it in the Silo. A rule with a monitor mode keeps doing so as the container
///     changes (<see cref="IChangeMonitorable" />): the monitor says that something changed, and the container is scanned
///     again, which reads only the items whose signature differs. One service per open Silo.
/// </summary>
public sealed class AutoAddService
{
    private const string RulePrefix = "autoadd/rule/";
    private const string ItemPrefix = "autoadd/item/";

    /// <summary>How long changes are gathered before the container is scanned again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(800);

    private static readonly ConditionalWeakTable<IStoredStore, AutoAddService> Services = new();
    private static          bool                                               _watching;

    private readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);
    private readonly IStoredStore                _store;

    private AutoAddService(IStoredStore store)
    {
        _store = store;
    }

    /// <summary>The service for the Silo's store.</summary>
    public static AutoAddService For(IStoredStore store)
    {
        return Services.GetValue(store, s => new AutoAddService(s));
    }

    /// <summary>
    ///     Starts the rules of each Silo as it opens (and of those already open). Call once at startup, on the UI thread.
    /// </summary>
    public static void ResumeOnOpen()
    {
        if (_watching) return;

        _watching = true;
        OpenSilo.OpenSilos.CollectionChanged += (_, e) =>
                                                {
                                                    if (e.Action == NotifyCollectionChangedAction.Add)
                                                        foreach (var silo in e.NewItems!.OfType<OpenSilo>())
                                                            Resume(silo);
                                                };
        foreach (var silo in OpenSilo.OpenSilos) Resume(silo);
    }

    private static void Resume(OpenSilo silo)
    {
        if (silo.Data is IStoredStore store) _ = Task.Run(() => For(store).ResumeAll());
    }

    // ---- the rules ------------------------------------------------------------------------------

    /// <summary>The id of the rule for a container.</summary>
    public static string IdFor(Uri container)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(container.AbsoluteUri.ToLowerInvariant())))[..16];
    }

    /// <summary>Every rule set up in the Silo.</summary>
    public IReadOnlyList<AutoAddRule> Rules()
    {
        var rules = new List<AutoAddRule>();
        foreach (var record in _store.ListStored(RulePrefix, StoredScope.Shared))
        {
            try
            {
                if (JsonConvert.DeserializeObject<AutoAddRule>(record.Data) is { } rule) rules.Add(rule);
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "The auto-add rule {Key} could not be read", record.Key);
            }
        }

        return rules;
    }

    /// <summary>The rule for the container, if one is set up.</summary>
    public AutoAddRule? RuleFor(Uri container)
    {
        return Rules().FirstOrDefault(r => r.Id == IdFor(container));
    }

    /// <summary>The items auto-add has added to the Silo.</summary>
    public IReadOnlyList<AutoAddedItem> Items()
    {
        return ReadItems().Values.ToList();
    }

    private Dictionary<string, AutoAddedItem> ReadItems()
    {
        var items = new Dictionary<string, AutoAddedItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in _store.ListStored(ItemPrefix, StoredScope.Shared))
        {
            try
            {
                if (JsonConvert.DeserializeObject<AutoAddedItem>(record.Data) is { } item) items[item.Uri.AbsoluteUri] = item;
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "The auto-added item {Key} could not be read", record.Key);
            }
        }

        return items;
    }

    // ---- setting up and taking down ---------------------------------------------------------------

    /// <summary>
    ///     Saves the rule, adds what is in the container now, and starts monitoring it as the rule says.
    /// </summary>
    public async Task<AutoAddResult> EnableAsync(AutoAddRule rule, IItemContainer container)
    {
        Write(RulePrefix + rule.Id, typeof(AutoAddRule), JsonConvert.SerializeObject(rule));
        Stop(rule.Id);

        var result = await ScanAsync(rule, container);
        Start(rule, container);
        return result;
    }

    /// <summary>Removes the rule and stops monitoring. The items already added stay.</summary>
    public void Disable(string id)
    {
        Stop(id);
        _store.DeleteStored(RulePrefix + id, StoredScope.Shared);
    }

    private void ResumeAll()
    {
        foreach (var rule in Rules())
        {
            var container = ContainerFor(rule.Container);
            if (container is null || !container.CanEnumerate)
            {
                Log.Debug("The auto-add container {Uri} cannot be listed; its rule is not started", rule.Container);
                continue;
            }

            Start(rule, container);
        }
    }

    /// <summary>The container a rule's URI names (a folder or drive, or a registry key); null when it is neither or is not there.</summary>
    public static IItemContainer? ContainerFor(Uri uri)
    {
        try
        {
            if (uri.IsFile) return new FileModel(uri.LocalPath);

            var path = new RegistryUriProvider().ParseUri(uri, false);
            if (path is null || path.IsLocalRoot) return null;

            var hive = RegistryKey.OpenBaseKey(path.Hive, RegistryView.Default);
            var key  = path.Path is "*" or "" ? hive : hive.OpenSubKey(path.Path.Trim('\\'));
            return key is null ? null : new RegistryModel(key, path.FullName);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "No auto-add container for {Uri}", uri);
            return null;
        }
    }

    // ---- scanning ------------------------------------------------------------------------------

    /// <summary>
    ///     Reads the container (on the thread pool), adds the new items and updates the changed ones. The Silo is written on the UI
    ///     thread, once, at the end.
    /// </summary>
    public async Task<AutoAddResult> ScanAsync(AutoAddRule rule, IItemContainer container, CancellationToken cancel = default)
    {
        var (result, writes) = await Task.Run(() => Scan(rule, container, cancel), cancel);
        if (writes.Count == 0) return result;

        void Apply()
        {
            foreach (var item in writes)
                Write(ItemPrefix + item.Uri.AbsoluteUri, typeof(AutoAddedItem), JsonConvert.SerializeObject(item));
        }

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) await dispatcher.InvokeAsync(Apply);
        else Apply();

        return result;
    }

    private (AutoAddResult Result, List<AutoAddedItem> Writes) Scan(AutoAddRule rule, IItemContainer container, CancellationToken cancel)
    {
        var known    = ReadItems();
        var handling = RoleHandlingTable.Load(_store);
        var specs    = ItemClassifier.Specs();
        var writes   = new List<AutoAddedItem>();
        int added = 0, updated = 0, skipped = 0, unchanged = 0;
        var now = DateTime.UtcNow;

        foreach (var item in container.EnumerateItems(rule.Recursive, cancel))
        {
            if (item.IsContainer) continue;

            known.TryGetValue(item.Uri.AbsoluteUri, out var before);
            if (before is not null && before.Signature == item.Signature)
            {
                unchanged++;
                continue;
            }

            if (before is not null && !rule.UpdateChanged)
            {
                unchanged++;
                continue;
            }

            var kind   = ItemClassifier.Classify(item, specs);
            var how    = handling.Find(kind);
            if (how?.Action == HandlingAction.Skip)
            {
                skipped++;
                continue;
            }

            string role = how?.Role ?? rule.Role;
            writes.Add(new AutoAddedItem(item.Uri, item.Name, role, kind.Kind.ToString(), kind.Format, kind.Spec, item.Signature,
                                         before?.Added ?? now, now, rule.Id));
            if (before is null) added++;
            else updated++;
        }

        return (new AutoAddResult(added, updated, skipped, unchanged), writes);
    }

    private void Write(string key, Type type, string json)
    {
        _store.SetStored(key, StoredCodec.TypeNameOf(type), StoredCodec.Json, json, StoredScope.Shared);
    }

    // ---- monitoring ----------------------------------------------------------------------------

    private void Start(AutoAddRule rule, IItemContainer container)
    {
        if (rule.Monitor == MonitorModes.None || container is not IChangeMonitorable monitorable) return;

        var mode = monitorable.SupportedModes.HasFlag(rule.Monitor) ? rule.Monitor : monitorable.Best();
        if (mode == MonitorModes.None) return;

        var running = new Running();
        running.Monitor = monitorable.StartMonitoring(mode, _ => running.Poke(() => Rescan(rule, container, running)), rule.Recursive);
        if (running.Monitor is null) return;

        lock (_running) _running[rule.Id] = running;
        Log.Information("Auto-add is watching {Container} ({Mode})", container.ContainerName, mode);
    }

    private void Stop(string id)
    {
        Running? running;
        lock (_running)
        {
            if (!_running.Remove(id, out running)) return;
        }

        running.Dispose();
    }

    /// <summary>Whether a rule is being monitored.</summary>
    public bool IsMonitoring(string id)
    {
        lock (_running) return _running.ContainsKey(id);
    }

    private async void Rescan(AutoAddRule rule, IItemContainer container, Running running)
    {
        try
        {
            var result = await ScanAsync(rule, container, running.Cancel.Token);
            if (result.Changed) App.Status($"Auto-add {container.ContainerName}: {result}");
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-add could not scan {Container}", container.ContainerName);
        }
    }

    /// <summary>A monitor that is running, and the timer that gathers a burst of changes into one scan.</summary>
    private sealed class Running : IDisposable
    {
        private Timer? _timer;

        public CancellationTokenSource Cancel { get; } = new();

        public IDisposable? Monitor { get; set; }

        public void Dispose()
        {
            Cancel.Cancel();
            lock (this)
            {
                _timer?.Dispose();
                _timer = null;
            }

            Monitor?.Dispose();
        }

        /// <summary>Runs <paramref name="scan" /> once the changes have settled (each change pushes it back).</summary>
        public void Poke(Action scan)
        {
            lock (this)
            {
                if (Cancel.IsCancellationRequested) return;

                _timer?.Dispose();
                _timer = new Timer(_ => scan(), null, Settle, Timeout.InfiniteTimeSpan);
            }
        }
    }
}
