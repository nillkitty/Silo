using Telefrag.DI;

namespace Silo.Labs.States;

/// <summary>
///     Designs and tests state machines the way Telefrag.State describes them: states, the constraints that decide whether a transition may be taken,
///     the effects it has, and the transitions between states. The machine is drawn and edited on a canvas, then run: change its fields, see which
///     transitions can be taken and why the others cannot, take them, and read the log.
/// </summary>
[Singleton(typeof(ILab))]
public class StateLab : LabBase<StateDoc>
{
    public override string Description => "Design and test state machines: draw states and the transitions between them, give transitions constraints and effects, " +
                                          "then run the machine, change its fields and see what can be taken. Keep the machine in the Silo or copy it as Telefrag.State code.";

    public override string IconKey => "FlowDiagram";

    public override string Id => "state";

    public override string Name => "State Lab";

    public override int Order => 75;

    public override LabSession<StateDoc> CreateSession()
    {
        return new StateLabSession(this);
    }

    protected override bool TryConvert(object? candidate, out StateDoc? value)
    {
        switch (candidate)
        {
            case StateDoc doc:
                value = doc;
                return true;
            // asked of every node when a menu is built: only the state:Name way of referring to a kept machine is taken
            case string text when text.StartsWith("state:", StringComparison.OrdinalIgnoreCase) && text.Length > 6:
                value = StateLabSession.Kept.Load(text[6..].Trim());
                return value is not null;
            default:
                value = null;
                return false;
        }
    }

    public override bool TryParse(string text, out StateDoc? value)
    {
        string name = text.Trim();
        if (name.StartsWith("state:", StringComparison.OrdinalIgnoreCase)) name = name[6..].Trim();

        // a name the Silo keeps opens that machine, any other starts a new one of that name
        value = name.Length == 0 ? null : StateLabSession.Kept.Load(name) ?? new StateDoc { Name = name };
        return value is not null;
    }
}
