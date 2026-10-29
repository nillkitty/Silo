namespace Silo.Contracts.Adapters;

public interface IAdapter
{
    bool   ThrowErrors { get; set; }
    Type[] ConversionsFor(Type inputType);

    Task<object?> ConvertAsync(object inObj);
}

public interface IAdapter<in TFrom, TTo> : IAdapter
{
    Task<object?> ConvertAsync(TFrom inObj);

    async Task<object?> IAdapter.ConvertAsync(object inObj)
    {
        if (inObj is TFrom tf)
            return await ConvertAsync(tf);
        return null;
    }

    Type[] IAdapter.ConversionsFor(Type inType)
    {
        return inType == typeof(TFrom) ? [typeof(TTo)] : [];
    }
}