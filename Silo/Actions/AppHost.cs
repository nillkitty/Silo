using System.IO;
using System.Windows.Threading;
using Silo.Commanding;
using Silo.Model;

namespace Silo.Actions;

/// <summary>The running WPF app as seen by the App actions. Everything is done on the UI thread, whichever thread asks.</summary>
internal sealed class AppHost(App app) : IAppActionHost
{
    public Task SaveAllAsync()
    {
        return OnUi(async () =>
                    {
                        foreach (var silo in OpenSilo.OpenSilos.Where(s => s.IsDirty).ToList())
                            await silo.SaveAsync(false);
                    });
    }

    public Task<string?> OpenSiloAsync(string path, bool forceReopen)
    {
        return OnUi(async () =>
                    {
                        if (forceReopen && OpenSilo.OpenSilos.FirstOrDefault(o => SamePath(o.FilePath, path)) is { } open)
                            await open.CloseAsync(false);

                        var opened = await OpenSiloCommand.OpenPathAsync(path);
                        return opened?.FilePath ?? throw new InvalidOperationException($"'{path}' could not be opened.");
                    });
    }

    public Task<string?> NewSiloAsync(string path)
    {
        return OnUi(() =>
                    {
                        var full = Path.GetFullPath(path);
                        if (File.Exists(full)) throw new IOException($"'{full}' already exists.");
                        return Task.FromResult<string?>(OpenSilo.CreateFile(new SiloFile(full)).FilePath);
                    });
    }

    public Task ExitAsync(bool saveChanges)
    {
        return OnUi(async () =>
                    {
                        if (saveChanges)
                            foreach (var silo in OpenSilo.OpenSilos.Where(s => s.IsDirty).ToList())
                                await silo.SaveAsync(false);

                        foreach (var silo in OpenSilo.OpenSilos.ToList())
                            await silo.CloseAsync(false);

                        app.Shutdown(0);
                        return (object?) null;
                    });
    }

    private static bool SamePath(string? a, string b)
    {
        return a is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }

    private Task OnUi(Func<Task> work)
    {
        return app.Dispatcher.InvokeAsync(work).Task.Unwrap();
    }

    private Task<T> OnUi<T>(Func<Task<T>> work)
    {
        return app.Dispatcher.InvokeAsync(work).Task.Unwrap();
    }
}
