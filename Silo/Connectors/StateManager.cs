namespace Silo.Connectors;

public abstract class StateManager<TModel, TState> : IStateManager where TState : IConnectionState
{
    protected abstract TState GetState(object context);

    Type IStateManager.GetStateType()
    {
        return typeof(TState);
    }

    object? IStateManager.GetState(object context)
    {
        return GetState(context);
    }
}

public abstract class StateManager : IStateManager
{
    protected abstract Type    GetStateType();
    protected abstract object? GetState(object context);

    Type IStateManager.GetStateType()
    {
        return GetStateType();
    }

    object? IStateManager.GetState(object context)
    {
        return GetState(context);
    }
}

public class Connectionless<TModel> : StateManager<TModel, ConnectionlessState>
{
    private ConnectionlessState _state = new(false);

    public bool IsError
    {
        get;
        set
        {
            field = value;
            _set();
        }
    }

    public string? StateText
    {
        get;
        set
        {
            field = value;
            _set();
        }
    }

    private void _set()
    {
        _state = new ConnectionlessState(IsError, StateText);
    }

    protected override ConnectionlessState GetState(object context)
    {
        return _state;
    }
}

public record ConnectionlessState(bool IsError, string? StateText = null) : IConnectionState
{
    public bool IsConnected => true;
    public bool IsDisposed  => false;
    public bool IsFailed    => IsError;

    public override string ToString()
    {
        return StateText ?? (IsError ? "Failed" : "OK (Connectionless)");
    }
}