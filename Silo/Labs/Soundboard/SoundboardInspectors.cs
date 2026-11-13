using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Silo.Labs.Soundboard;

/// <summary>Boxes for the properties of one thing (a button, the board) that put what is typed into it and show again what it says when it changes from elsewhere.</summary>
internal sealed class PaneForm<T> where T : class
{
    private readonly Func<T?>           _target;
    private readonly Action<Action<T>>  _edit;
    private readonly List<Action>       _pulls = [];
    private          bool               _loading;

    public PaneForm(Func<T?> target, Action<Action<T>> edit)
    {
        _target = target;
        _edit   = edit;
    }

    private string Read(Func<T, string> read) => _target() is { } t ? read(t) : string.Empty;

    private static void Put(TextBox box, string text)
    {
        if (!box.IsKeyboardFocusWithin && box.Text != text) box.Text = text;
    }

    public TextBox Text(Func<T, string> read, Action<T, string> write, string? tip = null, double width = 150, bool mono = false)
    {
        var box = LabUi.Field(Read(read), v =>
                                          {
                                              if (!_loading) _edit(t => write(t, v));
                                          }, width, tip, mono);
        _pulls.Add(() => Put(box, Read(read)));
        return box;
    }

    public TextBox Number(Func<T, double> read, Action<T, double> write, double min, double max, string? tip = null, double width = 70)
    {
        string Show(T t) => read(t).ToString("0.##", CultureInfo.InvariantCulture);
        var box = LabUi.Field(Read(Show), v =>
                                          {
                                              if (_loading || !double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return;

                                              _edit(t => write(t, Math.Clamp(n, min, max)));
                                          }, width, tip);
        _pulls.Add(() => Put(box, Read(Show)));
        return box;
    }

    public ComboBox Choice<TEnum>(Func<T, TEnum> read, Action<T, TEnum> write, string? tip = null, double width = 130) where TEnum : struct, Enum
    {
        var combo = LabUi.Combo(Enum.GetNames<TEnum>(), _target() is { } t ? read(t).ToString() : null, v =>
                                                                                                          {
                                                                                                              if (!_loading && Enum.TryParse<TEnum>(v, out var e)) _edit(x => write(x, e));
                                                                                                          }, width);
        combo.ToolTip = tip;
        _pulls.Add(() => combo.SelectedItem = _target() is { } x ? read(x).ToString() : null);
        return combo;
    }

    public ComboBox Pick(IEnumerable<string> items, Func<T, string> read, Action<T, string> write, string? tip = null, double width = 150)
    {
        var combo = LabUi.Combo(items, Read(read), v =>
                                                   {
                                                       if (!_loading) _edit(t => write(t, v));
                                                   }, width);
        combo.ToolTip = tip;
        _pulls.Add(() => combo.SelectedItem = Read(read));
        return combo;
    }

    public CheckBox Check(string text, Func<T, bool> read, Action<T, bool> write, string? tip = null)
    {
        var box = LabUi.Check(text, _target() is { } t && read(t), on =>
                                                                    {
                                                                        if (!_loading) _edit(x => write(x, on));
                                                                    }, tip);
        _pulls.Add(() => box.IsChecked = _target() is { } x && read(x));
        return box;
    }

    /// <summary>A box for a color (a name or #RRGGBB) with swatches to click.</summary>
    public UIElement Shade(Func<T, string> read, Action<T, string> write, string? tip = null)
    {
        var box = LabUi.Field(Read(read), v =>
                                          {
                                              if (!_loading && BoardColors.IsColor(v)) _edit(t => write(t, v.Trim()));
                                          }, 80, tip ?? "A color: #RRGGBB or a name", true);
        _pulls.Add(() => Put(box, Read(read)));

        var swatches = new WrapPanel { Margin = new Thickness(2, 3, 0, 0), MaxWidth = 230 };
        foreach (string hex in BoardColors.Palette)
        {
            string picked = hex;
            var chip = new Border
                       {
                           Width = 17, Height = 17, Margin = new Thickness(1), Background = BoardColors.Frozen(BoardColors.Parse(hex, Colors.Gray)), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = hex,
                           BorderBrush = BoardColors.Frozen(Color.FromRgb(0x77, 0x7C, 0x86))
                       };
            chip.MouseLeftButtonUp += (_, _) =>
                                      {
                                          _edit(t => write(t, picked));
                                          Put(box, picked);
                                          box.Text = picked;
                                      };
            swatches.Children.Add(chip);
        }

        var panel = new StackPanel();
        panel.Children.Add(box);
        panel.Children.Add(swatches);
        box.HorizontalAlignment = HorizontalAlignment.Left;
        return panel;
    }

    /// <summary>Shows what the properties say now.</summary>
    public void Pull()
    {
        _loading = true;
        try
        {
            foreach (var pull in _pulls) pull();
        }
        finally
        {
            _loading = false;
        }
    }
}

internal static class PaneLayout
{
    public static UIElement Section(string title, IEnumerable<(string, UIElement)> rows, double titleWidth = 100)
    {
        var panel = new StackPanel();
        panel.Children.Add(LabUi.Heading(title));
        panel.Children.Add(LabUi.Form(rows, titleWidth));
        return panel;
    }

