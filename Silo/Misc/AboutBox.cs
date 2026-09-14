using System.Windows;
using System.Windows.Controls;
using Silo.Commanding;
using Silo.Model;
using Silo.UI;
using Silo.Ui.Controls;

namespace Silo;

/// <summary>
/// Imterface for app components which contributes to the About Box
/// </summary>
public interface IAboutBox
{
    /// <summary>
    /// Gets the content to be added to the About Box.
    /// </summary>
    Control AboutContent { get; }
}

public class ContentWindow : Window
{
    public ContentWindow(UIElement content)
    {
        Content = content.Required();
        var hg = new HunterGatherer<IWindowChrome>(content);
        hg.GatherAndApplyTo(this);
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


    [ModalDialog]
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