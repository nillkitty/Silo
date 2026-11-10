using System.Reflection;
using System.Windows.Controls;
using Silo.Commanding;
using Silo.Model;
using Silo.Ui.Controls;

namespace Silo.Ui;

/// <summary>
///     Imterface for app components which contributes to the About Box
/// </summary>
public interface IAboutBox
{
    /// <summary>
    ///     Gets the content to be added to the About Box.
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
        /// <summary>The About text: the template resource with the version, authors, address, info, thanks and memorial lines filled in.</summary>
        internal string AboutText
        {
            get
            {
                if (App.Instance?.TryFindResource("AboutText")?.ToString() is not { } template) return "\n\nCant find about text resource!\n\n";

                string Part(string key) => App.Instance.TryFindResource(key)?.ToString()?.Trim() is { } s && s != "-" ? s : string.Empty;

                string version = System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                 ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";
                int plus = version.IndexOf('+'); // the build's commit hash

                try
                {
                    return string.Format(template, plus > 0 ? version[..plus] : version, Part("AboutAuthors"), Part("AboutUrl"), Part("AboutInfo"), Part("AboutThanks"), Part("AboutMemory"))
                                 .Replace("\n\n\n", "\n\n").Trim();
                }
                catch (FormatException)
                {
                    return template;
                }
            }
        }

        internal UIElement Content => new ScrollViewer
                                      {
                                          MaxHeight                     = 300.0,
                                          HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                                          VerticalScrollBarVisibility   = ScrollBarVisibility.Visible,
                                          Content                       = Sections
                                      };

        internal UIElement MainSection => _group(new TextBlock
                                                 {
                                                     Text                = AboutText,
                                                     TextWrapping        = TextWrapping.Wrap,
                                                     HorizontalAlignment = HorizontalAlignment.Stretch,
                                                     VerticalAlignment   = VerticalAlignment.Top,
                                                     Padding             = new Thickness(5.0),
                                                     Margin              = new Thickness(2.0),
                                                     MinHeight           = 32.0
                                                 }, "About Silo");

        internal StackPanel Sections { get; }

        private GroupBox _group(UIElement content, string header)
        {
            return new GroupBox
                   {
                       Content = content,
                       Header  = header,
                       Padding = new Thickness(5.0)
                   };
        }

        public AboutModel() : base(App.LocalConnection)
        {
            StackPanel p = new();
            p.Children.Add(MainSection);
            var ext = App.Resolve<IEnumerable<IAboutBox>>()?.ToList();
            if (ext is [..])
                foreach (var e in ext)
                    if (e?.AboutContent is Control extCtrl)
                        p.Children.Add(_group(extCtrl, e.GetType()?.FullName ?? e.ToString() ?? ""));

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