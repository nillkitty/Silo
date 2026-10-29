using System.Net.Http;
using Telefrag.DI;

namespace Silo.Connectors.Web;

[Singleton(typeof(IHttpClientFactory))]
public class HttpClientFactory : IHttpClientFactory
{
    public static List<WeakReference<HttpClient>> Clients { get; } = [];
    public static HttpClientHandler               Handler { get; } = new();

    public HttpClient CreateClient(string name)
    {
        var c = new HttpClient(Handler);
        Clients.RemoveAll(x => !x.TryGetTarget(out _));
        Clients.Add(new WeakReference<HttpClient>(c));
        return c;
    }
}