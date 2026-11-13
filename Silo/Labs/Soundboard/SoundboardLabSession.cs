using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace Silo.Labs.Soundboard;

/// <summary>One Soundboard Lab window: the board being built or performed, and the engine that plays it.</summary>
public sealed class SoundboardLabSession : LabSession<Board>, ILabLive
{
    private static readonly LabSaved<Board> Saved = new("soundboard");

    private readonly SoundEngine        _engine = new();
    private readonly HashSet<string>    _held   = [];
    private readonly List<LabInspector> _inspectors;
    private          Board              _board = new();
    private          string?            _selected;
    private          bool               _dirty;
    private          bool               _started;
    private          bool               _edit = true;

    public SoundboardLabSession(SoundboardLab lab) : base(lab)
    {
        _engine.Changed += () => Changed?.Invoke(this, EventArgs.Empty);
        _engine.Problem += text =>
                           {
                               Status = text;
                               ReportProblem(LabProblemSeverity.Warning, "Playing", text);
                           };
        _inspectors = [new SoundboardButtonInspector(this), new SoundboardBoardInspector(this), new SoundboardSoundsInspector(this)];
    }

    internal static LabSaved<Board> Kept => Saved;

    public Board Board => _board;

    internal SoundEngine Engine => _engine;

    /// <summary>Whether buttons are moved and shaped (true) or pressed (false).</summary>
    public bool EditMode => _edit;

    public SoundButton? Selected => _board.Find(_selected);

    public bool IsDirty => _dirty;

    public event EventHandler? Changed;

    /// <summary>A different board was opened: the view shows it afresh.</summary>
    public event EventHandler? Loaded;

    private SoundboardLab Lab_ => (SoundboardLab)LabOf;

    public override Board? CurrentValue => _board.Clone();

    public override string ValueText => _board.Name;

    public override string Title => $"{LabOf.Name} - {(string.IsNullOrWhiteSpace(_board.Name) ? "new board" : _board.Name)}{(_dirty ? " *" : string.Empty)}";

    public override IReadOnlyList<LabInspector> Inspectors => _inspectors;

    public override bool LibraryOpensWithWindow => true;

    protected override bool RecentOnOpen => false;

    public void Say(string text) => Status = text;

    // ---- opening ---------------------------------------------------------------------------------

    protected override Task OpenValueAsync(Board value)
    {
        Replace(value.Clone(), false, true);
        return Task.CompletedTask;
    }

    protected override Task OpenDefaultAsync()
    {
        if (!_started) Replace(Lab_.Blank(true), false, true);
        return Task.CompletedTask;
    }

    protected override void OnAttached()
    {
        _engine.Warm();
        if (!_started) _ = OpenDefaultAsync();
    }

    protected override void OnDispose() => _engine.Dispose();

    public void Replace(Board board, bool dirty, bool edit)
    {
        _engine.StopAll();
        _started  = true;
        _board    = board;
        _board.Normalize();
        _board.FixIds();
        _dirty    = dirty;
        _edit     = edit;
        _selected = null;
        _held.Clear();
        _engine.SetMaster(_board.MasterVolume / 100.0);
        _engine.SetDuck(_board.DuckPercent / 100.0);
        Prepare();
        Changed?.Invoke(this, EventArgs.Empty);
        Loaded?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseProblemsChanged();
        RaiseActionsChanged();
    }

    /// <summary>Speaks every text of the board ahead of time, so a press speaks at once.</summary>
    private void Prepare()
    {
        foreach (var b in _board.Buttons.Where(b => b.Action == ButtonAction.Speech && b.Speech.Trim().Length > 0).ToList()) _ = SpeechFiles.MakeAsync(b.Clone());
        // tones and digits are written to their files now, so the first press only has to play
        foreach (var b in _board.Buttons.Where(b => b.Action is ButtonAction.Tone or ButtonAction.DualTone).ToList()) SoundSource.Resolve(b);
    }

    // ---- editing ---------------------------------------------------------------------------------

