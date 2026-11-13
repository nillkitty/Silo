using System.IO;
using System.Security.Cryptography;
using System.Speech.Synthesis;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;

namespace Silo.Labs.Soundboard;

/// <summary>Makes tones as WAV files: a single tone, two at once, or a run of keypad digits in DTMF or MF.</summary>
public static class ToneSynth
{
    public const int Rate = 44100;

    private static readonly double[] DtmfRows = [697, 770, 852, 941];

    private static readonly double[] DtmfCols = [1209, 1336, 1477, 1633];

    private const string DtmfKeys = "123A456B789C*0#D";

    private static readonly Dictionary<char, (double, double)> Mf = new()
                                                                    {
                                                                        ['1'] = (700, 900), ['2'] = (700, 1100), ['3'] = (900, 1100), ['4'] = (700, 1300), ['5'] = (900, 1300), ['6'] = (1100, 1300),
                                                                        ['7'] = (700, 1500), ['8'] = (900, 1500), ['9'] = (1100, 1500), ['0'] = (1300, 1500), ['*'] = (1100, 1700), ['#'] = (1500, 1700)
                                                                    };

    /// <summary>The two frequencies of a key in a standard; false when the standard has no such key.</summary>
    public static bool TryDigit(DualStandard standard, char key, out (double Low, double High) pair)
    {
        key = char.ToUpperInvariant(key);
        switch (standard)
        {
            case DualStandard.Dtmf:
                int at = DtmfKeys.IndexOf(key);
                if (at >= 0)
                {
                    pair = (DtmfRows[at / 4], DtmfCols[at % 4]);
                    return true;
                }

                break;
            case DualStandard.Mf when Mf.TryGetValue(key, out var mf):
                pair = mf;
                return true;
        }

        pair = default;
        return false;
    }

    /// <summary>The keys of the digits that a standard can play (spaces, dashes and the like are skipped, a comma is a pause).</summary>
    public static IReadOnlyList<char> Playable(DualStandard standard, string digits)
    {
        return digits.Where(c => c == ',' || TryDigit(standard, c, out _)).ToList();
    }

    /// <summary>
    ///     A tone of <paramref name="milliseconds" />; a tone of no length is one second made to loop without a click (the frequencies are whole, so a second holds whole cycles).
    ///     <paramref name="hz2" /> is another tone sounded with it; 0 for none.
    /// </summary>
    public static byte[] Tone(double hz1, double hz2, int milliseconds)
    {
        bool loop = milliseconds <= 0;
        if (loop)
        {
            hz1 = Math.Round(hz1);
            hz2 = Math.Round(hz2);
        }

        int count = (loop ? 1000 : Math.Clamp(milliseconds, 1, 60_000)) * Rate / 1000;
        var samples = new short[count];
        Fill(samples, 0, count, hz1, hz2, !loop);
        return Wav(samples);
    }

    /// <summary>Digits one after another, each a tone and a gap.</summary>
    public static byte[] Digits(DualStandard standard, string digits, int toneMs, int gapMs = 60)
    {
        toneMs = toneMs <= 0 ? 100 : Math.Clamp(toneMs, 20, 5000);
        var keys = Playable(standard, digits);
        int tone = toneMs * Rate / 1000, gap = gapMs * Rate / 1000, pause = 500 * Rate / 1000;
        var samples = new List<short>();
        foreach (char key in keys)
        {
            if (key == ',')
            {
                samples.AddRange(new short[pause]);
                continue;
            }

            TryDigit(standard, key, out var pair);
            var part = new short[tone];
            Fill(part, 0, tone, pair.Low, pair.High, true);
            samples.AddRange(part);
            samples.AddRange(new short[gap]);
        }

        if (samples.Count == 0) samples.AddRange(new short[Rate / 100]);
        return Wav(samples.ToArray());
    }

