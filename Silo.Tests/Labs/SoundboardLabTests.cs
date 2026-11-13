using Newtonsoft.Json;
using Silo.Labs.Soundboard;

namespace Silo.Tests.Labs;

public class SoundboardLabTests
{
    private static int SampleCount(byte[] wav) => BitConverter.ToInt32(wav, 40) / 2;

    // ---- tones ------------------------------------------------------------------------------

    [Test]
    public void A_tone_is_a_wav_of_the_length_asked_for()
    {
        var wav = ToneSynth.Tone(440, 0, 500);

        Assert.That(System.Text.Encoding.ASCII.GetString(wav, 0, 4), Is.EqualTo("RIFF"));
        Assert.That(System.Text.Encoding.ASCII.GetString(wav, 8, 4), Is.EqualTo("WAVE"));
        Assert.That(SampleCount(wav), Is.EqualTo(ToneSynth.Rate / 2));
        Assert.That(BitConverter.ToInt32(wav, 4), Is.EqualTo(wav.Length - 8));
    }

    [Test]
    public void A_tone_of_no_length_is_a_second_that_loops()
    {
        Assert.That(SampleCount(ToneSynth.Tone(440.4, 0, 0)), Is.EqualTo(ToneSynth.Rate));
    }

    [Test]
    public void A_tone_does_not_click_at_its_ends()
    {
        var wav = ToneSynth.Tone(1000, 0, 100);

        Assert.That(BitConverter.ToInt16(wav, 44), Is.EqualTo(0));
        Assert.That(Math.Abs((int)BitConverter.ToInt16(wav, wav.Length - 2)), Is.LessThan(2000));
    }

