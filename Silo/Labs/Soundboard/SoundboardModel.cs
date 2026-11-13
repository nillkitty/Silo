using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Silo.Labs.Soundboard;

/// <summary>What a button does when it is pressed.</summary>
public enum ButtonAction
{
    /// <summary>Plays a sound: one imported into the Silo, or a file on this machine.</summary>
    Sound,

    /// <summary>Plays a single tone.</summary>
    Tone,

    /// <summary>Plays two tones at once (or a run of keypad digits as DTMF or MF).</summary>
    DualTone,

    /// <summary>Speaks a text.</summary>
    Speech,

    /// <summary>Does something to the board: mute all, stop all...</summary>
    Command
}

/// <summary>What happens when a sound button is pressed again while it is still playing.</summary>
public enum PlayMode
{
    /// <summary>The sound cuts itself off and starts over.</summary>
    Monophonic,

    /// <summary>Another copy of the sound plays alongside.</summary>
    Stackable
}

/// <summary>Where a sound is played: effects on top, a music bed behind everything.</summary>
public enum SoundRole
{
    Effect,
    MusicBed
}

/// <summary>The keypad standard a run of digits is played in.</summary>
public enum DualStandard
{
    /// <summary>The two frequencies the button says.</summary>
    Custom,

    /// <summary>Touch-tone dialing: 0-9, * # and A-D.</summary>
    Dtmf,

    /// <summary>R1 multi-frequency (the trunk signalling): 0-9, with * as KP and # as ST.</summary>
    Mf
}

/// <summary>What a command button does to the board.</summary>
public enum BoardCommand
{
    MuteAll,
    StopAll,
    StopEffects,
    StopMusic,
    FadeMusic,
    MasterUp,
    MasterDown
}

public enum ButtonShape
{
    Rectangle,
    Rounded,
    Pill,
    Ellipse,
    Diamond,
    Hexagon
}

public enum ButtonStyle
{
    Flat,
    Gradient,
    Glossy,
    Outline,
    Neon
}

/// <summary>One button on a board: where it is, how it looks, and what it does.</summary>
public sealed class SoundButton
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Label { get; set; } = "Button";

    /// <summary>A symbol or emoji drawn above the label.</summary>
    public string Icon { get; set; } = string.Empty;

    // ---- place and look ----------------------------------------------------------------------------

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 120;

    public double Height { get; set; } = 90;

    public ButtonShape Shape { get; set; } = ButtonShape.Rounded;

    public ButtonStyle Style { get; set; } = ButtonStyle.Glossy;

    public string Fill { get; set; } = "#3B82D6";

    public string TextColor { get; set; } = "#FFFFFF";

    public string BorderColor { get; set; } = "#FFFFFF";

    public double BorderWidth { get; set; } = 2;

    public double CornerRadius { get; set; } = 14;

    public double FontSize { get; set; } = 14;

    public double Opacity { get; set; } = 1;

    /// <summary>Degrees the button is turned.</summary>
    public double Tilt { get; set; }

    /// <summary>The key that presses the button (a WPF key name: A, D1, F5, Space...).</summary>
    public string Hotkey { get; set; } = string.Empty;

    // ---- what it does ------------------------------------------------------------------------------

    public ButtonAction Action { get; set; } = ButtonAction.Sound;

    /// <summary>For a sound: <c>silo:name</c> for a sound imported into the Silo, otherwise the path of a file.</summary>
    public string Source { get; set; } = string.Empty;

    public double ToneHz { get; set; } = 440;

    public double ToneHz2 { get; set; } = 480;

    /// <summary>How long a tone lasts, in milliseconds; 0 for as long as the button is held.</summary>
    public int ToneMs { get; set; } = 500;

    public DualStandard Standard { get; set; } = DualStandard.Custom;

    /// <summary>The digits played in the <see cref="Standard" /> when it is not Custom.</summary>
    public string Digits { get; set; } = "5551212";

    public string Speech { get; set; } = "Hello";

    /// <summary>The name of the voice; empty for the default one.</summary>
    public string Voice { get; set; } = string.Empty;

    /// <summary>-10 (slow) to 10 (fast).</summary>
    public int SpeechRate { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public BoardCommand Command { get; set; } = BoardCommand.StopAll;

    /// <summary>What a second press does while it plays; Stackable plays copies together.</summary>
    public PlayMode Mode { get; set; } = PlayMode.Monophonic;

    /// <summary>The sound stops when the button is released.</summary>
    public bool Momentary { get; set; }

    /// <summary>The sound starts over when it ends. A looping button (not momentary) is pressed again to stop it.</summary>
    public bool Loop { get; set; }

    public SoundRole Role { get; set; } = SoundRole.Effect;

    /// <summary>0 to 100.</summary>
    public int Volume { get; set; } = 100;

    /// <summary>A button that is pressed again to stop what it started: a music bed or a loop that is not momentary.</summary>
    [JsonIgnore]
    public bool Latches => !IsMomentary && PlaysSound && (Role == SoundRole.MusicBed || Loop);

    /// <summary>A tone with no length lasts as long as the button is held.</summary>
    [JsonIgnore]
    public bool HoldsTone => ToneMs <= 0 && (Action == ButtonAction.Tone || (Action == ButtonAction.DualTone && Standard == DualStandard.Custom));

    [JsonIgnore]
    public bool IsMomentary => Momentary || HoldsTone;

    [JsonIgnore]
    public bool PlaysSound => Action != ButtonAction.Command;

    public SoundButton Clone() => (SoundButton)MemberwiseClone();

    /// <summary>A one-line account of what the button does.</summary>
    public string Describe()
    {
        return Action switch
               {
                   ButtonAction.Sound    => Source.Length == 0 ? "no sound chosen" : Source.StartsWith(SoundboardSounds.SiloPrefix, StringComparison.Ordinal) ? "Silo sound " + Source[SoundboardSounds.SiloPrefix.Length..] : Path.GetFileName(Source),
                   ButtonAction.Tone     => $"{ToneHz:0.##} Hz" + (ToneMs > 0 ? $" for {ToneMs} ms" : " while held"),
                   ButtonAction.DualTone => Standard == DualStandard.Custom ? $"{ToneHz:0.##} + {ToneHz2:0.##} Hz" : $"{Standard.ToString().ToUpperInvariant()} {Digits}",
                   ButtonAction.Speech   => "says: " + LabUi.Shorten(Speech, 30),
                   _                     => Command.ToString()
               };
    }
}