    private static void Fill(short[] samples, int from, int count, double hz1, double hz2, bool fade)
    {
        double amp  = hz2 > 0 && hz1 > 0 ? 0.35 : 0.5;
        int    ramp = fade ? Math.Min(Rate / 200, count / 2) : 0;
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / Rate;
            double v = (hz1 > 0 ? Math.Sin(2 * Math.PI * hz1 * t) : 0) + (hz2 > 0 ? Math.Sin(2 * Math.PI * hz2 * t) : 0);
            double envelope = ramp == 0 ? 1 : i < ramp ? (double)i / ramp : i >= count - ramp ? (double)(count - 1 - i) / ramp : 1;
            samples[from + i] = (short)Math.Round(v * amp * envelope * short.MaxValue);
        }
    }

    /// <summary>16-bit mono PCM in a WAV container.</summary>
    public static byte[] Wav(short[] samples)
    {
        int bytes = samples.Length * 2;
        using var stream = new MemoryStream(44 + bytes);
        using var w      = new BinaryWriter(stream);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + bytes);
        w.Write("WAVEfmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(bytes);
        foreach (short s in samples) w.Write(s);
        w.Flush();
        return stream.ToArray();
    }
}

/// <summary>Speaks text into WAV data with the voices Windows has.</summary>
internal static class SoundboardSpeech
{
    public static IReadOnlyList<string> Voices()
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            return synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing the voices failed");
            return [];
        }
    }

    /// <summary>Speaks the text; slow, so not on the window's thread.</summary>
    public static byte[] Render(string text, string voice, int rate)
    {
        using var synth = new SpeechSynthesizer { Rate = Math.Clamp(rate, -10, 10) };
        if (voice.Length > 0)
            try
            {
                synth.SelectVoice(voice);
            }
            catch (ArgumentException)
            {
                // the voice is not on this machine: the default one speaks
            }

        using var stream = new MemoryStream();
        synth.SetOutputToWaveStream(stream);
        synth.Speak(text);
        return stream.ToArray();
    }
}

/// <summary>The files sounds are played from: made once and kept by what is in them.</summary>
internal static class SoundFiles
{
    private static readonly string Directory_ = Path.Combine(Path.GetTempPath(), "Silo", "soundboard");

    /// <summary>The path of a file holding the data (written when it is not there already).</summary>
    public static string For(byte[] data, string extension)
    {
        string name = Convert.ToHexString(SHA256.HashData(data))[..24].ToLowerInvariant() + extension;
        string path = Path.Combine(Directory_, name);
        if (File.Exists(path) && new FileInfo(path).Length == data.Length) return path;

        Directory.CreateDirectory(Directory_);
        File.WriteAllBytes(path, data);
        return path;
    }
}

/// <summary>Finds the file a button plays: a Silo sound or a local file, a tone or a run of digits. Speech is made by <see cref="SpeechFiles" />.</summary>
internal static class SoundSource
{
    /// <summary>The file for the button, or what is wrong; null file and problem for a button that needs speech.</summary>
    public static (string? File, string? Problem) Resolve(SoundButton b)
    {
        try
        {
            switch (b.Action)
            {
                case ButtonAction.Sound:
                    return b.Source.Length == 0 ? (null, "This button has no sound yet.") : SoundFile(b.Source);
                case ButtonAction.Tone:
                    return (SoundFiles.For(ToneSynth.Tone(b.ToneHz, 0, b.ToneMs), ".wav"), null);
                case ButtonAction.DualTone when b.Standard == DualStandard.Custom:
                    return (SoundFiles.For(ToneSynth.Tone(b.ToneHz, b.ToneHz2, b.ToneMs), ".wav"), null);
                case ButtonAction.DualTone:
                    return ToneSynth.Playable(b.Standard, b.Digits).Count == 0
                               ? (null, $"There is nothing in \"{b.Digits}\" that {b.Standard.ToString().ToUpperInvariant()} can play.")
                               : (SoundFiles.For(ToneSynth.Digits(b.Standard, b.Digits, b.ToneMs), ".wav"), null);
                default:
                    return (null, null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "The sound could not be prepared: " + ex.Message);
        }
    }

    private static (string?, string?) SoundFile(string source)
    {
        if (!SoundboardSounds.IsSilo(source)) return File.Exists(source) ? (source, null) : (null, $"The file {source} is not there.");

        string name = SoundboardSounds.SiloName(source);
        if (!SoundboardSounds.Clips.IsAvailable) return (null, "Open a Silo to play sounds kept in it.");

        return SoundboardSounds.Clips.Load(name) is { Data.Length: > 0 } clip ? (SoundFiles.For(clip.Data, clip.Extension), null) : (null, $"There is no sound called {name} in the Silo.");
    }
}

/// <summary>Speech made ahead of time, so pressing a button speaks at once.</summary>
internal static class SpeechFiles
{
    private static readonly Dictionary<string, string> Made = new();