    private void Touched(bool structural = false)
    {
        _dirty = true;
        _board.Normalize();
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseValueChanged();
        RaiseActionsChanged();
        if (structural) RaiseProblemsChanged();
    }

    public void EditBoard(Action<Board> change)
    {
        change(_board);
        _engine.SetMaster(_board.MasterVolume / 100.0);
        _engine.SetDuck(_board.DuckPercent / 100.0);
        Touched();
    }

    public void EditButton(string id, Action<SoundButton> change)
    {
        if (_board.Find(id) is not { } button) return;

        change(button);
        if (button.Action == ButtonAction.Speech) Prepare();
        Touched(true);
    }

    public void SetEditMode(bool edit)
    {
        if (_edit == edit) return;

        _edit = edit;
        if (edit) _engine.StopAll();
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseActionsChanged();
        Status = edit ? "Editing: drag buttons to move them, drag a corner to resize." : "Performing: press the buttons (or their keys).";
    }

    public void Select(string? id)
    {
        if (_selected == id) return;

        _selected = id;
        Changed?.Invoke(this, EventArgs.Empty);
        RaiseActionsChanged();
    }

    public double Snap(double value) => _board.Snap && _board.GridSize > 0 ? Math.Round(value / _board.GridSize) * _board.GridSize : value;

    public SoundButton AddButton(SoundButton? button = null)
    {
        button ??= new SoundButton { Label = "Button " + (_board.Buttons.Count + 1) };
        if (button.X == 0 && button.Y == 0) (button.X, button.Y) = NextSlot(button.Width, button.Height);
        _board.Buttons.Add(button);
        _selected = button.Id;
        Touched(true);
        Status = $"Added {button.Label}.";
        return button;
    }

    /// <summary>The first place, going along the rows of the board, where a button of this size does not cover another.</summary>
    private (double, double) NextSlot(double width, double height)
    {
        double step = _board.GridSize > 0 ? _board.GridSize : 20;
        double gap  = Math.Max(step, 10);
        for (double y = gap; y + height <= _board.Height; y += step)
        for (double x = gap; x + width <= _board.Width; x += step)
            if (!_board.Buttons.Any(b => x < b.X + b.Width + gap / 2 && x + width + gap / 2 > b.X && y < b.Y + b.Height + gap / 2 && y + height + gap / 2 > b.Y))
                return (x, y);

        return (gap, gap);
    }

    public void AddSoundButton(string source)
    {
        string label = SoundboardSounds.IsSilo(source) ? SoundboardSounds.SiloName(source) : Path.GetFileNameWithoutExtension(source);
        AddButton(SoundboardSounds.ButtonFor(source, label, 0, 0));
    }

    public void DeleteSelected()
    {
        if (Selected is not { } button) return;

        _engine.StopButton(button.Id);
        _board.Buttons.Remove(button);
        _selected = null;
        Touched(true);
        Status = $"Deleted {button.Label}.";
    }

    public void DuplicateSelected()
    {
        if (Selected is not { } button) return;

        var copy = button.Clone();
        copy.Id    = Guid.NewGuid().ToString("N")[..8];
        copy.Label = button.Label + " copy";
        copy.Hotkey = string.Empty;
        copy.X     = Math.Min(button.X + 24, _board.Width - button.Width);
        copy.Y     = Math.Min(button.Y + 24, _board.Height - button.Height);
        AddButton(copy);
    }

    public void BringToFront()
    {
        if (Selected is not { } b) return;

        _board.Buttons.Remove(b);
        _board.Buttons.Add(b);
        Touched();
    }

    public void SendToBack()
    {
        if (Selected is not { } b) return;

        _board.Buttons.Remove(b);
        _board.Buttons.Insert(0, b);
        Touched();
    }

    /// <summary>Places the button (snapped, and kept on the board).</summary>
    public void Place(string id, double x, double y, double? width = null, double? height = null)
    {
        if (_board.Find(id) is not { } b) return;

        double w = Math.Clamp(Snap(width ?? b.Width), 24, _board.Width);
        double h = Math.Clamp(Snap(height ?? b.Height), 24, _board.Height);
        b.Width  = w;
        b.Height = h;
        b.X      = Math.Clamp(Snap(x), 0, Math.Max(0, _board.Width - w));
        b.Y      = Math.Clamp(Snap(y), 0, Math.Max(0, _board.Height - h));
        Touched();
    }