    public static UIElement Wrap(IEnumerable<UIElement> parts)
    {
        var panel = new StackPanel();
        foreach (var p in parts) panel.Children.Add(p);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    public static UIElement Note(string text) => LabUi.Label(text, muted: true, wrap: true).Also(l => l.Margin = new Thickness(8, 0, 8, 6));
}

/// <summary>The selected button: what it does, how it plays, how it looks and where it is.</summary>
internal sealed class SoundboardButtonInspector(SoundboardLabSession session) : LiveInspector(session)
{
    private ContentControl?        _host;
    private PaneForm<SoundButton>? _form;
    private string?                _key;
    private IReadOnlyList<string>? _voices;

    public override string Id => "button";

    public override string Title => "Button";

    public override string IconKey => "Sound";

    public override LabPaneKind Kind => LabPaneKind.Entity;

    protected override UIElement Create()
    {
        _host = new ContentControl();
        return _host;
    }

    protected override void Refresh()
    {
        if (_host is null) return;

        var    b   = session.Selected;
        string key = b is null ? string.Empty : $"{b.Id}|{b.Action}|{b.Standard}|{b.Shape}|{b.Role}|{b.Loop}|{b.Momentary}";
        if (key != _key)
        {
            _key        = key;
            _host.Content = b is null ? Empty() : Build(b);
        }

        _form?.Pull();
    }

    private static UIElement Empty() => PaneLayout.Wrap([PaneLayout.Note("Select a button on the board (while editing) to set what it does and how it looks. Double-click an empty place on the board to add one.")]);

