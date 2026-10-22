using Serilog;
using Silo.Extensions;
using Silo.Providers;

namespace Silo;

public static class UriExtensions
{
    extension(Uri uri)
    {
        public object? ResolvePublic()
        {
            return uri._resolve(UriContext.Public);
        }

        public object? ResolvePrivate()
        {
            return uri._resolve(UriContext.Private);
        }

        private object? _resolve(UriContext context)
        {
            var ur = App.RequireAny<IUriResolver>();
            ur = App.ResolveAll<IUriResolver>();
            foreach (var u in ur)
            {
                if (u is null) continue;

                var rr = u.Resolve(uri, context);
                if (rr is null)
                {
                    Log.Debug("Trying '{uri}' via '{resolver}'; result: no result", uri, u.GetType().ShortDisplayName());
                    continue;
                }

                Log.Debug("Trying '{uri}' via '{resolver}'; result: {obj}", uri, u.GetType().ShortDisplayName(), rr);

                return rr;
            }

            Log.Debug("Trying '{uri}' failed via {num} resolvers", uri, ur.Count);
            return null;
        }

        public Uri Append(params string[] newFragments)
        {
            ArgumentNullException.ThrowIfNull(uri);

            string rawUri = uri.OriginalString;
            if (newFragments == null || newFragments.Length == 0)
                return new Uri(rawUri, UriKind.RelativeOrAbsolute);

            // Separate existing fragment from the base URI (path + query)
            int    hashIndex        = rawUri.IndexOf('#');
            string baseUri          = hashIndex >= 0 ? rawUri.Substring(0, hashIndex) : rawUri;
            string existingFragment = hashIndex >= 0 ? rawUri.Substring(hashIndex + 1) : string.Empty;

            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(existingFragment)) sb.Append(existingFragment);

            foreach (string fragment in newFragments)
            {
                if (string.IsNullOrEmpty(fragment))
                    continue;

                // Trim leading '#' to avoid unintended double-hashes
                ReadOnlySpan<char> span = fragment.AsSpan().TrimStart('#');
                if (span.IsEmpty)
                    continue;

                if (sb.Length > 0) sb.Append('/');

                sb.Append(Uri.EscapeDataString(span.ToString()));
            }

            return sb.Length > 0 ? new Uri($"{baseUri}#{sb}", UriKind.RelativeOrAbsolute) : new Uri(baseUri, UriKind.RelativeOrAbsolute);
        }
    }
}