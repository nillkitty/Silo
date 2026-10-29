using Microsoft.Win32;

namespace Silo.Providers.Script;

public static class AppsManager
{
    private static readonly string[] ExecutableExtensions = { ".exe", ".cmd", ".bat" };

    /// <summary>
    ///     Resolves the absolute path to an executable by querying App Paths (Registry),
    ///     system PATH environment variables, and active user environment roots.
    /// </summary>
    /// <param name="appName">The name or binary alias (e.g. "python", "git", "code").</param>
    /// <returns>Absolute path to the executable if located; otherwise null.</returns>
    public static string? GetExePathFor(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            return null;

        var candidates = GetNameVariants(appName);

        // 1. Query Registry: App Paths (HKCU & HKLM, 64-bit and 32-bit views)
        foreach (var name in candidates)
        {
            var regPath = QueryAppPathsRegistry(name);
            if (regPath != null && File.Exists(regPath))
                return Path.GetFullPath(regPath);
        }

        // 2. Query PATH Environment Variable (Process, User, and Machine targets)
        var pathEntries = GetMergedPathEnvironmentEntries();
        foreach (var dir in pathEntries)
        {
            foreach (var name in candidates)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
        }

        // 3. Fallback: Query App-Specific Root Registry Keys (e.g., PythonCore)
        if (appName.Equals("python", StringComparison.OrdinalIgnoreCase))
        {
            var pythonPath = QueryPythonCoreRegistry();
            if (pythonPath != null && File.Exists(pythonPath))
                return Path.GetFullPath(pythonPath);
        }

        return null;
    }

    private static IEnumerable<string> GetNameVariants(string appName)
    {
        var hasExt = Path.HasExtension(appName);
        if (hasExt)
        {
            yield return appName;
            yield break;
        }

        yield return appName;
        foreach (var ext in ExecutableExtensions)
            yield return appName + ext;
    }

    private static string? QueryAppPathsRegistry(string executableName)
    {
        const string subKeyBase  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
        var          relativeKey = subKeyBase + executableName;

        var hives = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key     = baseKey.OpenSubKey(relativeKey);
                if (key == null)
                    continue;

                // Default unnamed value contains the full binary path
                if (key.GetValue(null) is string defaultPath && !string.IsNullOrWhiteSpace(defaultPath))
                {
                    var cleanPath = CleanRegistryPath(defaultPath);
                    if (File.Exists(cleanPath))
                        return cleanPath;
                }
            }
        }

        return null;
    }

    private static string? QueryPythonCoreRegistry()
    {
        const string relativePath = @"SOFTWARE\Python\PythonCore";
        var          hives        = new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        var          views        = new[] { RegistryView.Registry64, RegistryView.Registry32 };

        foreach (var hive in hives)
        {
            foreach (var view in views)
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var coreKey = baseKey.OpenSubKey(relativePath);
                if (coreKey == null)
                    continue;

                foreach (var version in coreKey.GetSubKeyNames().OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase))
                {
                    using var installPathKey = coreKey.OpenSubKey($@"{version}\InstallPath");
                    if (installPathKey == null)
                        continue;

                    // Standard Python registers ExecutablePath, or defaults to directory
                    if (installPathKey.GetValue("ExecutablePath") is string exePath && File.Exists(exePath))
                        return exePath;

                    if (installPathKey.GetValue(null) is string dirPath)
                    {
                        var candidate = Path.Combine(CleanRegistryPath(dirPath), "python.exe");
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
        }

        return null;
    }

    private static HashSet<string> GetMergedPathEnvironmentEntries()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var targets = new[]
                      {
                          EnvironmentVariableTarget.Process,
                          EnvironmentVariableTarget.User,
                          EnvironmentVariableTarget.Machine
                      };

        foreach (var target in targets)
        {
            var raw = Environment.GetEnvironmentVariable("PATH", target);
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var segments = raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var segment in segments)
            {
                var expanded = Environment.ExpandEnvironmentVariables(segment);
                if (Directory.Exists(expanded))
                    paths.Add(expanded);
            }
        }

        return paths;
    }

    private static string CleanRegistryPath(string path)
    {
        var trimmed = path.Trim().Trim('\"');
        return Environment.ExpandEnvironmentVariables(trimmed);
    }
}