    private UIElement Build(SoundButton b)
    {
        string id = b.Id;
        var    f  = new PaneForm<SoundButton>(() => session.Board.Find(id), edit => session.EditButton(id, edit));
        _form = f;
        var parts = new List<UIElement>();

        // ---- the button -------------------------------------------------------------------------------
        parts.Add(PaneLayout.Section("Button",
                                     [
                                         ("Label", f.Text(x => x.Label, (x, v) => x.Label = v)),
                                         ("Symbol", f.Text(x => x.Icon, (x, v) => x.Icon = v, "A symbol or emoji drawn above the label", 60)),
                                         ("Does", f.Choice(x => x.Action, (x, v) => x.Action = v, "What pressing the button does"))
                                     ]));

        // ---- what it does ---------------------------------------------------------------------------
        var does = new List<(string, UIElement)>();
        switch (b.Action)
        {
            case ButtonAction.Sound:
                does.Add(("Sound", f.Text(x => x.Source, (x, v) => x.Source = v.Trim().Trim('"'), "silo:name for a sound kept in the Silo, or the path of a file on this machine", 230, true)));
                does.Add(("", SoundPicker(id)));
                break;
            case ButtonAction.Tone:
                does.Add(("Frequency (Hz)", f.Number(x => x.ToneHz, (x, v) => x.ToneHz = v, 20, 20000)));
                does.Add(("Length (ms)", f.Number(x => x.ToneMs, (x, v) => x.ToneMs = (int)v, 0, 60000, "0 plays the tone for as long as the button is held")));
                break;
            case ButtonAction.DualTone:
                does.Add(("Standard", f.Choice(x => x.Standard, (x, v) => x.Standard = v, "Custom plays the two frequencies; DTMF and MF play keypad digits")));
                if (b.Standard == DualStandard.Custom)
                {
                    does.Add(("Tone 1 (Hz)", f.Number(x => x.ToneHz, (x, v) => x.ToneHz = v, 20, 20000)));
                    does.Add(("Tone 2 (Hz)", f.Number(x => x.ToneHz2, (x, v) => x.ToneHz2 = v, 20, 20000)));
                    does.Add(("Length (ms)", f.Number(x => x.ToneMs, (x, v) => x.ToneMs = (int)v, 0, 60000, "0 plays the tones for as long as the button is held")));
                }
                else
                {
                    does.Add(("Digits", f.Text(x => x.Digits, (x, v) => x.Digits = v, b.Standard == DualStandard.Dtmf ? "0-9 * # A-D; a comma pauses" : "0-9, * (KP) and # (ST); a comma pauses", 190, true)));
                    does.Add(("Tone (ms)", f.Number(x => x.ToneMs, (x, v) => x.ToneMs = (int)v, 20, 5000, "How long each digit sounds")));
                }

                break;
            case ButtonAction.Speech:
                var say = f.Text(x => x.Speech, (x, v) => x.Speech = v, "What the button says", 230);
                does.Add(("Says", say));
                _voices ??= SoundboardSpeech.Voices();
                does.Add(("Voice", f.Pick(_voices.Prepend("(default)"), x => x.Voice.Length == 0 ? "(default)" : x.Voice, (x, v) => x.Voice = v == "(default)" ? string.Empty : v, "A voice Windows has", 190)));
                does.Add(("Speed", f.Number(x => x.SpeechRate, (x, v) => x.SpeechRate = (int)v, -10, 10, "-10 (slow) to 10 (fast)")));
                break;
            case ButtonAction.Command:
                does.Add(("Command", f.Choice(x => x.Command, (x, v) => x.Command = v, "What the button does to the board")));
                break;
        }

        parts.Add(PaneLayout.Section("What it does", does));

        // ---- how it plays ---------------------------------------------------------------------------
        if (b.PlaysSound)
        {
            var plays = new List<(string, UIElement)>
                        {
                            ("Role", f.Choice(x => x.Role, (x, v) => x.Role = v, "Effects play on top; a music bed plays behind everything and is turned down while effects play")),
                            ("Again", f.Choice(x => x.Mode, (x, v) => x.Mode = v, "Monophonic: pressing again cuts the sound off and starts it over. Stackable: another copy plays alongside.")),
                            ("", f.Check("Momentary (stops when released)", x => x.Momentary, (x, v) => x.Momentary = v, "The sound plays only while the button is held")),
                            ("", f.Check("Loop", x => x.Loop, (x, v) => x.Loop = v, "The sound starts over when it ends")),
                            ("Volume (%)", f.Number(x => x.Volume, (x, v) => x.Volume = (int)v, 0, 100))
                        };
            parts.Add(PaneLayout.Section("How it plays", plays));
            parts.Add(PaneLayout.Note(b.IsMomentary ? "It plays while the button is held." : b.Latches ? "Press it to start, press it again to stop. It also stops when it ends." : b.Mode == PlayMode.Stackable ? "Each press adds a copy that plays to the end." : "Each press cuts the sound off and starts it over."));
        }

        // ---- how it looks ---------------------------------------------------------------------------
        var looks = new List<(string, UIElement)>
                    {
                        ("Shape", f.Choice(x => x.Shape, (x, v) => x.Shape = v)),
                        ("Style", f.Choice(x => x.Style, (x, v) => x.Style = v)),
                        ("Color", f.Shade(x => x.Fill, (x, v) => x.Fill = v)),
                        ("Text color", f.Shade(x => x.TextColor, (x, v) => x.TextColor = v)),
                        ("Border color", f.Shade(x => x.BorderColor, (x, v) => x.BorderColor = v)),
                        ("Border", f.Number(x => x.BorderWidth, (x, v) => x.BorderWidth = v, 0, 20)),
                        ("Text size", f.Number(x => x.FontSize, (x, v) => x.FontSize = v, 6, 96)),
                        ("Opacity (%)", f.Number(x => x.Opacity * 100, (x, v) => x.Opacity = v / 100, 10, 100)),
                        ("Tilt (deg)", f.Number(x => x.Tilt, (x, v) => x.Tilt = v, -180, 180))
                    };
        if (b.Shape == ButtonShape.Rounded) looks.Insert(2, ("Corners", f.Number(x => x.CornerRadius, (x, v) => x.CornerRadius = v, 0, 200)));
        parts.Add(PaneLayout.Section("How it looks", looks));

        // ---- where it is ----------------------------------------------------------------------------
        parts.Add(PaneLayout.Section("Where it is",
                                     [
                                         ("Left", f.Number(x => x.X, (x, v) => x.X = v, 0, Board.MaxSize)),
                                         ("Top", f.Number(x => x.Y, (x, v) => x.Y = v, 0, Board.MaxSize)),
                                         ("Width", f.Number(x => x.Width, (x, v) => x.Width = v, 24, 1000)),
                                         ("Height", f.Number(x => x.Height, (x, v) => x.Height = v, 24, 1000)),
                                         ("Key", HotkeyBox(f))
                                     ]));

        parts.Add(LabUi.Bar(LabUi.Button("Test", () => { if (session.Selected is { } t) session.Test(t); }, "Press the button", icon: "Play"),
                            LabUi.Button("Duplicate", session.DuplicateSelected, "Copy the button", icon: "TbCopy"),
                            LabUi.Button("Delete", session.DeleteSelected, "Remove the button from the board", icon: "TbDelete")));
        return PaneLayout.Wrap(parts);
    }

