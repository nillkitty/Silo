using System.Windows;

namespace Silo.Contracts.Adapters;

public interface IAdapter
{
    bool   ThrowErrors { get; set; }
    Type[] ConversionsFor(Type inputType);

    Task<object?> ConvertAsync(object inObj);

    /// <summary>
    ///     True when the same input always gives the same output, so a Flow may cache the result.
    /// </summary>
    bool Pure => false;

    /// <summary>The UI that configures the adapter, or null when it has nothing to configure.</summary>
    UIElement? GetConfigUi()
    {
        return null;
    }
}

/// <summary>
///     Implemented by an adapter that wants to know about the Flow it runs in. Called once, before the first conversion.
/// </summary>
public interface IFlowAdapter
{
    void AttachToFlow(Flows.FlowContext context);
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
