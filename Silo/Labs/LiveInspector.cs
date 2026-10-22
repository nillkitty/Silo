using System.Windows.Threading;

namespace Silo.Labs;

/// <summary>What a session that has live inspectors tells them: something changed, draw again.</summary>
internal interface ILabLive
{
    event EventHandler? Changed;
}

/// <summary>The base of an inspector that draws itself again a moment after the session says something changed (and only once it has been shown).</summary>
internal abstract class LiveInspector : LabInspector
{
    private bool _pending;
    private bool _ready;

    protected LiveInspector(ILabLive session)
    {
        session.Changed += (_, _) => Poke();
    }

    protected sealed override UIElement BuildView()
    {
        var view = Create();
        _ready = true;
        Refresh();
        return view;
    }

    protected abstract UIElement Create();

    protected abstract void Refresh();

    public override void OnShown() => Refresh();

    protected void Poke()
    {
        if (!_ready || _pending) return;

        _pending = true;
        (Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher).InvokeAsync(() =>
                                                                                      {
                                                                                          _pending = false;
                                                                                          Refresh();
                                                                                      }, DispatcherPriority.Background);
    }
}