    private UIElement SoundPicker(string id)
    {
        var names = session.SoundNames();
        var panel = new WrapPanel();
        if (names.Count > 0)
        {
            var combo = new ComboBox { MinWidth = 130, Margin = new Thickness(2), ToolTip = "A sound kept in the Silo" };
            combo.Items.Add("Silo sound...");
            foreach (string n in names) combo.Items.Add(n);
            combo.SelectedIndex = 0;
            combo.SelectionChanged += (_, _) =>
                                      {
                                          if (combo.SelectedIndex <= 0 || combo.SelectedItem is not string name) return;

                                          session.EditButton(id, b => b.Source = SoundboardSounds.SiloPrefix + name);
                                          combo.SelectedIndex = 0;
                                      };
            panel.Children.Add(combo);
        }

        panel.Children.Add(LabUi.Button("File...", () => session.BrowseSound(id), "Choose a sound file on this machine", icon: "TbOpen"));
        panel.Children.Add(LabUi.Button("Preview", () => { if (session.Board.Find(id) is { } b) session.Preview(b.Source); }, "Hear the sound", icon: "Play"));
        return panel;
    }

    private UIElement HotkeyBox(PaneForm<SoundButton> f)
    {
        string id  = session.Selected?.Id ?? string.Empty;
        var    box = new TextBox { IsReadOnly = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Click, then press the key that should press the button (Delete clears it)", Text = session.Selected?.Hotkey ?? string.Empty };
        box.PreviewKeyDown += (_, e) =>
                              {
                                  var key = e.Key == Key.System ? e.SystemKey : e.Key;
                                  if (key is Key.Tab or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;

                                  string value = key is Key.Delete or Key.Back or Key.Escape ? string.Empty : key.ToString();
                                  session.EditButton(id, b => b.Hotkey = value);
                                  box.Text  = value;
                                  e.Handled = true;
                              };
        return box;
    }
}

/// <summary>The board: its size, background, grid and volumes.</summary>
internal sealed class SoundboardBoardInspector(SoundboardLabSession session) : LiveInspector(session)
{
    private PaneForm<Board>? _form;

    public override string Id => "board";

    public override string Title => "Board";

    public override string IconKey => "Settings";

    public override LabPaneKind Kind => LabPaneKind.Entity;

    protected override UIElement Create()
    {
        var f = new PaneForm<Board>(() => session.Board, edit => session.EditBoard(edit));
        _form = f;
        var parts = new List<UIElement>
                    {
                        PaneLayout.Section("Board",
                                           [
                                               ("Name", f.Text(b => b.Name, (b, v) => b.Name = v.Trim(), "What the board is called in the Silo")),
                                               ("Width", f.Number(b => b.Width, (b, v) => b.Width = (int)v, Board.MinSize, Board.MaxSize)),
                                               ("Height", f.Number(b => b.Height, (b, v) => b.Height = (int)v, Board.MinSize, Board.MaxSize)),
                                               ("Grid", f.Number(b => b.GridSize, (b, v) => b.GridSize = (int)v, 0, 200, "Spacing of the grid buttons snap to; 0 for none")),
                                               ("", f.Check("Snap buttons to the grid", b => b.Snap, (b, v) => b.Snap = v))
                                           ]),
                        PaneLayout.Section("Look",
                                           [
                                               ("Top color", f.Shade(b => b.Background, (b, v) => b.Background = v)),
                                               ("Bottom color", f.Shade(b => b.Background2, (b, v) => b.Background2 = v)),
                                               ("Picture", Picture(f)),
                                               ("Picture (%)", f.Number(b => b.ImageOpacity, (b, v) => b.ImageOpacity = (int)v, 0, 100, "How strong the background picture is"))
                                           ]),
                        PaneLayout.Section("Sound",
                                           [
                                               ("Master (%)", f.Number(b => b.MasterVolume, (b, v) => b.MasterVolume = (int)v, 0, 100)),
                                               ("Music under effects (%)", f.Number(b => b.DuckPercent, (b, v) => b.DuckPercent = (int)v, 0, 100, "How loud a music bed is while effects play over it"))
                                           ], 140)
                    };
        return PaneLayout.Wrap(parts);
    }

    private UIElement Picture(PaneForm<Board> f)
    {
        var panel = new WrapPanel();
        panel.Children.Add(f.Text(b => b.BackgroundImage, (b, v) => b.BackgroundImage = v.Trim().Trim('"'), "The path of a picture laid over the background", 160, true));
        panel.Children.Add(LabUi.Button("Browse...", () =>
                                                     {
                                                         var dialog = new OpenFileDialog { Title = "Choose a background picture", Filter = Silo.Providers.Format.Formats.OpenFilter<ImageSource>("Pictures") };
                                                         if (dialog.ShowDialog() == true) session.EditBoard(b => b.BackgroundImage = dialog.FileName);
                                                     }, "Choose a picture file", icon: "TbOpen"));
        return panel;
    }

    protected override void Refresh() => _form?.Pull();
}

/// <summary>The sounds kept in the Silo, and the way to add more.</summary>
internal sealed class SoundboardSoundsInspector(SoundboardLabSession session) : LiveInspector(session)
{
    private ListBox?   _list;
    private TextBlock? _note;
    private string     _shown = string.Empty;

    public override string Id => "sounds";

    public override string Title => "Sounds";

    public override string IconKey => "Play";

    public override LabPaneKind Kind => LabPaneKind.Entity;

    protected override UIElement Create()
    {
        _list = LabUi.List();
        _note = LabUi.Label(string.Empty, muted: true, wrap: true);
        _note.Margin = new Thickness(8, 4, 8, 4);
        _list.MouseDoubleClick += (_, _) => AddSelected();

        var bar = LabUi.Bar(LabUi.Button("Import...", session.ImportDialog, "Keep sound files in the Silo so the board can play them anywhere", session.SilosAvailable, "TbOpen"),
                            LabUi.Button("Add Button", AddSelected, "Put a button for the sound on the board", icon: "TbNew"),
                            LabUi.Button("Use", UseOnSelected, "Make the selected button play the sound", icon: "Check"),
                            LabUi.Button("Preview", PreviewSelected, "Hear the sound", icon: "Play"),
                            LabUi.Button("Stop", session.StopPreview, "Stop the preview", icon: "Stop"),
                            LabUi.Button("Remove", RemoveSelected, "Take the sound out of the Silo", icon: "TbDelete"),
                            LabUi.Button("File...", session.AddLocal, "Put a button on the board for a sound file on this machine", icon: "TbOpen"));
        return LabUi.Layout(bar, _list, _note);
    }

    private string? Chosen => _list?.SelectedItem as string;

    private void AddSelected()
    {
        if (Chosen is { } name) session.AddSoundButton(SoundboardSounds.SiloPrefix + name);
    }

    private void UseOnSelected()
    {
        if (Chosen is not { } name) return;

        if (session.Selected is not { } b)
        {
            session.Say("Select a button on the board first.");
            return;
        }

        session.EditButton(b.Id, x =>
                                 {
                                     x.Action = ButtonAction.Sound;
                                     x.Source = SoundboardSounds.SiloPrefix + name;
                                 });
    }

    private void PreviewSelected()
    {
        if (Chosen is { } name) session.Preview(SoundboardSounds.SiloPrefix + name);
    }

    private void RemoveSelected()
    {
        if (Chosen is { } name) session.RemoveSound(name);
    }

    protected override void Refresh()
    {
        if (_list is null) return;

        var names = session.SoundNames();
        string signature = string.Join('\n', names);
        if (signature != _shown)
        {
            string? keep = Chosen;
            _shown = signature;
            _list.Items.Clear();
            foreach (string n in names) _list.Items.Add(n);
            if (keep is not null && names.Contains(keep)) _list.SelectedItem = keep;
        }

        _note!.Text = !session.SilosAvailable ? "No Silo is open, so there is nowhere to keep sounds. Sound files on this machine can still be added with File..." : names.Count == 0 ? "No sounds kept in the Silo yet. Import some, then double-click one to add a button." : "Double-click a sound to add a button for it.";
    }
}
