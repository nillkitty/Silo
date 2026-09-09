namespace Silo.Contracts;

public interface IViewModel<TType>
{
    Type ModelType { get; }
}

public interface IViewModelBinder
{
    Type? GetModelType(Type forType);
    Type? GetModelType<TType>();
}