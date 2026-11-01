using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Serilog;

namespace Silo.Tools.Battery;

/// <summary>What the battery was doing at one moment.</summary>
/// <param name="Time">When it was read (UTC).</param>
/// <param name="Percent">The charge, 0 to 100.</param>
/// <param name="OnAc">Whether the computer was on mains power.</param>
/// <param name="Charging">Whether the battery was taking charge.</param>
public sealed record BatterySample(DateTime Time, int Percent, bool OnAc, bool Charging);

/// <summary>
///     Reads the battery now and then and keeps what it read (the last day, saved so it survives a restart): the charge over time, and whether
///     it was on mains power or charging, so a level that falls while plugged in shows.
/// </summary>
public sealed class BatteryHistory : IDisposable
{
    private static readonly TimeSpan Keep      = TimeSpan.FromHours(24);
    private static readonly TimeSpan Interval  = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);

    private readonly object              _gate    = new();
    private readonly List<BatterySample> _samples = [];
    private readonly Timer               _timer;
    private          DateTime            _lastSave = DateTime.UtcNow;

    public BatteryHistory()
    {
        Load();
        _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, Interval);
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerMode;
    }

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Silo", "battery-history.json");

    /// <summary>True when the computer has a battery.</summary>
    public bool HasBattery { get; private set; } = true;

    /// <summary>How long the battery will last, or null when Windows does not know (or it is not in use).</summary>
    public TimeSpan? TimeLeft { get; private set; }

    /// <summary>A reading was added.</summary>
    public event EventHandler? Changed;

    /// <summary>What was read in the last <paramref name="span" />, oldest first.</summary>
    public IReadOnlyList<BatterySample> Last(TimeSpan span)
    {
        var since = DateTime.UtcNow - span;
        lock (_gate) return _samples.Where(s => s.Time >= since).ToList();
    }

    public BatterySample? Latest
    {
        get
        {
            lock (_gate) return _samples.Count == 0 ? null : _samples[^1];
        }
    }

    /// <summary>
    ///     Says what is odd, if anything: the charge fell while on mains power (or while charging) over the last stretch. Null when it did not.
    /// </summary>
    public string? Warning()
    {
        var recent = Last(TimeSpan.FromMinutes(15));
        if (recent.Count < 3 || !recent[^1].OnAc) return null;

        // the run at the end that was on mains power
        int start = recent.Count - 1;
        while (start > 0 && recent[start - 1].OnAc) start--;
        if (recent.Count - start < 3) return null;

        int drop = recent[start].Percent - recent[^1].Percent;
        if (drop < 1) return null;

        int minutes = (int)Math.Max(1, (recent[^1].Time - recent[start].Time).TotalMinutes);
        return $"Dropping on AC: -{drop}% in {minutes} min" + (recent[^1].Charging ? " (while charging)" : string.Empty);
    }

    private void OnPowerMode(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        Sample();
    }

    private void Sample()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return;

            // flag 128: no battery; percent 255: unknown
            if ((status.BatteryFlag & 128) != 0 || status.BatteryLifePercent == 255)
            {
                HasBattery = (status.BatteryFlag & 128) == 0;
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }

            HasBattery = true;
            TimeLeft   = status.BatteryLifeTime == uint.MaxValue ? null : TimeSpan.FromSeconds(status.BatteryLifeTime);
            var sample = new BatterySample(DateTime.UtcNow, status.BatteryLifePercent, status.ACLineStatus == 1, (status.BatteryFlag & 8) != 0);

            lock (_gate)
            {
                _samples.Add(sample);
                _samples.RemoveAll(s => s.Time < sample.Time - Keep);
            }

            if (DateTime.UtcNow - _lastSave > SaveEvery) Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "The battery could not be read");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;

            var saved = JsonConvert.DeserializeObject<List<BatterySample>>(File.ReadAllText(FilePath)) ?? [];
            lock (_gate) _samples.AddRange(saved.Where(s => s.Time >= DateTime.UtcNow - Keep));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "The saved battery history could not be read");
        }
    }

    private void Save()
    {
        _lastSave = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            List<BatterySample> copy;
            lock (_gate) copy = [.. _samples];
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(copy));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "The battery history could not be saved");
        }
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerMode;
        _timer.Dispose();
        Save();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