/// <summary>A soundboard: the buttons and how the board is drawn. This is what the Silo keeps.</summary>
public sealed class Board
{
    public const int MinSize = 200;

    public const int MaxSize = 4000;

    public string Name { get; set; } = string.Empty;

    public int Width { get; set; } = 1000;

    public int Height { get; set; } = 620;

    public string Background { get; set; } = "#1B1F2A";

    /// <summary>The bottom of the background gradient; the same as Background for a flat one.</summary>
    public string Background2 { get; set; } = "#0E1118";

    /// <summary>A picture file laid over the background; empty for none.</summary>
    public string BackgroundImage { get; set; } = string.Empty;

    /// <summary>0 to 100.</summary>
    public int ImageOpacity { get; set; } = 35;

    public int GridSize { get; set; } = 20;

    public bool Snap { get; set; } = true;

    /// <summary>0 to 100.</summary>
    public int MasterVolume { get; set; } = 100;

    /// <summary>How loud the music bed is, in percent, while effects are playing over it.</summary>
    public int DuckPercent { get; set; } = 35;

    public List<SoundButton> Buttons { get; set; } = [];

    public Board Clone() => JsonConvert.DeserializeObject<Board>(JsonConvert.SerializeObject(this)) ?? new Board();

    public void Normalize()
    {
        Width        = Math.Clamp(Width, MinSize, MaxSize);
        Height       = Math.Clamp(Height, MinSize, MaxSize);
        GridSize     = Math.Clamp(GridSize, 0, 200);
        MasterVolume = Math.Clamp(MasterVolume, 0, 100);
        DuckPercent  = Math.Clamp(DuckPercent, 0, 100);
        ImageOpacity = Math.Clamp(ImageOpacity, 0, 100);
        Buttons ??= [];
        foreach (var b in Buttons)
        {
            b.Width  = Math.Clamp(b.Width, 24, 1000);
            b.Height = Math.Clamp(b.Height, 24, 1000);
            b.Volume = Math.Clamp(b.Volume, 0, 100);
            b.Opacity = Math.Clamp(b.Opacity, 0.1, 1);
            if (string.IsNullOrEmpty(b.Id)) b.Id = Guid.NewGuid().ToString("N")[..8];
        }
    }

