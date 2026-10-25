using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using Silo.Design;
using Silo.Drawio.Host;

namespace Silo.Drawio.Options;

/// <summary>
///     Hooks a <see cref="DrawioView" /> into the Silo design menu (Ctrl+right-click at Author level):
///     a few quick switches for the restrictions people reach for most, plus "Diagram options..." which opens
///     the full panel with every option.
/// </summary>
public static class DrawioDesign
{
    /// <summary>Adds the diagram items to <paramref name="host" /> (usually the pane's root element, so the menu is reachable anywhere inside it).</summary>
    public static void Attach(DependencyObject host, DrawioView view)
    {
        var items = DesignMenu.EnsureItems(host);

        items.Add(Toggle(view, "Lock generated shapes", o => o.LockGeneratedCells, (o, v) => o.LockGeneratedCells = v));
        items.Add(Toggle(view, "Chromeless (view only)", o => o.Chromeless, (o, v) => o.Chromeless                 = v));
        items.Add(Toggle(view, "Hide shape sidebar", o => o.HideSidebar, (o, v) => o.HideSidebar                   = v));
        items.Add(Toggle(view, "Hide toolbar", o => o.HideToolbar, (o, v) => o.HideToolbar                         = v));
        items.Add(Toggle(view, "Block cloud storage", o => o.DisableCloudStorage, (o, v) => o.DisableCloudStorage  = v));
        items.Add(DesignMenuItem.Separator());
        items.Add(new DesignMenuItem { Header = "Diagram options...", Command = new Relay(() => DrawioOptionsWindow.ShowFor(view)) });
        items.Add(new DesignMenuItem { Header = "Reload editor", Command      = new Relay(() => _ = view.ResetEditorAsync()) });
    }

    private static DesignMenuItem Toggle(DrawioView view, string header, Func<DrawioOptions, bool> get, Action<DrawioOptions, bool> set)
    {
        return DesignMenuItem.Toggle(header, DesignLevel.Author, () => get(view.Options), v => view.EditOptions(o => set(o, v)));
    }

    private sealed class Relay(Action run) : ICommand
    {
        public bool CanExecute(object? parameter)
        {
            return true;
        }

        public void Execute(object? parameter)
        {
            run();
        }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }
}
