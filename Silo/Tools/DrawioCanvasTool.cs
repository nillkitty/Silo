using System.Linq;
using System.Threading;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog;
using Diagram = Silo.DbModel.Global.Diagram;
using Silo.Drawio;
using Silo.Model;
using Telefrag.DI;

namespace Silo.Tools;

/// <summary>
///     The free-form canvas: a full draw.io editor for design-world sketches, drawings and notes. Diagrams are stored
///     inside the active Silo document (when it can hold them -- see <see cref="IDiagramStore" />) and can also be
///     opened from / saved to ordinary <c>.drawio</c> files. With no writable Silo open the canvas still works as an
///     unsaved scratch page.
/// </summary>
/// <remarks>
///     The pane uses the <see cref="DrawioProfiles.Canvas" /> profile, so it is as unrestricted as the profile allows;
///     Ctrl+right-click -> "Diagram options..." at Author level changes that for the profile or for just the open diagram
///     (a per-diagram change is stored with the diagram).
/// </remarks>
[Singleton(typeof(ITool))]
public class DrawioCanvasTool : ToolBase
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private readonly ComboBox _picker = new() { MinWidth = 180, DisplayMemberPath = nameof(Diagram.Name) };
    private readonly TextBlock _status = new() { Opacity = 0.6, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _saveTimer;

    private DrawioView? _view;
    private IDiagramStore? _store;      // the store of the Silo the picker was last filled from (not "whatever is active now")
    private OpenSilo?      _hookedSilo; // the Silo whose SaveHooks currently contain FlushAsync
    private Diagram?    _current;
    private bool        _loading;
    private bool        _subscribed;
    private int?        _pendingSelect; // a diagram the Explorer asked for before the canvas was built
    private bool        _raising;       // true while this tool itself raises DiagramEvents.Changed

    /// <summary>The (single) canvas tool, once it exists; the Explorer opens diagrams through it.</summary>
    public static DrawioCanvasTool? Current { get; private set; }

    /// <summary>The canvas opens as a pane in the dock area, which can be dragged out into its own window.</summary>
    protected override bool UsesPane => true;

    public DrawioCanvasTool() : base("drawiocanvas")
    {
        DisplayName = "Diagram Canvas";
        GroupName   = "Diagrams";
        Current     = this;
        DiagramEvents.Changed += OnDiagramsChanged;

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) =>
                           {
                               _saveTimer.Stop();
                               _ = SaveNowAsync();
                           };
    }

    private IDiagramStore? Store => App.Instance?.ActiveSilo is { IsWritable: true } s ? s.Data as IDiagramStore : null;

    protected override UIElement OnBuildContent()
    {
        _view = new DrawioView(DrawioProfiles.Canvas);
        _view.DocumentChanged += (_, _) => QueueSave();
        _view.OverrideChanged += (_, _) => QueueSave();

        var root = new DockPanel();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
        DockPanel.SetDock(bar, Dock.Top);

        _picker.SelectionChanged += async (_, _) =>
                                    {
                                        if (_loading || _picker.SelectedItem is not Diagram d || d.Id == _current?.Id) return;
                                        await FlushAsync();
                                        await OpenAsync(d);
                                    };

        bar.Children.Add(_picker);
        bar.Children.Add(MakeButton("New", async () => await NewAsync()));
        bar.Children.Add(MakeButton("Rename", Rename));
        bar.Children.Add(MakeButton("Delete", Delete));
        bar.Children.Add(MakeButton("Open file...", async () => await ImportAsync()));
        bar.Children.Add(MakeButton("Save as file...", async () => await ExportFileAsync()));
        bar.Children.Add(MakeButton("Save Silo", () => App.Instance?.ActiveSilo?.Commands.SaveSilo.Execute(null)));
        bar.Children.Add(_status);

        root.Children.Add(bar);
        root.Children.Add(_view);

        DrawioDesign.Attach(root, _view);

        // Leaving the pane must not lose an edit that is still waiting for the save timer.
        root.Unloaded += async (_, _) => await FlushAsync();

        if (!_subscribed && App.Instance is { } app)
        {
            _subscribed             =  true;
            app.ActiveSiloChanged += async (_, _) =>
                                     {
                                         await FlushAsync();
                                         HookSilo();
                                         await RefreshListAsync(null);
                                     };
        }

        HookSilo();

        _ = RefreshListAsync(null);
        return root;
    }

    /// <summary>Makes sure saving (or closing) the active Silo first flushes this pane's pending edit into it.</summary>
    private void HookSilo()
    {
        var silo = App.Instance?.ActiveSilo;
        if (ReferenceEquals(silo, _hookedSilo)) return;

        _hookedSilo?.SaveHooks.Remove(FlushAsync);
        _hookedSilo = silo;
        silo?.SaveHooks.Add(FlushAsync);
    }

    private static Button MakeButton(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    // ---- list / open -----------------------------------------------------------------------------

    private void Raise()
    {
        _raising = true;
        try
        {
            DiagramEvents.RaiseChanged();
        }
        finally
        {
            _raising = false;
        }
    }

    /// <summary>Shows the canvas (opening its pane if needed) on the diagram <paramref name="id" /> of the active Silo.</summary>
    public async void ShowDiagram(int id)
    {
        try
        {
            if (_view is null)
            {
                // the first fill of the picker, when the content is built, selects it
                _pendingSelect = id;
                Show();
                return;
            }

            Show();

            await FlushAsync();
            await RefreshListAsync(id);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening the diagram {id} failed", id);
        }
    }

    /// <summary>Someone else (the Explorer) created, renamed or deleted a diagram: keep the picker and the open diagram in step.</summary>
    private void OnDiagramsChanged(object? sender, EventArgs e)
    {
        if (_raising || _view is null) return;

        var store = Store;
        _store = store;
        var list = store?.ListDiagrams().Where(d => d.Kind == "canvas").ToList() ?? [];

        _loading = true;
        try
        {
            _picker.ItemsSource = list;
            _picker.IsEnabled   = store is not null;
        }
        finally
        {
            _loading = false;
        }

        if (_current is null) return;

        var latest = list.FirstOrDefault(d => d.Id == _current.Id);
        if (latest is null)
        {
            // the open diagram was deleted: nothing of it is left to save
            _saveTimer.Stop();
            _current = null;
            _ = OpenAsync(list.FirstOrDefault());
            return;
        }

        _current    = latest;
        _view.Title = latest.Name;
        _loading    = true;
        try
        {
            _picker.SelectedItem = latest;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task RefreshListAsync(int? select)
    {
        select         ??= _pendingSelect;
        _pendingSelect =   null;

        var store = Store;
        _store = store;
        var list  = store?.ListDiagrams().Where(d => d.Kind == "canvas").ToList() ?? [];

        _loading = true;
        try
        {
            _picker.ItemsSource = list;
            _picker.IsEnabled   = store is not null;
        }
        finally
        {
            _loading = false;
        }

        if (store is null)
        {
            _status.Text = "Not saved to a Silo (no writable Silo is open)";
            _current     = null;
            await OpenAsync(null);
            return;
        }

        Diagram? target = list.FirstOrDefault(d => d.Id == select) ?? list.FirstOrDefault();
        if (target is null)
            await NewAsync();
        else
            await OpenAsync(target);
    }

    private async Task OpenAsync(Diagram? d)
    {
        if (_view is null) return;

        _loading = true;
        try
        {
            _current    = d;
            _view.Title = d?.Name;

            if (d is null)
            {
                _view.Override = null;
                _view.SetProfile(DrawioProfiles.Canvas);
                await _view.LoadXmlAsync(DrawioDocument.EmptyXml);
            }
            else
            {
                _view.SetProfile(d.Profile);
                _view.Override = d.OptionsJson is { Length: > 0 } json ? DrawioOptions.FromJson(json) : null;
                await _view.LoadXmlAsync(d.Xml);
                _picker.SelectedItem = (_picker.ItemsSource as IEnumerable<Diagram>)?.FirstOrDefault(x => x.Id == d.Id);
                _status.Text         = $"Saved {d.Modified.ToLocalTime():t}";
            }
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- save ------------------------------------------------------------------------------------

    private void QueueSave()
    {
        if (_loading || Store is null) return;

        _saveTimer.Stop();
        _saveTimer.Start();
        _status.Text = "Unsaved changes...";
    }

    private async Task FlushAsync()
    {
        if (!_saveTimer.IsEnabled) return;

        _saveTimer.Stop();
        await SaveNowAsync();
    }

    private async Task SaveNowAsync()
    {
        var store = _store;
        if (store is null || _view is null || _current is null) return;

        try
        {
            await _view.GetXmlAsync();
            string xml  = _view.UserXml ?? DrawioDocument.EmptyXml;
            string? opt = _view.Override?.ToJson();

            if (xml == _current.Xml && opt == _current.OptionsJson && _view.Profile.Name == _current.Profile) return;

            // start from what the store holds now, so a rename made elsewhere (the Explorer) is not undone by this save
            var latest = store.GetDiagram(_current.Id);
            if (latest is null) return; // deleted elsewhere

            var edited = latest.Clone();
            edited.Xml         = xml;
            edited.OptionsJson = opt;
            edited.Profile     = _view.Profile.Name;
            edited.Modified    = DateTime.UtcNow;
            _current           = store.SaveDiagram(edited);
            _status.Text = $"Saved {_current.Modified.ToLocalTime():t}";
            Raise();
            _view.SetStatus(null, false); // clear draw.io's "unsaved" marker; the Silo file itself is written by Save Silo
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Saving the diagram failed");
            _status.Text = "Save failed - see log";
        }
    }

    // ---- toolbar actions -------------------------------------------------------------------------

    private async Task NewAsync()
    {
        var store = Store;
        if (store is null)
        {
            await OpenAsync(null);
            return;
        }

        await FlushAsync();

        string? name = Prompt("New diagram", "Name:", $"Diagram {store.ListDiagrams().Count + 1}");
        if (name is null) return;

        var d = store.SaveDiagram(new Diagram { Name = name, Kind = "canvas", Profile = DrawioProfiles.Canvas, Xml = DrawioDocument.EmptyXml });
        Raise();
        await RefreshListAsync(d.Id);
    }

    private async void Rename()
    {
        var store = Store;
        if (store is null || _current is null) return;

        string? name = Prompt("Rename diagram", "Name:", _current.Name);
        if (name is null) return;

        await FlushAsync();
        var renamed = _current.Clone();
        renamed.Name     = name;
        renamed.Modified = DateTime.UtcNow;
        var updated = store.SaveDiagram(renamed);
        Raise();
        await RefreshListAsync(updated.Id);
    }

    private async void Delete()
    {
        var store = Store;
        if (store is null || _current is null) return;

        if (MessageBox.Show($"Delete the diagram \"{_current.Name}\" from this Silo?", "Delete diagram", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _saveTimer.Stop();
        store.DeleteDiagram(_current.Id);
        Raise();
        await RefreshListAsync(null);
    }

    private async Task ImportAsync()
    {
        if (_view is null) return;

        var dlg = new OpenFileDialog { Filter = "draw.io diagrams (*.drawio;*.xml)|*.drawio;*.xml|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;

        string xml;
        try
        {
            xml = await File.ReadAllTextAsync(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Open diagram", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Normalise (also unpacks legacy compressed diagrams) before it goes anywhere.
        xml = DrawioDocument.Parse(xml).ToXml();

        if (Store is { } store)
        {
            await FlushAsync();
            var d = store.SaveDiagram(new Diagram
                                      {
                                          Name    = Path.GetFileNameWithoutExtension(dlg.FileName),
                                          Kind    = "canvas",
                                          Profile = DrawioProfiles.Canvas,
                                          Xml     = xml
                                      });
            Raise();
            await RefreshListAsync(d.Id);
        }
        else
        {
            await _view.LoadXmlAsync(xml);
        }
    }

    private async Task ExportFileAsync()
    {
        if (_view is null) return;

        var dlg = new SaveFileDialog
                  {
                      Filter   = "draw.io diagram (*.drawio)|*.drawio",
                      FileName = (_current?.Name ?? "diagram") + ".drawio"
                  };
        if (dlg.ShowDialog() != true) return;

        await _view.GetXmlAsync();
        try
        {
            await File.WriteAllTextAsync(dlg.FileName, _view.UserXml ?? DrawioDocument.EmptyXml);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save diagram", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>A one-line text prompt; returns null if cancelled or empty.</summary>
    private static string? Prompt(string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 260, Margin = new Thickness(0, 4, 0, 10) };
        var ok  = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0) };
        var no  = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 3, 14, 3) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(no);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var win = new Window
                  {
                      Title                 = title,
                      Content               = panel,
                      SizeToContent         = SizeToContent.WidthAndHeight,
                      ResizeMode            = ResizeMode.NoResize,
                      WindowStartupLocation = WindowStartupLocation.CenterOwner,
                      ShowInTaskbar         = false,
                      Owner                 = Application.Current?.MainWindow
                  };
        ok.Click += (_, _) => win.DialogResult = true;
        box.SelectAll();
        box.Focus();

        return win.ShowDialog() == true && box.Text.Trim() is { Length: > 0 } t ? t : null;
    }
}
