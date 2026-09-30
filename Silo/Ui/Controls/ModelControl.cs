using System.Windows;
using System.Windows.Controls;
using Silo.Commanding;
using Silo.Extensions;

namespace Silo.Ui.Controls;

/// <summary>
///     A control which displays the procedurally generated user interface
///     as defined by its ViewModel and descendants.
/// </summary>
/// <typeparam name="TModel">The type of the ViewModel.</typeparam>
public class ModelControl<TModel> : UserControl
{
    private readonly IUiBuilder<TModel> _ib;

    /// <summary>
    ///     Gets a reference to the underlying ViewModel
    /// </summary>
    public TModel Model { get; }

    private UIElement _placeholder(string message)
    {
        return new TextBlock
               {
                   Text                = message,
                   FontSize            = 18.0,
                   HorizontalAlignment = HorizontalAlignment.Center,
                   VerticalAlignment   = VerticalAlignment.Center,
                   Padding             = new Thickness(5.0)
               };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Content = _build(Model);
        }
        catch (Exception ex)
        {
            Content = _placeholder($"Failed to initialize ({ex.GetType().Name}).  {ex.Message}");
        }
    }

    private UIElement _build(TModel model)
    {
        if (model is null)
            return _placeholder($"Model for '{typeof(TModel).ShortDisplayName()}' was null.");

        return _ib.Build(model);
    }

    /// <summary>
    ///     Creates a ModelControl for the specified model.
    /// </summary>
    public ModelControl(TModel model)
    {
        Model = model.Required()!;
        _ib   = App.Require<IUiBuilder<TModel>>();
        string text = Model?.ToString() ?? Model?.GetType().ShortDisplayName() ?? "";
        Content =  _placeholder(text);
        Loaded  += OnLoaded;
    }
}