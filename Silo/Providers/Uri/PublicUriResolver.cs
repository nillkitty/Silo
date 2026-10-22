using System.Data;
using Serilog;
using Silo.Connectors;
using Silo.Connectors.Web;
using Silo.Platform;
using Telefrag.DI;

namespace Silo.Providers;

[Transient(typeof(IUriResolver))]
public class PublicUriResolver : IUriResolver
{
    private PrivateUriResolver _priv;

    public object? Resolve(Uri uri, UriContext context)
    {
        uri.Required();
        if (context != UriContext.Public) return null;
        var ui = App.Resolve<IUrlInfo>();

        if (ui is null)
        {
            Log.Warning("{component} cannot find a registered IUrlInfo service so it is " + "failing to resolve public URL '{uri}'",
                        nameof(PublicUriResolver), uri);
            return null;
        }

        var i = ui.GetInfo(uri);
        if (i is null)
        {
            Log.Warning("{component} cannot find a registered scheme '{scheme}' for public URL '{uri}' being resolved",
                        nameof(PublicUriResolver), uri.Scheme, uri);
            return null;
        }

        if (i.IsOurs)
        {
            // the URL handler is registered to silo.exe

            Log.Verbose("Resolved URL '{uri}' to our own app", uri);
            _priv ??= new PrivateUriResolver();

            return _priv.Resolve(uri, UriContext.Navigation);
        }

        if (i.IsWebUrl)
            // the URL handler is registered to a browser
            return new WebDestination(i.CanonicalUri ?? uri, i.HandlerCommand);

        // the URL handler is registered to something else
        return new AppDestination(i.HandlerCommand, i.CanonicalUri ?? uri);
    }
}

public record AppDestination(Uri Url, string HandlerCommand) : IDestination
{
    public string Command { get; }

    public Uri Uri
    {
        get => Url;
        set => throw new ReadOnlyException("This destination object is read-only");
    }

    public AppDestination(string handlerCommand, Uri url) : this(url, handlerCommand)
    {
        handlerCommand.Required();
        // the command as the shell would run it: the program, and its arguments with the address where the registration puts it
        Command = ShellCommand.Split(handlerCommand) is { } split ? $"\"{split.Program}\" {ShellCommand.Substitute(split.Arguments, Url)}".TrimEnd() : handlerCommand.Replace("%1", Url.ToString());
    }
}