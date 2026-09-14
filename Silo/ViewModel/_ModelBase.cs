using System.Windows;
using Serilog;
using Telefrag;

namespace Silo.Model;

/// <summary>
/// Represents a ViewModel for an item whose DTO is <typeparamref name="TDataObject"/>
/// </summary>
/// <typeparam name="TDataObject"></typeparam>
public class ModelBase<TDataObject> : ModelBase
{
    public TDataObject? Data { get; protected set; }
}

/// <summary>
/// Represents the base class for all ViewModels
/// </summary>
public abstract class ModelBase : DependencyObject, ILogContext
{
    /// <summary>
    /// Gets a logger for the model's scope
    /// </summary>
    public ILogger Logger { get; }

    protected ModelBase()
    {
        Logger = Log.Logger.ForContext(GetType());
    }
}

public interface ILogContext
{
    ILogger Logger { get; }
}