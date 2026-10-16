namespace Silo;

public static class UriExtensions
{
    extension(Uri uri)
    {
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