    // ---- playing ---------------------------------------------------------------------------------

    public void Press(SoundButton button)
    {
        _held.Add(button.Id);
        if (button.Action == ButtonAction.Command)
        {
            Run(button.Command);
            return;
        }

        // pressing again stops what a loop or a music bed started
        if (button.Latches && _engine.IsPlaying(button.Id))
        {
            _engine.StopButton(button.Id);
            Status = $"Stopped {button.Label}.";
            return;
        }

        var (file, problem) = SoundSource.Resolve(button);
        if (problem is not null)
        {
            Status = problem;
            ReportProblem(LabProblemSeverity.Warning, "Playing", $"{button.Label}: {problem}");
            return;
        }

        if (file is not null)
        {
            Start(button, file);
            return;
        }

        if (SpeechFiles.IsReady(button, out var ready) && ready is not null)
        {
            Start(button, ready);
            return;
        }

        Status = "Getting the voice ready...";
        _ = SpeakAsync(button.Clone(), button.Id);
    }

    private async Task SpeakAsync(SoundButton copy, string id)
    {
        var (file, problem) = await SpeechFiles.MakeAsync(copy);
        if (file is null)
        {
            Status = problem ?? "Speaking failed.";
            return;
        }

        if (_board.Find(id) is { } live) Start(live, file);
    }

    private void Start(SoundButton button, string file)
    {
        // a button that plays while it is held does not start if it was let go while the sound was being made
        if (button.IsMomentary && !_held.Contains(button.Id)) return;

        _engine.Play(button, file);
        Status = $"Playing {button.Label}";
    }

    public void Release(SoundButton button)
    {
        _held.Remove(button.Id);
        if (button.IsMomentary && button.PlaysSound) _engine.StopButton(button.Id);
    }

    /// <summary>Presses a button once from the inspector: a button that plays while held is let go after a moment.</summary>
    public void Test(SoundButton button)
    {
        Press(button);
        if (!button.IsMomentary) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
                      {
                          timer.Stop();
                          Release(button);
                      };
        timer.Start();
    }

    public void Run(BoardCommand command)
    {
        switch (command)
        {
            case BoardCommand.MuteAll:
                _engine.ToggleMute();
                Status = _engine.Muted ? "Everything is muted." : "Sound is back on.";
                break;
            case BoardCommand.StopAll:
                _engine.StopAll();
                Status = "Stopped everything.";
                break;
            case BoardCommand.StopEffects:
                _engine.StopEffects();
                Status = "Stopped the effects.";
                break;
            case BoardCommand.StopMusic:
                _engine.StopMusic();
                Status = "Stopped the music.";
                break;
            case BoardCommand.FadeMusic:
                _engine.FadeMusic();
                Status = "Fading the music out.";
                break;
            case BoardCommand.MasterUp:
                SetMaster(_board.MasterVolume + 10);
                break;
            case BoardCommand.MasterDown:
                SetMaster(_board.MasterVolume - 10);
                break;
        }
    }

    public void SetMaster(int percent)
    {
        EditBoard(b => b.MasterVolume = Math.Clamp(percent, 0, 100));
        Status = $"Master volume {_board.MasterVolume}%";
    }

    public void ToggleMute() => Run(BoardCommand.MuteAll);

    /// <summary>The button whose key this is, when there is one.</summary>
    public SoundButton? ByHotkey(string key) => _board.Buttons.FirstOrDefault(b => b.Hotkey.Length > 0 && b.Hotkey.Equals(key, StringComparison.OrdinalIgnoreCase));

    // ---- sounds kept in the Silo -------------------------------------------------------------------

    public IReadOnlyList<string> SoundNames() => SoundboardSounds.Clips.Names();

    public bool SilosAvailable => SoundboardSounds.Clips.IsAvailable;