    [Test]
    public void Two_tones_are_louder_than_none_and_not_louder_than_a_single_tone_peak()
    {
        var wav  = ToneSynth.Tone(350, 440, 300);
        int peak = 0;
        for (int i = 44; i < wav.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wav, i)));

        Assert.That(peak, Is.GreaterThan(5000).And.LessThan(short.MaxValue));
    }

    [TestCase('5', 770, 1336)]
    [TestCase('0', 941, 1336)]
    [TestCase('*', 941, 1209)]
    [TestCase('#', 941, 1477)]
    [TestCase('A', 697, 1633)]
    [TestCase('d', 941, 1633)]
    public void Dtmf_keys_have_the_standard_pairs(char key, double low, double high)
    {
        Assert.That(ToneSynth.TryDigit(DualStandard.Dtmf, key, out var pair), Is.True);
        Assert.That(pair, Is.EqualTo((low, high)));
    }

    [TestCase('1', 700, 900)]
    [TestCase('0', 1300, 1500)]
    [TestCase('*', 1100, 1700)]
    [TestCase('#', 1500, 1700)]
    public void Mf_keys_have_the_standard_pairs(char key, double low, double high)
    {
        Assert.That(ToneSynth.TryDigit(DualStandard.Mf, key, out var pair), Is.True);
        Assert.That(pair, Is.EqualTo((low, high)));
    }

    [Test]
    public void Mf_has_no_letters_and_custom_has_no_keys()
    {
        Assert.That(ToneSynth.TryDigit(DualStandard.Mf, 'A', out _), Is.False);
        Assert.That(ToneSynth.TryDigit(DualStandard.Custom, '5', out _), Is.False);
    }

    [Test]
    public void Digits_skip_what_cannot_be_played_and_a_comma_is_a_pause()
    {
        Assert.That(ToneSynth.Playable(DualStandard.Dtmf, "555-1212, x").Select(c => c.ToString()), Is.EqualTo(new[] { "5", "5", "5", "1", "2", "1", "2", "," }));
    }

    [Test]
    public void A_run_of_digits_is_a_tone_and_a_gap_each()
    {
        int expected = 3 * (100 + 60) * ToneSynth.Rate / 1000;

        Assert.That(SampleCount(ToneSynth.Digits(DualStandard.Dtmf, "123", 100)), Is.EqualTo(expected));
    }

    // ---- what a button does -----------------------------------------------------------------

    [Test]
    public void A_music_bed_or_a_loop_is_pressed_again_to_stop_but_not_when_momentary()
    {
        Assert.That(new SoundButton { Role = SoundRole.MusicBed }.Latches, Is.True);
        Assert.That(new SoundButton { Loop = true }.Latches, Is.True);
        Assert.That(new SoundButton { Role = SoundRole.MusicBed, Momentary = true }.Latches, Is.False);
        Assert.That(new SoundButton().Latches, Is.False);
        Assert.That(new SoundButton { Action = ButtonAction.Command, Loop = true }.Latches, Is.False);
    }

    [Test]
    public void A_tone_with_no_length_plays_while_held()
    {
        Assert.That(new SoundButton { Action = ButtonAction.Tone, ToneMs = 0 }.IsMomentary, Is.True);
        Assert.That(new SoundButton { Action = ButtonAction.DualTone, ToneMs = 0 }.IsMomentary, Is.True);
        Assert.That(new SoundButton { Action = ButtonAction.DualTone, Standard = DualStandard.Dtmf, ToneMs = 0 }.IsMomentary, Is.False);
        Assert.That(new SoundButton { Action = ButtonAction.Tone, ToneMs = 200 }.IsMomentary, Is.False);
    }

    [Test]
    public void Music_files_become_music_beds_and_other_sounds_effects()
    {
        Assert.That(SoundboardSounds.ButtonFor(@"C:\x\cue.mp3", "cue", 0, 0).Role, Is.EqualTo(SoundRole.MusicBed));
        Assert.That(SoundboardSounds.ButtonFor(@"C:\x\song.mid", "song", 0, 0).Role, Is.EqualTo(SoundRole.MusicBed));
        Assert.That(SoundboardSounds.ButtonFor(@"C:\x\honk.wav", "honk", 0, 0).Role, Is.EqualTo(SoundRole.Effect));
        Assert.That(SoundboardSounds.ButtonFor("silo:honk", "honk", 0, 0).Role, Is.EqualTo(SoundRole.Effect));
    }

    // ---- boards -----------------------------------------------------------------------------

    [Test]
    public void A_board_survives_being_kept_and_read_back()
    {
        var board = Board.Starter();
        board.Name = "Show";

        var back = JsonConvert.DeserializeObject<Board>(StoredJson(board))!;

        Assert.That(back.Name, Is.EqualTo("Show"));
        Assert.That(back.Buttons.Count, Is.EqualTo(board.Buttons.Count));
        Assert.That(back.Buttons.Select(b => (b.Label, b.Action, b.X, b.Y, b.Shape)), Is.EqualTo(board.Buttons.Select(b => (b.Label, b.Action, b.X, b.Y, b.Shape))));
        Assert.That(back.Buttons.Any(b => b.Action == ButtonAction.Command && b.Command == BoardCommand.MuteAll), Is.True);
    }

    private static string StoredJson(Board board) => Silo.Storage.StoredCodec.Serialize(board);

    [Test]
    public void A_clip_keeps_its_bytes()
    {
        var clip = new SoundClip { Name = "honk", Extension = ".mp3", Data = [1, 2, 3, 250] };

        var back = Silo.Storage.StoredCodec.Deserialize<SoundClip>(Silo.Storage.StoredCodec.Serialize(clip))!;

        Assert.That(back.Data, Is.EqualTo(clip.Data));
        Assert.That(back.Extension, Is.EqualTo(".mp3"));
    }

    [Test]
    public void Normalizing_keeps_buttons_and_the_board_within_their_limits()
    {
        var board = new Board { Width = 5, Height = 99999, MasterVolume = 400, DuckPercent = -3 };
        board.Buttons.Add(new SoundButton { Width = 1, Height = 5000, Volume = 900, Opacity = 0, Id = string.Empty });

        board.Normalize();

        Assert.That((board.Width, board.Height), Is.EqualTo((Board.MinSize, Board.MaxSize)));
        Assert.That((board.MasterVolume, board.DuckPercent), Is.EqualTo((100, 0)));
        var b = board.Buttons[0];
        Assert.That((b.Width, b.Height, b.Volume), Is.EqualTo((24d, 1000d, 100)));
        Assert.That(b.Opacity, Is.GreaterThan(0));
        Assert.That(b.Id, Is.Not.Empty);
    }

    [Test]
    public void Buttons_with_the_same_id_get_their_own()
    {
        var board = new Board();
        board.Buttons.Add(new SoundButton { Id = "a" });
        board.Buttons.Add(new SoundButton { Id = "a" });

        board.FixIds();

        Assert.That(board.Buttons.Select(b => b.Id).Distinct().Count(), Is.EqualTo(2));
    }

    [Test]
    public void Cloning_a_board_does_not_share_buttons()
    {
        var board = Board.Starter();
        var copy  = board.Clone();

        copy.Buttons[0].Label = "changed";

        Assert.That(board.Buttons[0].Label, Is.Not.EqualTo("changed"));
    }

    [Test]
    public void A_sound_file_given_to_the_Lab_becomes_a_board_with_a_button_for_it()
    {
        var lab = new SoundboardLab();

        Assert.That(lab.TryParse(@"C:\music\cue.mp3", out var board), Is.True);
        Assert.That(board!.Buttons, Has.Count.EqualTo(1));
        Assert.That(board.Buttons[0].Source, Is.EqualTo(@"C:\music\cue.mp3"));
        Assert.That(lab.TryParse("not a sound", out _), Is.False);
    }
}
