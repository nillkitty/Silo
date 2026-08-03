using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Silo.Extensions;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.Docking;

namespace Silo.Ui;

public static class Ui
{
    public static TModel ModalModel<TModel>(TModel m, string? title = null)
    {
        m.Required();

        var form    = new RadDataForm() { CurrentItem = m };
        var button1 = new Button() { Content = "Cancel", IsCancel = true };
        var button2 = new Button() { Content = "OK", IsDefault = true };
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

        button1.Click += (object o, RoutedEventArgs e) =>
                         {
                             if (o is Button
                                      {
                                          Parent: DockPanel
                                                  {
                                                      Parent: StackPanel
                                                              {
                                                                  Parent:
                                                                  RadWindow ww
                                                              }
                                                  }
                                      })
                             {
                                 ww.DialogResult = true;
                                 ww.Close();
                             }
                         };

        var stack = new StackPanel()
                    {
                        Children =
                        {
                            form,
                            dock
                        }
                    };
        var w = new RadWindow()
                {
                    Header  = title ?? m.GetType().FullName,
                    Content = stack
                };

        return m;
    }
}