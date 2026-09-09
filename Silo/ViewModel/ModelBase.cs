using System.Windows;
using Serilog;
using Telefrag;

namespace Silo.Model;

public class ModelBase : DependencyObject, ILogContext
{
    public ILogger Logger { get; }

    public ModelBase()
    {
        Logger = Log.Logger.ForContext(GetType());
    }
}

public interface ILogContext
{
    ILogger Logger { get; }
}