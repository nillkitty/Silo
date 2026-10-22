namespace Silo.Contracts;

public interface IConnectionState
{
    bool IsConnected { get; }
    bool IsDisposed  { get; }
    bool IsFailed    { get; }
}

public interface IStateManager
{
    Type    GetStateType();
    object? GetState(object context);
}

public interface IStateManager<in TModel, out TState> : IStateManager where TState : IConnectionState
{
    TState? GetState(TModel context);

    object? IStateManager.GetState(object context)
    {
        return context is TModel tm ? GetState(tm) : null;
    }

    Type IStateManager.GetStateType()
    {
        return typeof(TState);
    }
}