    /// <summary>Buttons with ids that clash get new ones (a pasted or edited board).</summary>
    public void FixIds()
    {
        var seen = new HashSet<string>();
        foreach (var b in Buttons)
            while (!seen.Add(b.Id))
                b.Id = Guid.NewGuid().ToString("N")[..8];
    }

    public SoundButton? Find(string? id) => id is null ? null : Buttons.FirstOrDefault(b => b.Id == id);

    public static Board Starter()
    {
        var board = new Board { Name = string.Empty };
        void Add(string label, string icon, string fill, double x, double y, Action<SoundButton> set)
        {
            var b = new SoundButton { Label = label, Icon = icon, Fill = fill, X = x, Y = y };
            set(b);
            board.Buttons.Add(b);
        }

        Add("Dial tone", "☎", "#3FA95B", 40, 40, b => { b.Action = ButtonAction.DualTone; b.ToneHz = 350; b.ToneHz2 = 440; b.ToneMs = 0; b.Shape = ButtonShape.Ellipse; b.Width = 110; b.Height = 110; });
        Add("Dial 555-1212", "⌗", "#D9A41E", 180, 40, b => { b.Action = ButtonAction.DualTone; b.Standard = DualStandard.Dtmf; b.Digits = "5551212"; b.ToneMs = 120; b.Mode = PlayMode.Monophonic; });
        Add("Say hello", "💬", "#B05DD6", 320, 40, b => { b.Action = ButtonAction.Speech; b.Speech = "Hello, and welcome to the show."; b.Shape = ButtonShape.Pill; });
        Add("Beep", "🔔", "#E5484D", 460, 40, b => { b.Action = ButtonAction.Tone; b.ToneHz = 1000; b.ToneMs = 250; b.Mode = PlayMode.Stackable; b.Shape = ButtonShape.Diamond; b.Width = 110; b.Height = 110; });
        Add("Hold to tone", "〰", "#2DB8C4", 600, 40, b => { b.Action = ButtonAction.Tone; b.ToneHz = 600; b.ToneMs = 0; b.Style = ButtonStyle.Neon; b.Shape = ButtonShape.Hexagon; });
        Add("Mute all", "🔇", "#555B66", 40, 480, b => { b.Action = ButtonAction.Command; b.Command = BoardCommand.MuteAll; });
        Add("Stop all", "⏹", "#C24E00", 180, 480, b => { b.Action = ButtonAction.Command; b.Command = BoardCommand.StopAll; });
        Add("Fade music", "🎚", "#3B82D6", 320, 480, b => { b.Action = ButtonAction.Command; b.Command = BoardCommand.FadeMusic; });
        return board;
    }
}

/// <summary>A sound imported into the Silo: the file, kept as it was.</summary>
public sealed class SoundClip
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The extension of the file it came from, with its dot (.mp3).</summary>
    public string Extension { get; set; } = ".wav";

    public byte[] Data { get; set; } = [];
}

/// <summary>The sounds kept in the Silo and the files a board can play.</summary>
public static class SoundboardSounds
{
    public const string SiloPrefix = "silo:";

    public static readonly string[] Extensions = [".wav", ".mp3", ".wma", ".mid", ".midi", ".m4a", ".aac", ".flac", ".ogg", ".aif", ".aiff", ".rmi"];

    /// <summary>The sounds that are music cues rather than effects, going by the file.</summary>
    public static readonly string[] MusicExtensions = [".mp3", ".wma", ".mid", ".midi", ".m4a", ".aac", ".flac", ".ogg", ".rmi"];

    internal static readonly LabSaved<SoundClip> Clips = new("soundboard-sound");

    public static bool IsSound(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsSilo(string source) => source.StartsWith(SiloPrefix, StringComparison.OrdinalIgnoreCase);

    public static string SiloName(string source) => source[SiloPrefix.Length..];

    public static string Filter => "Sounds|" + string.Join(";", Extensions.Select(e => "*" + e)) + "|All files|*.*";

    /// <summary>A button for a sound file; music goes behind everything.</summary>
    public static SoundButton ButtonFor(string source, string label, double x, double y)
    {
        string ext = IsSilo(source) ? string.Empty : Path.GetExtension(source);
        bool music = MusicExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        return new SoundButton
               {
                   Label  = label,
                   Icon   = music ? "♪" : "🔊",
                   Source = source,
                   X      = x,
                   Y      = y,
                   Fill   = music ? "#7A329E" : "#3B82D6",
                   Role   = music ? SoundRole.MusicBed : SoundRole.Effect
               };
    }
}
