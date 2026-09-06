using System.Collections;
using System.Collections.ObjectModel;
using System.Media;
using System.Resources;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Serilog;
using Silo.Commanding;
using Silo.Connectors;
using Silo.Contracts;
using Silo.Extensions;
using Silo.Model;
using Silo.Ui.Windows;
using Telefrag.Collections;
using Telefrag.Common;
using Telefrag.DI;
using Telefrag.Exceptions;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Silo;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application, IContainerHost
{
    private readonly FragTable<OpenSilo, string> _silos = [];
    protected ILogger Logger { get; } = Log.ForContext<App>();
    public ObservableCollection<Serilog.Events.LogEvent> Logs { get; } = [];
    public IEnumerable<OpenSilo> OpenSilos => _silos.Values;


    public Container Components { get; } = new(nameof(App));

    public static     AppCommand NewSiloCommand  => new NewSiloCommand();
    public static     AppCommand OpenSiloCommand => new OpenSiloCommand();
    public static new AppCommand ExitCommand     => new AppExitCommand();
    public static     AppCommand About           => new AppAboutCommand();


    public string GetLogs()
    {
        StringBuilder sb = new(Logs.Count * 10);
        foreach (Serilog.Events.LogEvent v in Logs)
        {
            sb.AppendLine($"{v.Timestamp} [{v.Level}] {v.RenderMessage()}");
        }

        return sb.ToString();
    }

    public IConnection? NewConnection()
    {
        if (ConnectionWindow.Modal() is IConnection c)
        {
            return c;
        }

        Status("New connection cancelled");
        return null;
    }

    /// <summary>
    /// Creates a new Silo file at a path chosen by the user via modal file save dialog.
    /// </summary>
    public OpenSilo? NewSilo()
    {
        var s = new SaveFileDialog()
                {
                    Title  = "Create Silo",
                    Filter = "(*.silo)|*.silo"
                };
        if (s.ShowDialog() is true)
        {
            var file = new SiloFile(s.FileName);
            if (OpenSilo.CreateFile(file) is OpenSilo c)
            {
                Logger.Information("Created new silo at '{path}'",
                                   c.File.FilePath);
                return c;
            }
        }

        return null;
    }

    public void ExportAs(string content, string filter, string? defaultFilename)
    {
        var s = new SaveFileDialog()
                {
                    Title    = "Export As",
                    Filter   = filter,
                    FileName = defaultFilename!
                };
        if (s.ShowDialog() is true)
        {
            System.IO.File.WriteAllText(s.FileName, content);
            string msg = "Wrote {n} bytes to file '{file}'";
            Logger.Debug(msg, content.Length, content);
            MessageBox.Show(msg, "Success", MessageBoxButton.OK,
                            MessageBoxImage.Information);
        }
    }

    public void ExportLogs() => ExportAs(Instance?.GetLogs(),
                                         "Log Files (*.log)|*.log", null);

    public static App? Instance => Application.Current as App;

    public OpenSilo? ActiveSilo
    {
        get;
        set
        {
            field = value;
            ActiveSiloChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler?           ActiveSiloChanged;
    public event EventHandler<OpenSilo>? SiloOpened;
    public event EventHandler<OpenSilo>? SiloOpening;
    public event EventHandler<OpenSilo>? SiloClosing;
    public event EventHandler?           SiloClosed;

    internal void NotifySiloOpening(object sender, OpenSilo o) =>
        SiloOpening?.Invoke(sender, o);

    internal void NotifySiloOpened(object sender, OpenSilo o) =>
        SiloOpened?.Invoke(sender, o);

    internal void NotifySiloClosing(object sender, OpenSilo o) =>
        SiloClosing?.Invoke(sender, o);

    internal void NotifySiloClosed(object sender, OpenSilo o) =>
        SiloClosed?.Invoke(sender, EventArgs.Empty);


    public bool Status(string text)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.Invoke(() => InterError(text));
        (MainWindow as MainWindow)?.StatusError.Content = text;
        return false;
    }

    public bool InterError(string error)
    {
        Beep();
        return Status(error);
    }

    public bool Beep()
    {
        try
        {
            SystemSounds.Exclamation.Play();
            return true;
        }
        catch
        {
            /* intentional */
        }

        return false;
    }

    private IDatabaseProvider _db => ActiveSilo?.Data;

    public bool InterAdd<TItem>(TItem item)
    {
        if (ActiveSilo is null)
            return
                InterError("No silo is active.  Create, open, or select a silo first.");

        if (Exists(item))
            return InterError("Error:  Item not found in silo.");

        // _validateItem(item);
        // _validateChild(item, null);
        return _db.AddItem(item);
    }

    public bool Exists<TItem>(TItem item) => _db.ItemExists(item);

    public bool Remove<TItem>(TItem item) => _db.RemoveItem(item.Required());

    public static Task<Credential?> NewCredential(OpenSilo? target)
    {
        target ??= Instance?.ActiveSilo ?? OpenSilo.OnlySilo ??
                   throw new
                       InvalidOperationException("Could not determine the target silo for the operation.");
        if (Ui.Ui.ModalModel(new Credential()) is Credential c)
        {
            if (Instance.InterAdd(c))
                return Task.FromResult(c);
        }

        return Task.FromResult<Credential?>(null);
    }

    public static TService Require<TService>() where TService : class
    {
        return Resolve<TService>() ??
               throw new
                   RequiredComponentMissingException($"Component '{typeof(TService)}' is required but is not a registered" +
                                                     $" service, component, or resource.");
    }

    public static TService? Resolve<TService>() where TService : class
    {
        return App.Instance?.Components?.Resolve<TService>();
    }

    public void Unhandled(Exception exception)
    {
        throw exception;
    }
}