    /// <summary>Imports sound files into the Silo; true if anything was kept.</summary>
    public IReadOnlyList<string> Import(IEnumerable<string> paths)
    {
        var kept = new List<string>();
        foreach (string path in paths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Status = $"{path} is not there.";
                    continue;
                }

                var info = new FileInfo(path);
                if (info.Length > 64 * 1024 * 1024 && !LabUi.Confirm($"{info.Name} is {info.Length / 1048576} MB. Keep it in the Silo anyway?", "Soundboard Lab")) continue;

                string name = UniqueName(Path.GetFileNameWithoutExtension(path));
                SoundboardSounds.Clips.Save(name, new SoundClip { Name = name, Extension = info.Extension.ToLowerInvariant(), Data = File.ReadAllBytes(path) });
                kept.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Status = ex.Message;
            }
        }

        if (kept.Count > 0) Status = kept.Count == 1 ? $"Kept {kept[0]} in the Silo." : $"Kept {kept.Count} sounds in the Silo.";
        Changed?.Invoke(this, EventArgs.Empty);
        return kept;
    }

    private static string UniqueName(string name)
    {
        string candidate = name;
        for (int n = 2; SoundboardSounds.Clips.Exists(candidate); n++) candidate = $"{name} {n}";
        return candidate;
    }

    public void ImportDialog()
    {
        var dialog = new OpenFileDialog { Title = "Import sounds into the Silo", Filter = SoundboardSounds.Filter, Multiselect = true };
        if (dialog.ShowDialog() != true) return;

        var kept = Import(dialog.FileNames);
        if (kept.Count > 0 && LabUi.Confirm($"Add a button to the board for {(kept.Count == 1 ? "it" : "each")}?", "Soundboard Lab"))
            foreach (string name in kept)
                AddSoundButton(SoundboardSounds.SiloPrefix + name);
    }

    public void RemoveSound(string name)
    {
        int used = _board.Buttons.Count(b => b.Action == ButtonAction.Sound && b.Source.Equals(SoundboardSounds.SiloPrefix + name, StringComparison.OrdinalIgnoreCase));
        if (used > 0 && !LabUi.Confirm($"{name} is used by {used} button(s) on this board. Remove it from the Silo anyway?", "Soundboard Lab")) return;

        Status = SoundboardSounds.Clips.Delete(name) ? $"Removed {name} from the Silo." : $"{name} was not kept.";
        RaiseProblemsChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Plays a sound (or a button's sound) without a button on the board.</summary>
    public void Preview(string source)
    {
        _engine.StopButton("preview");
        var temp = SoundboardSounds.ButtonFor(source, "Preview", 0, 0);
        temp.Id   = "preview";
        temp.Role = SoundRole.Effect;
        var (file, problem) = SoundSource.Resolve(temp);
        if (file is null)
        {
            Status = problem ?? "Nothing to play.";
            return;
        }

        _engine.Play(temp, file);
        Status = "Previewing " + temp.Describe();
    }

    public void StopPreview() => _engine.StopButton("preview");

    public void BrowseSound(string buttonId)
    {
        var dialog = new OpenFileDialog { Title = "Choose a sound", Filter = SoundboardSounds.Filter };
        if (dialog.ShowDialog() != true) return;

        EditButton(buttonId, b =>
                             {
                                 b.Source = dialog.FileName;
                                 if (SoundboardSounds.MusicExtensions.Contains(Path.GetExtension(dialog.FileName), StringComparer.OrdinalIgnoreCase) && b.Label.StartsWith("Button", StringComparison.Ordinal)) b.Role = SoundRole.MusicBed;
                                 if (b.Label.StartsWith("Button", StringComparison.Ordinal)) b.Label = Path.GetFileNameWithoutExtension(dialog.FileName);
                             });
    }

    // ---- problems --------------------------------------------------------------------------------

    protected override IEnumerable<LabProblem> Validate()
    {
        foreach (var b in _board.Buttons)
        {
            string? problem = null;
            switch (b.Action)
            {
                case ButtonAction.Sound when b.Source.Length == 0:
                    problem = "has no sound chosen";
                    break;
                case ButtonAction.Sound when SoundboardSounds.IsSilo(b.Source) && SoundboardSounds.Clips.IsAvailable && !SoundboardSounds.Clips.Exists(SoundboardSounds.SiloName(b.Source)):
                    problem = $"uses {SoundboardSounds.SiloName(b.Source)}, which is not in the Silo";
                    break;
                case ButtonAction.Sound when !SoundboardSounds.IsSilo(b.Source) && !File.Exists(b.Source):
                    problem = $"uses {b.Source}, which is not there";
                    break;
                case ButtonAction.Speech when b.Speech.Trim().Length == 0:
                    problem = "has nothing to say";
                    break;
                case ButtonAction.DualTone when b.Standard != DualStandard.Custom && ToneSynth.Playable(b.Standard, b.Digits).Count == 0:
                    problem = $"has no digits {b.Standard.ToString().ToUpperInvariant()} can play";
                    break;
            }

            if (problem is not null)
            {
                string id = b.Id;
                yield return new LabProblem(LabProblemSeverity.Warning, "Buttons", $"{b.Label} {problem}", null, () => Select(id));
            }
        }

        var clashes = _board.Buttons.Where(b => b.Hotkey.Length > 0).GroupBy(b => b.Hotkey, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
        foreach (var g in clashes) yield return new LabProblem(LabProblemSeverity.Warning, "Buttons", $"Key {g.Key} is on {g.Count()} buttons: {string.Join(", ", g.Select(b => b.Label))}");
    }

    // ---- saved boards ----------------------------------------------------------------------------

    public void OpenSaved(string name)
    {
        if (Saved.Load(name) is not { } board)
        {
            Status = $"There is no board called {name} in the Silo.";
            return;
        }

        if (_dirty && !LabUi.Confirm("This board has changes that are not kept in the Silo. Open another anyway?", "Soundboard Lab")) return;

        if (string.IsNullOrWhiteSpace(board.Name)) board.Name = name;
        Replace(board, false, false);
        RememberRecent(name);
        Status = $"Launched {name}: press the buttons.";
    }

    public override Task OpenRecentAsync(string text)
    {
        OpenSaved(text);
        return Task.CompletedTask;
    }

    public override IReadOnlyList<LabLibrarySection> LibrarySections(string filter)
    {
        bool Wanted(string text) => filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var boards = Saved.Names().Where(Wanted)
                          .Select(name => new LabLibraryEntry(name, "Soundboard", null, "Sound", () => OpenSaved(name), name == _board.Name, ToolTip: "Double-click to launch", Delete: () => Saved.Delete(name)))
                          .ToList();
        var sounds = SoundNames().Where(Wanted)
                                 .Select(name => new LabLibraryEntry(name, "Sound", null, "Play", () => AddSoundButton(SoundboardSounds.SiloPrefix + name), ToolTip: "Double-click to add a button for it", Delete: () => RemoveSound(name)))
                                 .ToList();
        string? note = Saved.IsAvailable ? null : "No Silo is open";
        return [new("Soundboards in the Silo", "Database5", boards, Note: note), new("Sounds in the Silo", "Database5", sounds, Note: note)];
    }

    private void SaveBoard()
    {
        string? name = _board.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = LabUi.Ask("Save board", "What is the board called?");
            if (string.IsNullOrWhiteSpace(name)) return;
        }

        try
        {
            _board.Name = name.Trim();
            Saved.Save(_board.Name, _board);
            _dirty = false;
            Status = $"Kept {_board.Name} in the Silo";
            Changed?.Invoke(this, EventArgs.Empty);
            RaiseValueChanged();
            RaiseActionsChanged();
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
    }

    private void DeleteSaved()
    {
        if (string.IsNullOrWhiteSpace(_board.Name)) return;

        Status = Saved.Delete(_board.Name) ? $"Removed {_board.Name} from the Silo" : $"{_board.Name} was not kept";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NewBoard(bool starter)
    {
        if (_dirty && !LabUi.Confirm("This board has changes that are not kept in the Silo. Start another anyway?", "Soundboard Lab")) return;

        Replace(Lab_.Blank(starter), false, true);
    }

    // ---- actions ---------------------------------------------------------------------------------

    public override IEnumerable<ProvidedCommand> GlobalActions
    {
        get
        {
            yield return ProvidedCommand.From("new-board", "_New Board", () => NewBoard(false), "TbNew", null, "Start with an empty board");
            yield return ProvidedCommand.From("starter-board", "New Board with _Examples", () => NewBoard(true), "TbNew", null, "Start with a few buttons: tones, a dialed number, speech and the commands");
            yield return ProvidedCommand.From("mode", EditMode ? "_Perform" : "_Edit", () => SetEditMode(!_edit), _edit ? "Play" : "Settings", null, "Switch between editing the board and performing it") with { Gesture = "F4" };
            yield return ProvidedCommand.From("stop-all", "S_top All", () => Run(BoardCommand.StopAll), "Stop", null, "Stop every sound") with { Gesture = "F6" };
            yield return ProvidedCommand.From("mute", "_Mute All", ToggleMute, "Mute", null, "Silence everything until this is chosen again") with { IsChecked = () => _engine.Muted };
            yield return ProvidedCommand.From("show-saved", "Show _Saved Boards", () => Host?.ShowInspector("saved"), "Folder17", null, "Show the boards and sounds kept in the Silo");
            yield return ProvidedCommand.From("settings", "Open Se_ttings", () => Host?.ShowInspector(LabParametersInspector.InspectorId), "Settings", null, "Show the Lab's parameters");
        }
    }

    public override IEnumerable<ProvidedCommand> ContextActions
    {
        get
        {
            yield return ProvidedCommand.From("add-button", "_Add Button", () => AddButton(), "TbNew", null, "Put a new button on the board") with { Gesture = "Ctrl+B" };
            yield return ProvidedCommand.From("add-sound", "Add _Sound Button...", AddLocal, "Sound", null, "Choose a sound file and put a button for it on the board");
            yield return ProvidedCommand.From("import", "_Import Sounds into the Silo...", ImportDialog, "TbOpen", () => SoundboardSounds.Clips.IsAvailable, "Keep sound files in the Silo so boards can play them anywhere");
            yield return ProvidedCommand.From("duplicate", "D_uplicate Button", DuplicateSelected, "TbCopy", () => Selected is not null, "Copy the selected button") with { Gesture = "Ctrl+D" };
            yield return ProvidedCommand.From("delete-button", "Delete _Button", DeleteSelected, "TbDelete", () => Selected is not null, "Remove the selected button from the board");
            yield return ProvidedCommand.From("front", "Bring to _Front", BringToFront, null, () => Selected is not null, "Put the selected button above the others");
            yield return ProvidedCommand.From("back", "Send to Bac_k", SendToBack, null, () => Selected is not null, "Put the selected button below the others");
            yield return ProvidedCommand.From("test", "_Test Button", () => { if (Selected is { } b) Test(b); }, "Play", () => Selected is not null, "Press the selected button");
        }
    }

    /// <summary>Puts a button on the board for each sound file chosen on this machine.</summary>
    public void AddLocal()
    {
        var dialog = new OpenFileDialog { Title = "Choose a sound", Filter = SoundboardSounds.Filter, Multiselect = true };
        if (dialog.ShowDialog() != true) return;

        foreach (string path in dialog.FileNames) AddSoundButton(path);
    }

    public override IEnumerable<ProvidedCommand> ValueActions
    {
        get
        {
            yield return ProvidedCommand.From("save", "_Save Board", SaveBoard, "TbSave", () => Saved.IsAvailable, "Keep the board in the Silo") with { Gesture = "Ctrl+S" };
            yield return ProvidedCommand.From("copy-json", "Copy Board as _JSON", () => LabUi.Copy(JsonConvert.SerializeObject(_board, Formatting.Indented, new Newtonsoft.Json.Converters.StringEnumConverter())), "TbCopy", null, "Put the board on the clipboard as JSON");
            yield return ProvidedCommand.From("delete", "_Delete Saved Board", DeleteSaved, "TbDelete", () => Saved.IsAvailable && !string.IsNullOrWhiteSpace(_board.Name) && Saved.Exists(_board.Name), "Remove the board from the Silo");
        }
    }

    // ---- terminal --------------------------------------------------------------------------------

    private static Task<string?> Done(string? text) => Task.FromResult(text);

    public override IReadOnlyList<LabTerminalCommand> TerminalCommands =>
    [
        new("buttons", "buttons                list the buttons and what each does", _ => Done(string.Join('\n', _board.Buttons.Select(b => $"{b.Label,-20} {b.Action,-9} {b.Describe()}")))),
        new("press", "press <label>          press a button (a button that plays while held is let go after a moment)", call => Done(PressCommand(call.Rest))),
        new("stop", "stop                   stop every sound", _ => Done(StopCommand())),
        new("mute", "mute                   mute or unmute everything", _ => Done(MuteCommand())),
        new("volume", "volume [0-100]         the master volume", call => Done(VolumeCommand(call.Rest))),
        new("mode", "mode [edit|perform]    edit or perform the board", call => Done(ModeCommand(call.Rest))),
        new("sounds", "sounds                 the sounds kept in the Silo", _ => Done(SilosAvailable ? string.Join('\n', SoundNames()) : "No Silo is open.")),
        new("import", "import <path>           keep a sound file in the Silo", call => Done(ImportCommand(call.Rest))),
        new("add", "add <path|silo:name>     put a button for a sound on the board", call => Done(AddCommand(call.Rest))),
        new("save", "save [name]             keep the board in the Silo", call => Done(SaveCommand(call.Rest))),
        new("list", "list                    the boards kept in the Silo", _ => Done(Saved.IsAvailable ? string.Join('\n', Saved.Names()) : "No Silo is open.")),
        new("open", "open <name>             launch a board kept in the Silo", call => Done(OpenCommand(call.Rest)))
    ];

    private SoundButton Need(string label)
    {
        return _board.Buttons.FirstOrDefault(b => b.Label.Equals(label.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"There is no button called {label.Trim()}. Type buttons to see them.");
    }

    private string PressCommand(string rest)
    {
        var button = Need(rest);
        Test(button);
        return Status;
    }

    private string StopCommand()
    {
        Run(BoardCommand.StopAll);
        return Status;
    }

    private string MuteCommand()
    {
        ToggleMute();
        return Status;
    }

    private string VolumeCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) return $"Master volume {_board.MasterVolume}%.";
        if (!int.TryParse(rest.Trim(), out int n) || n is < 0 or > 100) throw new InvalidOperationException("The volume is a number from 0 to 100.");

        SetMaster(n);
        return Status;
    }

    private string ModeCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) return _edit ? "Editing." : "Performing.";

        SetEditMode(!rest.Trim().StartsWith('p'));
        return _edit ? "Editing." : "Performing.";
    }

    private string ImportCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) throw new InvalidOperationException("Import which file? import <path>");

        var kept = Import([rest.Trim().Trim('"')]);
        return kept.Count == 0 ? Status : $"Kept {kept[0]} in the Silo.";
    }

    private string AddCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) throw new InvalidOperationException("Add which sound? add <path> or add silo:<name>");

        AddSoundButton(rest.Trim().Trim('"'));
        return Status;
    }

    private string SaveCommand(string rest)
    {
        if (!string.IsNullOrWhiteSpace(rest)) _board.Name = rest.Trim();
        if (string.IsNullOrWhiteSpace(_board.Name)) throw new InvalidOperationException("Give it a name: save <name>.");

        try
        {
            Saved.Save(_board.Name, _board);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }

        _dirty = false;
        RaiseValueChanged();
        return $"Kept {_board.Name} in the Silo.";
    }

    private string OpenCommand(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) throw new InvalidOperationException("Open which? Type list to see them.");

        OpenSaved(rest.Trim());
        return Status;
    }

    public override UIElement BuildMainView() => new SoundboardView(this);
}
