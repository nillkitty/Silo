using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Silo.Extensions;

namespace Silo.Ui;

public static class Ui
{
    public static TModel ModalModel<TModel>(TModel m, string? title = null)
    {
        m.Required();

        // RadDataForm replacement: reuse DefaultUiBuilder's hand-rolled reflection-based
        // property editor (its arbitrary-object fallback - see Ui/DefaultUiBuilder.cs)
        // rather than duplicating that editor UI here.
        var form    = new DefaultUiBuilder<TModel>().Build(m);
        var button1 = new Button() { Content          = "Cancel", IsCancel = true };
        var button2 = new Button() { Content          = "OK", IsDefault    = true };
        var dock = new DockPanel()
                   {
                       LastChildFill = false,
                       Children =
                       {
                           button1, button2
                       }
                   };
        DockPanel.SetDock(button1, Dock.Right);
        DockPanel.SetDock(button2, Dock.Right);

        // NOTE: these two handlers were already swapped (the "Cancel" button wired to
        // Handlers.OkButton, "OK" wired to Handlers.CancelButton), and the window below
        // was already never actually shown (no .Show()/.ShowDialog() call) - both left
        // exactly as they were found, since fixing either is outside the scope of this
        // Telerik-removal pass, but both look like pre-existing bugs worth a second look.
        button1.Click += Handlers.OkButton;
        button2.Click += Handlers.CancelButton;

        var stack = new StackPanel()
                    {
                        Children =
                        {
                            form,
                            dock
                        }
                    };
        var w = new Window()
                {
                    Title   = title ?? m.GetType().FullName,
                    Content = stack
                };

        return m;
    }
}
