using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Silo.Model;

namespace Silo.Commanding;

public interface IAboutBox
{
    Control AboutContent { get; }
}

public class ModelControl<TModel> : UserControl
{
    private IUiBuilder<TModel> _ib;
    public  TModel             Model { get; }

    public ModelControl(TModel model)
    {
        Model = model.Required()!;
        _ib   = App.Require<IUiBuilder<TModel>>();
        string text = Model?.ToString() ?? Model?.GetType().ShortDisplayName() ?? "";
        Content     =  _placeholder(text);
        this.Loaded += OnLoaded;
    }

    UIElement _placeholder(string message) => new TextBlock()
                                              {
                                                  Text                = message,
                                                  FontSize            = 18.0,
                                                  HorizontalAlignment = HorizontalAlignment.Center,
                                                  VerticalAlignment   = VerticalAlignment.Center,
                                                  Padding             = new Thickness(5.0)
                                              };

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
}

public class ContentWindow : Window
{
    public ContentWindow(UIElement content)
    {
        Content = content.Required();
    }
}

public static class AboutBox
{
    public static bool? Show()
    {
        var model = new AboutModel();
        var ctrl  = new ModelControl<AboutModel>(model);
        var win   = new ContentWindow(ctrl);
        return win.ShowDialog();
    }


    public class AboutModel : ModelBase
    {
        internal string AboutText => App.Instance!.FindResource("AboutText")?.ToString() ?? "\n\nCant find about text resource!\n\n";

        internal UIElement MainSection => _group(new TextBlock()
                                                 {
                                                     Text                = AboutText,
                                                     TextWrapping        = TextWrapping.Wrap,
                                                     HorizontalAlignment = HorizontalAlignment.Stretch,
                                                     VerticalAlignment   = VerticalAlignment.Top,
                                                     Padding             = new Thickness(5.0),
                                                     Margin              = new Thickness(2.0),
                                                     MinHeight           = 32.0
                                                 }, "About Silo");

        internal UIElement Content => new ScrollViewer()
                                      {
                                          MaxHeight                     = 300.0,
                                          HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                                          VerticalScrollBarVisibility   = ScrollBarVisibility.Visible,
                                          Content                       = Sections
                                      };

        internal StackPanel Sections { get; }

        private GroupBox _group(UIElement content, string header)
        {
            return new GroupBox()
                   {
                       Content = content,
                       Header  = header,
                       Padding = new Thickness(5.0)
                   };
        }

        public AboutModel()
        {
            StackPanel p = new();
            p.Children.Add(MainSection);
            var ext = App.Instance!.Components.Resolve<IEnumerable<IAboutBox>>()?.ToList();
            if (ext is [..])
            {
                foreach (var e in ext)
                {
                    if (e?.AboutContent is Control extCtrl)
                    {
                        p.Children.Add(_group(extCtrl, e.GetType()?.FullName ?? e.ToString() ?? ""));
                    }
                }
            }

            Sections = p;
        }
    }
}

public record AppAboutCommand() : SiloCommand("_About Silo...", Enabler.Always)
{
    protected override Task OnExecute(object? parameter)
    {
        App.RunSafe(() => AboutBox.Show());
        return Task.CompletedTask;
    }
}