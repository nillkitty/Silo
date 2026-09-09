using System.Windows;
using System;
using System.Windows;
using System.Windows.Data;
using Telerik.Windows.Controls.Docking;

namespace Silo.Commanding;

internal interface IUiBuilder<TModel>
{
    UIElement Build(TModel model);
}