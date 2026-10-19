using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Threading;
using Serilog;
using Silo.Drawio;
using Silo.Model;
using DataBinding = Silo.DbModel.Global.DataBinding;

namespace Silo.Bindings;

/// <summary>
///     Raised whenever a Silo's data bindings are created, changed or deleted, so the Explorer and the sampler stay current.
/// </summary>
public static class DataBindingEvents
{
    public static event EventHandler? Changed;

    public static void RaiseChanged()
    {
        Changed?.Invoke(null, EventArgs.Empty);
    }
}

/// <summary>
///     The latest reading of a data binding.
/// </summary>
/// <param name="Value">The value read, or null when the read failed.</param>
/// <param name="Time">When it was read (UTC).</param>
/// <param name="Error">Why the read failed, or null.</param>
public sealed record BindingSample(object? Value, DateTime Time, string? Error);

/// <summary>
///     Samples the enabled <see cref="DataBinding" />s of every open Silo on their intervals, and publishes the values to
///     diagrams as the <c>bind</c> scheme: <c>{bind:CpuPercent}</c>. When two open Silos define the same name, the active
///     Silo's wins.
/// </summary>
public sealed class DataBindingService : IDiagramDataProvider
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

    private readonly object                                   _gate    = new();
    private readonly ILogger                                  _log     = Log.ForContext<DataBindingService>();
    private readonly Dictionary<(OpenSilo, int), Poller>      _pollers = new();
    private          Dictionary<string, Poller>               _byName  = new(StringComparer.OrdinalIgnoreCase);
    private          bool                                     _started;

    private DataBindingService()
    {
    }

    public static DataBindingService Current { get; } = new();

    public event Action? Changed;

    public string Scheme => "bind";

    /// <summary>The value of the binding named <paramref name="path" />.</summary>
    public bool TryGet(string path, out object? value)
    {
        value = null;
        if (!TryGetSample(path, out var sample) || sample.Error is not null) return false;
        value = sample.Value;
        return true;
    }

    /// <summary>The latest reading of the binding named <paramref name="name" /> (in the Silo whose binding wins that name).</summary>
    public bool TryGetSample(string name, out BindingSample sample)
    {
        lock (_gate)
        {
            if (_byName.TryGetValue(name.Trim(), out var p) && p.Sample is { } s)
            {
                sample = s;
                return true;
            }
        }

        sample = null!;
        return false;
    }

    /// <summary>Starts sampling (and keeps the set of bindings in step with the open Silos). Call once at startup.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        DiagramData.Register(this);
        OpenSilo.OpenSilos.CollectionChanged += OnSilosChanged;
        DataBindingEvents.Changed            += (_, _) => Rescan();
        if (App.Instance is { } app) app.ActiveSiloChanged += (_, _) => Rescan();
        Rescan();
    }

    public void Stop()
    {
        lock (_gate)
        {
            foreach (var p in _pollers.Values) p.Dispose();
            _pollers.Clear();
            _byName = new Dictionary<string, Poller>(StringComparer.OrdinalIgnoreCase);
            _started = false;
        }

        OpenSilo.OpenSilos.CollectionChanged -= OnSilosChanged;
    }

    private void OnSilosChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Rescan();
    }

    /// <summary>Re-reads every open Silo's bindings, starting, restarting and stopping samplers to match.</summary>
    public void Rescan()
    {
        var active = App.Instance?.ActiveSilo;
        var silos  = OpenSilo.OpenSilos.ToList().OrderBy(s => ReferenceEquals(s, active) ? 0 : 1).ToList();

        var wanted = new List<(OpenSilo Silo, DataBinding Binding)>();
        foreach (var silo in silos)
        {
            if (silo.Data is not IDataBindingStore store) continue;
            try
            {
                wanted.AddRange(store.ListDataBindings().Where(b => b.Enabled && b.Name.Length > 0).Select(b => (silo, b)));
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Could not read the data bindings of {silo}", silo.Name);
            }
        }

        lock (_gate)
        {
            if (!_started) return;

            var keep = new HashSet<(OpenSilo, int)>();
            var byName = new Dictionary<string, Poller>(StringComparer.OrdinalIgnoreCase);

            foreach (var (silo, binding) in wanted)
            {
                var key = (silo, binding.Id);
                keep.Add(key);

                if (!_pollers.TryGetValue(key, out var p) || !p.Matches(binding))
                {
                    p?.Dispose();
                    p             = new Poller(binding, OnSampled, _log);
                    _pollers[key] = p;
                }

                byName.TryAdd(binding.Name, p); // the active Silo comes first, so it wins a shared name
            }

            foreach (var key in _pollers.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                _pollers[key].Dispose();
                _pollers.Remove(key);
            }

            _byName = byName;
        }

        Changed?.Invoke();
    }

    private void OnSampled()
    {
        Changed?.Invoke();
    }

    /// <summary>Samples one binding on its own timer, off the UI thread.</summary>
    private sealed class Poller : IDisposable
    {
        private readonly DataBinding    _binding;
        private readonly ILogger        _log;
        private readonly Action         _onChange;
        private readonly Timer          _timer;
        private          IBindingReader? _reader;
        private          int            _busy;
        private          bool           _disposed;
        private          string?        _lastError;

        public Poller(DataBinding binding, Action onChange, ILogger log)
        {
            _binding  = binding.Clone();
            _onChange = onChange;
            _log      = log;
            var every = binding.Interval < MinInterval ? MinInterval : binding.Interval;
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, every);
        }

        public BindingSample? Sample { get; private set; }

        public bool Matches(DataBinding b)
        {
            return b.Name == _binding.Name && b.Source == _binding.Source && b.Interval == _binding.Interval;
        }

        private void Tick()
        {
            if (_disposed || Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                object? value;
                string? error = null;
                try
                {
                    _reader ??= BindingSources.Open(_binding.Source);
                    value   =   _reader.Read();
                }
                catch (Exception ex)
                {
                    value = null;
                    error = ex.Message;
                    _reader?.Dispose();
                    _reader = null; // try opening again next time
                    if (error != _lastError) _log.Warning("Data binding {name} ({source}) failed: {error}", _binding.Name, _binding.Source, error);
                }

                _lastError = error;
                var old = Sample;
                Sample = new BindingSample(value, DateTime.UtcNow, error);
                if (old is null || !Equals(old.Value, value) || old.Error != error) _onChange();
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Dispose();
            // let a tick in progress finish with its reader; it is dropped either way
            ThreadPool.QueueUserWorkItem(_ =>
                                         {
                                             SpinWait.SpinUntil(() => Volatile.Read(ref _busy) == 0, 2000);
                                             _reader?.Dispose();
                                         });
        }
    }
}