    private static string KeyOf(SoundButton b) => $"{b.Voice}|{b.SpeechRate}|{b.Speech}";

    public static bool IsReady(SoundButton b, out string? file)
    {
        lock (Made) return Made.TryGetValue(KeyOf(b), out file);
    }

    /// <summary>Speaks the button's text into a file (or finds it, when it was made already).</summary>
    public static Task<(string? File, string? Problem)> MakeAsync(SoundButton b)
    {
        if (b.Speech.Trim().Length == 0) return Task.FromResult<(string?, string?)>((null, "There is no text to speak."));
        if (IsReady(b, out var done)) return Task.FromResult<(string?, string?)>((done, null));

        string key = KeyOf(b), text = b.Speech, voice = b.Voice;
        int    rate = b.SpeechRate;
        return Task.Run<(string?, string?)>(() =>
                                            {
                                                try
                                                {
                                                    string file = SoundFiles.For(SoundboardSpeech.Render(text, voice, rate), ".wav");
                                                    lock (Made) Made[key] = file;
                                                    return (file, null);
                                                }
                                                catch (Exception ex)
                                                {
                                                    Log.Warning(ex, "Speaking failed");
                                                    return (null, "Speaking failed: " + ex.Message);
                                                }
                                            });
    }
}

/// <summary>
///     Plays what the buttons ask for, on the window's thread. Effects sit on top; a music bed plays behind everything and is turned down while effects play over it.
/// </summary>
internal sealed class SoundEngine : IDisposable
{
    private sealed class Voice
    {
        public required string      ButtonId;
        public required SoundRole   Role;
        public required double      Level;
        public required MediaPlayer Player;
        public          double      Fade = 1;
    }

    private readonly List<Voice> _voices = [];
    private readonly DispatcherTimer _fader;
    private          double          _master = 1;
    private          double          _duck   = 0.35;
    private          double          _fadeStep;

    public SoundEngine()
    {
        _fader      = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _fader.Tick += (_, _) => FadeTick();
    }

    public bool Muted { get; private set; }

    public double Master => _master;

    private bool _warmed;

    /// <summary>
    ///     Loads the media stack with a silent clip. The first player in a process takes a few hundred milliseconds to open anything, long enough for a short press to be over before the sound starts.
    /// </summary>
    public void Warm()
    {
        if (_warmed) return;

        _warmed = true;
        try
        {
            string file   = SoundFiles.For(ToneSynth.Wav(new short[ToneSynth.Rate / 20]), ".wav");
            var    player = new MediaPlayer { Volume = 0 };
            void Done()
            {
                player.Close();
            }

            player.MediaOpened += (_, _) => player.Play();
            player.MediaEnded  += (_, _) => Done();
            player.MediaFailed += (_, _) => Done();
            player.Open(new Uri(file));
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Warming up the sound engine failed");
        }
    }

    /// <summary>Something started or stopped: the buttons show what is playing.</summary>
    public event Action? Changed;

    /// <summary>Something could not be played.</summary>
    public event Action<string>? Problem;

    public IReadOnlyCollection<string> Active => _voices.Select(v => v.ButtonId).Distinct().ToList();

    public bool IsPlaying(string buttonId) => _voices.Any(v => v.ButtonId == buttonId);

    public int VoiceCount => _voices.Count;

    public void SetMaster(double fraction)
    {
        _master = Math.Clamp(fraction, 0, 1);
        Apply();
    }

