using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Silo.Drawio.Host;
using Silo.Drawio.Mx;
using Silo.Drawio.Options;
using Silo.Providers.Format;
using Telefrag.DI;

namespace Silo.Tools.TypeMap;

/// <summary>
///     Type Map: one draw.io diagram of an assembly's public API -- every public type grouped by namespace, the base classes and
///     interfaces each derives from, how each is registered with the container (DI services and their lifetimes), and the
///     summary, remarks and example from its XML docs or <c>[Description]</c>. Opens as a pane in the dock area.
/// </summary>
/// <remarks>
///     <para>
///         The toolbar picks the assembly (Silo's own loaded assemblies), narrows the map to the types whose full name contains
///         any of the filter words (separated by <c>;</c> or spaces), and turns the connector kinds, docs, members and the types
///         outside the map on or off. Hover a box for its full docs; Edit Data shows them as fields.
///     </para>
///     <para>
///         Docs come from the documentation file the build writes beside the assembly (<c>GenerateDocumentationFile</c>); see
///         <see cref="XmlDocs" />. The layout is in <see cref="TypeMapDiagramSource" />.
///     </para>
/// </remarks>
/// <example>
///     Filter <c>Drawio; ITool</c> with Members on maps the embedded draw.io host and every tool, each tool's box showing it is a
///     singleton registered as <c>ITool</c>.
/// </example>
[Singleton(typeof(ITool))]
public sealed class TypeMapTool : ToolBase
{
    public TypeMapTool() : base("typemap")
    {
        DisplayName = "Type Map";
        GroupName   = "Diagrams";
    }

    /// <inheritdoc />
    protected override bool UsesPane => true;

    /// <inheritdoc />
    protected override double PaneHeight => 560;

    /// <inheritdoc />
    protected override UIElement OnBuildContent()
    {
        var source = new TypeMapDiagramSource();
        var view   = new DrawioView(DrawioProfiles.TypeMap) { Title = "Type Map" };

        var assemblies = new ComboBox { MinWidth = 180, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var filter = new TextBox
                     {
                         MinWidth          = 220,
                         Margin            = new Thickness(4, 0, 8, 0),
                         VerticalAlignment = VerticalAlignment.Center,
                         ToolTip           = "Show only the types whose full name contains one of these words (separate them with ; or spaces). Enter applies."
                     };
        var status = new TextBlock { Opacity = 0.6, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        bool filling = false;

        void FillAssemblies()
        {
            filling = true;
            var selected = (assemblies.SelectedItem as ComboBoxItem)?.Tag as Assembly ?? source.Options.Assembly;
            assemblies.Items.Clear();
            foreach (var a in TypeMapModel.Assemblies())
                assemblies.Items.Add(new ComboBoxItem { Content = a.GetName().Name, Tag = a, IsSelected = a == selected });
            filling = false;
        }

        void Apply()
        {
            if ((assemblies.SelectedItem as ComboBoxItem)?.Tag is Assembly a) source.Options.Assembly = a;
            source.Options.Filters = filter.Text.Split([';', ' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            status.Text            = "Mapping…";
            source.Rebuild();
        }

        FillAssemblies();
        assemblies.SelectionChanged += (_, _) =>
                                       {
                                           if (!filling) Apply();
                                       };
        filter.KeyDown += (_, e) =>
                          {
                              if (e.Key != Key.Enter) return;
                              e.Handled = true;
                              Apply();
                          };

        var bar = new WrapPanel { Margin = new Thickness(6, 4, 6, 4) };
        DockPanel.SetDock(bar, Dock.Top);
        bar.Children.Add(assemblies);
        bar.Children.Add(new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(filter);
        bar.Children.Add(Toggle("Inheritance", source.Options.Inheritance, v => source.Options.Inheritance = v, Apply));
        bar.Children.Add(Toggle("Interfaces", source.Options.Interfaces, v => source.Options.Interfaces = v, Apply));
        bar.Children.Add(Toggle("DI services", source.Options.Registrations, v => source.Options.Registrations = v, Apply));
        bar.Children.Add(Toggle("Docs", source.Options.Docs, v => source.Options.Docs = v, Apply));
        bar.Children.Add(Toggle("Members", source.Options.Members, v => source.Options.Members = v, Apply));
        bar.Children.Add(Toggle("Outside types", source.Options.External, v => source.Options.External = v, Apply));
        bar.Children.Add(MakeButton("Refresh", () =>
                                               {
                                                   FillAssemblies();
                                                   Apply();
                                               }));
        bar.Children.Add(MakeButton("Save as file...", async () => await SaveAsync(view)));
        bar.Children.Add(status);

        source.Built += () => status.Dispatcher.BeginInvoke(() => status.Text = source.LastSummary);

        var root = new DockPanel();
        root.Children.Add(bar);
        root.Children.Add(view);

        DrawioDesign.Attach(root, view);
        view.Source = source;
        return root;
    }

    private static CheckBox Toggle(string text, bool initial, Action<bool> set, Action apply)
    {
        var box = new CheckBox { Content = text, IsChecked = initial, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        box.Click += (_, _) =>
                     {
                         set(box.IsChecked == true);
                         apply();
                     };
        return box;
    }

    private static Button MakeButton(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private static async Task SaveAsync(DrawioView view)
    {
        var drawio = Formats.Of<DrawioFormat>();
        var dlg = new SaveFileDialog
                  {
                      Filter   = drawio.Filter(),
                      FileName = drawio.FileName("type-map")
                  };
        if (dlg.ShowDialog() != true) return;

        try
        {
            await File.WriteAllTextAsync(dlg.FileName, await view.GetXmlAsync() ?? DrawioDocument.EmptyXml);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save diagram", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
