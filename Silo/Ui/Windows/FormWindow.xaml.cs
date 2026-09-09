using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Silo.Model;

namespace Silo.Ui.Windows;

/// <summary>
/// Interaction logic for FormWindow.xaml
/// </summary>
public partial class FormWindow : Window
{
    public FormWindow()
    {
        InitializeComponent();
    }

    public required OpenSilo Silo       { get; init; }
    public required Type     TargetType { get; init; }
    public required Type     ModelType  { get; init; }
    public          object   Result     { get; private set; }
    public          bool     AddToSilo  { get; init; }

    public static async Task<TType?> PromptFor<TType>(OpenSilo silo,
                                                      string?  title     = null,
                                                      bool     addToSilo = true,
                                                      Action<TType>?
                                                          configFunc = null,
                                                      bool modal = true)
    {
        var   s     = silo.Required();
        var   vmb   = s.Components.Resolve<IViewModelBinder>();
        var   svc   = s.Components.Resolve<IViewModel<TType>>();
        Type? model = svc?.ModelType ?? vmb?.GetModelType<TType>();
        model ??= typeof(TType);
        if (model is null)
            throw new
                InvalidOperationException($"Could not find a view model for type '{typeof(TType)}' directly or" +
                                          $" via a view model binder.");

        var f = new FormWindow()
                {
                    Silo = s,
                    Title = title ??
                            $"Select a {typeof(TType).ShortDisplayName()}",
                    TargetType = typeof(TType),
                    ModelType  = model,
                    AddToSilo  = addToSilo
                };
        bool? dr = f.ShowDialog();
        if (dr is true && f is { Result: TType tr })
        {
            return tr;
        }

        return default;
    }
}