    public void SetDuck(double fraction)
    {
        _duck = Math.Clamp(fraction, 0, 1);
        Apply();
    }

    public void SetMuted(bool muted)
    {
        Muted = muted;
        Apply();
        Changed?.Invoke();
    }

    public void ToggleMute() => SetMuted(!Muted);

    /// <summary>Plays the file for the button, as the button says (a bed replaces the bed that was playing, a monophonic sound cuts itself off).</summary>
    public void Play(SoundButton button, string file)
    {
        if (button.Role == SoundRole.MusicBed) Stop(v => v.Role == SoundRole.MusicBed);
        if (button.Mode == PlayMode.Monophonic) Stop(v => v.ButtonId == button.Id);

        var  player = new MediaPlayer();
        var  voice  = new Voice { ButtonId = button.Id, Role = button.Role, Level = Math.Clamp(button.Volume, 0, 100) / 100.0, Player = player };
        bool loop   = button.Loop || button.HoldsTone;

        player.MediaEnded += (_, _) =>
                             {
                                 if (loop && _voices.Contains(voice))
                                 {
                                     player.Position = TimeSpan.Zero;
                                     player.Play();
                                 }
                                 else
                                 {
                                     Remove(voice);
                                 }
                             };
        player.MediaFailed += (_, e) =>
                              {
                                  Remove(voice);
                                  Problem?.Invoke($"{button.Label}: {e.ErrorException?.Message ?? "the sound could not be played"}");
                              };

        _voices.Add(voice);
        try
        {
            player.Open(new Uri(file));
            player.Volume = Effective(voice);
            player.Play();
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            Remove(voice);
            Problem?.Invoke($"{button.Label}: {ex.Message}");
            return;
        }

        Apply();
        Changed?.Invoke();
    }

    public void StopButton(string buttonId) => Stop(v => v.ButtonId == buttonId);

    public void StopAll() => Stop(_ => true);

    public void StopEffects() => Stop(v => v.Role == SoundRole.Effect);

    public void StopMusic() => Stop(v => v.Role == SoundRole.MusicBed);

    /// <summary>Turns the music bed down to nothing over a couple of seconds, then stops it.</summary>
    public void FadeMusic(int milliseconds = 2000)
    {
        if (!_voices.Any(v => v.Role == SoundRole.MusicBed)) return;

        _fadeStep = 50.0 / Math.Max(100, milliseconds);
        _fader.Start();
    }

    private void FadeTick()
    {
        var beds = _voices.Where(v => v.Role == SoundRole.MusicBed).ToList();
        foreach (var v in beds) v.Fade -= _fadeStep;
        foreach (var v in beds.Where(v => v.Fade <= 0)) Remove(v);
        if (beds.All(v => v.Fade <= 0)) _fader.Stop();
        Apply();
    }

    private void Stop(Func<Voice, bool> which)
    {
        var gone = _voices.Where(which).ToList();
        if (gone.Count == 0) return;

        foreach (var v in gone) Close(v);
        _voices.RemoveAll(v => gone.Contains(v));
        Apply();
        Changed?.Invoke();
    }

    private void Remove(Voice voice)
    {
        if (!_voices.Remove(voice)) return;

        Close(voice);
        Apply();
        Changed?.Invoke();
    }

    private static void Close(Voice voice)
    {
        try
        {
            voice.Player.Stop();
            voice.Player.Close();
        }
        catch (InvalidOperationException)
        {
            // already closed
        }
    }

    private double Effective(Voice v)
    {
        double ducked = v.Role == SoundRole.MusicBed && _voices.Any(o => o.Role == SoundRole.Effect) ? _duck : 1;
        return Muted ? 0 : Math.Clamp(_master * v.Level * ducked * v.Fade, 0, 1);
    }

    private void Apply()
    {
        foreach (var v in _voices) v.Player.Volume = Effective(v);
    }

    public void Dispose()
    {
        _fader.Stop();
        foreach (var v in _voices) Close(v);
        _voices.Clear();
    }
}
