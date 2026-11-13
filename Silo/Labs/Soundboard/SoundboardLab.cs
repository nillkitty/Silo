using System.IO;
using Telefrag.DI;

namespace Silo.Labs.Soundboard;

public static class SoundboardLabParameters
{
    public const string Grid = "grid";
    public const string Duck = "duck";
}

/// <summary>
///     Makes soundboards: buttons placed anywhere on a board that play sounds (imported into the Silo or on this machine), tones, dual tones (DTMF and MF) or speech, or run commands such as Mute All.
///     Boards are kept in the Silo and launched from it.
/// </summary>
[Singleton(typeof(ILab))]
public class SoundboardLab : LabBase<Board>
{
    public override string Description => "Build soundboards: put buttons anywhere on a board and make each play a sound (imported into the Silo or on this machine), a tone, a dual tone (DTMF or MF) or speech, " +
                                          "or run a command such as Mute All. Choose whether a button is momentary, stackable or monophonic, play music behind everything as a bed, and style the buttons. " +
                                          "Boards are kept in the Silo and launched from it.";

    public override string IconKey => "Sound";

    public override string Id => "soundboard";

    public override string Name => "Soundboard Lab";

    public override int Order => 85;

    protected override IEnumerable<LabParameter> DefineParameters()
    {
        yield return new LabParameter(SoundboardLabParameters.Grid, "Grid of a new board", LabParameterKind.Text, "20", "The spacing, in pixels, buttons snap to on a new board (0 for none).", "New boards", hint: "20");
        yield return new LabParameter(SoundboardLabParameters.Duck, "Music level under effects (%)", LabParameterKind.Text, "35", "How loud a music bed is while effects play over it on a new board (0 to 100).", "New boards", hint: "35");
    }

    public override LabSession<Board> CreateSession() => new SoundboardLabSession(this);

    /// <summary>A new board with the Lab's own defaults.</summary>
    public Board Blank(bool starter)
    {
        var board = starter ? Board.Starter() : new Board();
        board.GridSize    = int.TryParse(Parameters[SoundboardLabParameters.Grid].Text, out int grid) ? Math.Clamp(grid, 0, 200) : 20;
        board.DuckPercent = int.TryParse(Parameters[SoundboardLabParameters.Duck].Text, out int duck) ? Math.Clamp(duck, 0, 100) : 35;
        return board;
    }

    protected override bool TryConvert(object? candidate, out Board? value)
    {
        switch (candidate)
        {
            case Board board:
                value = board;
                return true;
            case string path when SoundboardSounds.IsSound(path):
                value = OneSound(path);
                return true;
            case Uri { IsFile: true } uri when SoundboardSounds.IsSound(uri.LocalPath):
                value = OneSound(uri.LocalPath);
                return true;
            default:
                value = null;
                return false;
        }
    }

    public override bool TryParse(string text, out Board? value)
    {
        string path = text.Trim().Trim('"');
        if (SoundboardSounds.IsSound(path))
        {
            value = OneSound(path);
            return true;
        }

        value = null;
        return false;
    }

    private Board OneSound(string path)
    {
        var board = Blank(false);
        board.Buttons.Add(SoundboardSounds.ButtonFor(path, Path.GetFileNameWithoutExtension(path), board.GridSize == 0 ? 40 : board.GridSize * 2, board.GridSize == 0 ? 40 : board.GridSize * 2));
        return board;
    }
}
