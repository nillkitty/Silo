using System.Collections.ObjectModel;
using System.Windows.Controls;
using Telefrag.DI;

namespace Silo.Diagnostics;

[Singleton(typeof(DebugContext))]
public class DebugContext
{
    public ObservableCollection<IAdorner> Adorners           { get; } = [];
    public ObservableCollection<Control>  DebugPanelControls { get; } = [];
    public ObservableCollection<Control>  MenuItems          { get; } = [];
    public ObservableCollection<IView>    Views              { get; } = [];

    public static DebugContext Get()
    {
        return App.Require<DebugContext>();
    }
}

public abstract record DebugAdornerBase<TType> : IAdorner<TType>
{
    protected abstract Control? OnGetControl(AdornContext ctx);

    public Control? GetControl(AdornContext ctx)
    {
        if (OnGetControl(ctx) is { } c)
        {
            c.Visibility = App.Instance!.DesignLevel.IsDebug ? Visibility.Visible : Visibility.Collapsed;
            return c;
        }

        return null;
    }
}