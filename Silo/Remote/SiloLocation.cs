namespace Silo.Remote;

/// <summary>What kind of place a Silo comes from.</summary>
public enum SiloLocationKind
{
    /// <summary>A file on this computer (or a network share).</summary>
    Local,

    /// <summary>An http(s) URL; synced with plain HTTP (ETag on open, PUT on save).</summary>
    Http,

    /// <summary>A Git repository (git:// or ssh:// URL, or an http(s) URL ending in <c>.git</c>).</summary>
    Git
}

/// <summary>Classifies a command-line / dialog argument as a local path or a remote location.</summary>
public static class SiloLocation
{
    public static SiloLocationKind Classify(string target)
    {
        if (!TryParseRemote(target, out var uri)) return SiloLocationKind.Local;

        var scheme = uri.Scheme;
        if (scheme is "git" or "ssh" || uri.AbsolutePath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            return SiloLocationKind.Git;

        return SiloLocationKind.Http;
    }

    public static bool IsRemote(string target)
    {
        return Classify(target) != SiloLocationKind.Local;
    }

    /// <summary>True when <paramref name="target" /> is an absolute http, https, git or ssh URL (a drive path like <c>C:\x</c> is not).</summary>
    public static bool TryParseRemote(string? target, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(target) || !target.Contains("://", StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(target.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https" or "git" or "ssh")) return false;

        uri = parsed;
        return true;
    }
}
