using System.Windows;
using Silo.UI;

namespace Silo;

/// <summary>
/// Marks a ViewModel as being a model dialog
/// </summary>
public class ModalDialogAttribute : Attribute, IWindowChrome
{
    public bool CanMinimize   { get; set; } = false;
    public bool CanResize     { get; set; } = false;
    public bool ShowInTaskbar { get; set; } = false;
    public bool Topmost       { get; set; } = false;

    public void Apply(Window target)
    {
        target.Required();
        ResizeMode m       = 0;
        if (CanMinimize) m |= ResizeMode.CanMinimize;
        if (CanResize) m   |= ResizeMode.CanResize;
        target.ResizeMode    = m;
        target.ShowInTaskbar = ShowInTaskbar;
        target.Topmost       = Topmost;
    }
}