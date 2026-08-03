using System.Collections.ObjectModel;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Serilog;
using Silo.Connectors;
using Silo.Model;

namespace Silo;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected ILogger Logger { get; } = Log.ForContext<App>();
    public    ObservableCollection<Serilog.Events.LogEvent> Logs { get; } = [];


    public string GetLogs()
    {
        StringBuilder sb = new(Logs.Count * 10);
        foreach (Serilog.Events.LogEvent v in Logs)
        {
            sb.AppendLine($"{v.Timestamp} [{v.Level}] {v.RenderMessage()}");
        }

        return sb.ToString();
    }

    public IConnection NewConnection()
    {
    }

    /// <summary>
    /// Creates a new Silo file at a path chosen by the user via modal file save dialog.
    /// </summary>
    public OpenSilo NewSilo()
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

    public void ExportAs(string  content, string filter,
                         string? defaultFilename)
    {
        var s = new SaveFileDialog()
                {
                    Title    = "Export As",
                    Filter   = filter,
                    FileName = defaultFilename
                };
        if (s.ShowDialog() is true)
        {
            System.IO.File.WriteAllText(s.FileName, content);
            string msg = "Wrote {n} bytes to file '{file}'";
            Logger.Debug(msg, content.Length,
                         content);
            MessageBox.Show(msg, "Success", MessageBoxButton.OK,
                            MessageBoxImage.Information);
        }
    }

    public void ExportLogs() => ExportAs(Instance?.GetLogs(),
                                         "Log Files (*.log)|*.log",
                                         null);

    public static App? Instance => Application.Current as App;

    public OpenSilo ActiveSilo { get; set; }

    public static Task<Credential?> NewCredential(OpenSilo? target)
    {
        target ??= Instance?.ActiveSilo ?? OpenSilo.OnlySilo
                ?? throw new
                       InvalidOperationException("Could not determine the target silo for the operation.");
        if (Ui.ModalModel(new Credential()) is Credential c)
        {
            if (Add(c))
                return Task.FromResult(c);
        }

        return Task.FromResult<Credential?>(null);
    }
}