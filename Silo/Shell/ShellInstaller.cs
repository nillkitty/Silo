using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json;
using Serilog;

namespace Silo.Shell;

public enum ShellWriteKind
{
    /// <summary>The package made this registry key (it did not exist).</summary>
    KeyCreated,

    /// <summary>The package set this registry value; <see cref="ShellWrite.Existed" /> says whether it had one before.</summary>
    ValueSet,

    /// <summary>The package wrote this file (or shortcut).</summary>
    FileWritten
}

/// <summary>One thing an installed Shell package wrote, and what was there before: enough to take it away again.</summary>
public sealed class ShellWrite
{
    public DateTime WhenUtc { get; set; }

    public ShellWriteKind Kind { get; set; }

    /// <summary>The key from its hive ("HKCU\Software\Classes\x"), or the file's full path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The value's name (empty is the key's default value).</summary>
    public string? Name { get; set; }

    /// <summary>Whether the value or file was there before the package wrote it.</summary>
    public bool Existed { get; set; }

    /// <summary>The registry kind of what was there before (a <see cref="RegistryValueKind" /> name).</summary>
    public string? PreviousKind { get; set; }

    /// <summary>What was there before: the text, the number, or base64 for bytes.</summary>
    public string? PreviousData { get; set; }

    public override string ToString()
    {
        string what = Kind switch
                      {
                          ShellWriteKind.KeyCreated  => "key   " + Path,
                          ShellWriteKind.ValueSet    => "value " + Path + " : " + (string.IsNullOrEmpty(Name) ? "(Default)" : Name) + (Existed ? "  (replaced one)" : string.Empty),
                          _                          => "file  " + Path + (Existed ? "  (replaced one)" : string.Empty)
                      };
        return WhenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + what;
    }
}

/// <summary>A Shell package that is installed, as recorded in the Silo's own key in the registry.</summary>
public sealed class InstalledShellPackage
{
    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string? Description { get; init; }

    public DateTime InstalledUtc { get; init; }

    public ShellScope Scope { get; init; }

    /// <summary>Where it was installed from (a file, a Silo item, a Lab).</summary>
    public string? Source { get; init; }

    /// <summary>"Installed", or "Partly removed" when an uninstall could not take everything away.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>The package as it was installed.</summary>
    public ShellPackage? Package { get; init; }

    public IReadOnlyList<ShellWrite> Writes { get; init; } = [];
}

/// <summary>What an uninstall did.</summary>
/// <param name="Removed">How many writes were undone.</param>
/// <param name="Left">Keys left because something else is in them now.</param>
/// <param name="Failed">What could not be undone (the record keeps them, so uninstall can be tried again).</param>
public sealed record ShellUninstallResult(int Removed, IReadOnlyList<string> Left, IReadOnlyList<string> Failed)
{
    public bool Complete => Failed.Count == 0;
}

public sealed class ShellPackageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
///     Installs Shell packages and takes them away again. What an install writes is recorded as it is written under
///     <c>HKCU\Software\Silo\ShellPackages\&lt;name&gt;</c> (when, what, and what was there before), so uninstall undoes exactly that, newest first.
/// </summary>
public static class ShellInstaller
{
    public const string TrackingRoot = @"Software\Silo\ShellPackages";

    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    // ---- what is installed ---------------------------------------------------------------------

    public static IReadOnlyList<InstalledShellPackage> List()
    {
        var list = new List<InstalledShellPackage>();
        using var root = Registry.CurrentUser.OpenSubKey(TrackingRoot);
        if (root is null) return list;

        foreach (string name in root.GetSubKeyNames())
            if (Find(name) is { } package)
                list.Add(package);

        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static InstalledShellPackage? Find(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(TrackingRoot + "\\" + name);
        if (key is null) return null;

        ShellPackage? package = null;
        if (key.GetValue("Package") is string json && ShellJson.FromJson(json, out var read, out _)) package = read;

        var writes = new List<ShellWrite>();
        using (var w = key.OpenSubKey("Writes"))
            if (w is not null)
                foreach (string n in w.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
                    if (w.GetValue(n) is string text && JsonConvert.DeserializeObject<ShellWrite>(text) is { } write)
                        writes.Add(write);

        DateTime.TryParse(key.GetValue("InstalledUtc") as string, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when);
        Enum.TryParse(key.GetValue("Scope") as string, out ShellScope scope);
        return new InstalledShellPackage
               {
                   Name         = name,
                   Version      = key.GetValue("Version") as string ?? string.Empty,
                   Description  = key.GetValue("Description") as string,
                   InstalledUtc = when,
                   Scope        = scope,
                   Source       = key.GetValue("Source") as string,
                   Status       = key.GetValue("Status") as string ?? "Installed",
                   Package      = package,
                   Writes       = writes
               };
    }

    // ---- install -------------------------------------------------------------------------------

    /// <summary>
    ///     Installs the package (replacing an installed one of the same name: that is uninstalled first). Everything written is recorded; if
    ///     something fails, what was written is taken away again and a <see cref="ShellPackageException" /> says why.
    /// </summary>
    public static InstalledShellPackage Install(ShellPackage package, string? source = null)
    {
        if (ShellCheck.Validate(package).Where(p => p.Severity == ShellSeverity.Error).ToList() is { Count: > 0 } errors)
            throw new ShellPackageException("The package cannot be installed:\n" + string.Join('\n', errors.Select(e => "  " + e.Text)));

        if (package.Scope == ShellScope.AllUsers && !IsElevated)
            throw new ShellPackageException("A package for all users writes to the whole machine: run Silo as an administrator, or make the package for the current user.");

        IReadOnlyList<ShellOp> ops;
        try
        {
            ops = ShellCompiler.Compile(package);
        }
        catch (InvalidOperationException ex)
        {
            throw new ShellPackageException(ex.Message, ex);
        }

        if (Find(package.Name) is not null && !Uninstall(package.Name).Complete)
            throw new ShellPackageException($"{package.Name} is already installed and the old one could not be taken away completely.");

        string trackKey = TrackingRoot + "\\" + package.Name;
        try
        {
            using (var track = Registry.CurrentUser.CreateSubKey(trackKey))
            {
                track.SetValue("Version", package.Version);
                track.SetValue("Description", package.Description ?? string.Empty);
                track.SetValue("InstalledUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                track.SetValue("Scope", package.Scope.ToString());
                track.SetValue("Source", source ?? string.Empty);
                track.SetValue("Status", "Installing");
                track.SetValue("Package", ShellJson.ToJson(package));
            }

            using (var writes = Registry.CurrentUser.CreateSubKey(trackKey + @"\Writes"))
            {
                int n = 0;
                foreach (var op in ops)
                foreach (var write in Apply(op))
                    writes.SetValue((++n).ToString("D5", CultureInfo.InvariantCulture), JsonConvert.SerializeObject(write));
            }

            using (var track = Registry.CurrentUser.OpenSubKey(trackKey, true))
                track!.SetValue("Status", "Installed");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Installing the Shell package {Package} failed; taking back what was written", package.Name);
            try
            {
                Uninstall(package.Name);
            }
            catch (Exception undo)
            {
                Log.Warning(undo, "Taking back the Shell package {Package} failed", package.Name);
            }

            throw new ShellPackageException($"Installing {package.Name} failed: {ex.Message}", ex);
        }

        Notify();
        return Find(package.Name)!;
    }

    private static IEnumerable<ShellWrite> Apply(ShellOp op)
    {
        switch (op.Kind)
        {
            case ShellOpKind.Key:
                foreach (var w in EnsureKey(op.Path)) yield return w;
                break;
            case ShellOpKind.Value:
                foreach (var w in SetValue(op)) yield return w;
                break;
            case ShellOpKind.File:
                yield return WriteFile(Environment.ExpandEnvironmentVariables(op.Path), Encoding.UTF8.GetBytes(op.Data ?? string.Empty));
                break;
            case ShellOpKind.Shortcut:
                yield return WriteShortcut(op);
                break;
        }
    }

    private static IEnumerable<ShellWrite> EnsureKey(string path)
    {
        var (root, prefix, sub) = Split(path);
        using (root)
        {
            string current = string.Empty;
            foreach (string part in sub.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.Length == 0 ? part : current + "\\" + part;
                using (var existing = root.OpenSubKey(current))
                {
                    if (existing is not null) continue;
                }

                root.CreateSubKey(current).Dispose();
                yield return new ShellWrite { WhenUtc = DateTime.UtcNow, Kind = ShellWriteKind.KeyCreated, Path = prefix + "\\" + current };
            }
        }
    }

    private static IEnumerable<ShellWrite> SetValue(ShellOp op)
    {
        foreach (var w in EnsureKey(op.Path)) yield return w;

        var (root, _, sub) = Split(op.Path);
        using (root)
        using (var key = root.OpenSubKey(sub, true) ?? throw new ShellPackageException("Could not open " + op.Path + " to write to it."))
        {
            string name = op.Name ?? string.Empty;
            var write = new ShellWrite { Kind = ShellWriteKind.ValueSet, Path = op.Path, Name = name };
            if (key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                var kind = key.GetValueKind(name);
                write.Existed      = true;
                write.PreviousKind = kind.ToString();
                write.PreviousData = Encode(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), kind);
            }

            switch (op.ValueKind)
            {
                case ShellValueKind.DWord:
                    key.SetValue(name, int.Parse(op.Data ?? "0", CultureInfo.InvariantCulture), RegistryValueKind.DWord);
                    break;
                case ShellValueKind.ExpandString:
                    key.SetValue(name, op.Data ?? string.Empty, RegistryValueKind.ExpandString);
                    break;
                default:
                    key.SetValue(name, op.Data ?? string.Empty, RegistryValueKind.String);
                    break;
            }

            write.WhenUtc = DateTime.UtcNow;
            yield return write;
        }
    }

    private static ShellWrite WriteFile(string path, byte[] bytes, Action<string>? make = null)
    {
        var write = new ShellWrite { Kind = ShellWriteKind.FileWritten, Path = path };
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 1_000_000) throw new ShellPackageException($"{path} already exists and is too big to keep a copy of; it is not overwritten.");

            write.Existed      = true;
            write.PreviousData = Convert.ToBase64String(File.ReadAllBytes(path));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (make is null) File.WriteAllBytes(path, bytes);
        else make(path);

        write.WhenUtc = DateTime.UtcNow;
        return write;
    }

    private static ShellWrite WriteShortcut(ShellOp op)
    {
        string path = Environment.ExpandEnvironmentVariables(op.Path);
        return WriteFile(path, [], p =>
                                   {
                                       var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new ShellPackageException("Windows Script Host is not available to make shortcuts with.");
                                       dynamic shell = Activator.CreateInstance(type)!;
                                       try
                                       {
                                           dynamic link = shell.CreateShortcut(p);
                                           try
                                           {
                                               link.TargetPath = Environment.ExpandEnvironmentVariables(op.Data ?? string.Empty);
                                               link.Arguments  = op.Extra ?? string.Empty;
                                               link.Save();
                                           }
                                           finally
                                           {
                                               Marshal.ReleaseComObject(link);
                                           }
                                       }
                                       finally
                                       {
                                           Marshal.ReleaseComObject(shell);
                                       }
                                   });
    }

    // ---- uninstall -----------------------------------------------------------------------------

    /// <summary>
    ///     Takes away everything the package wrote, newest first, putting back what was there before. A key that something else has content in now is left.
    ///     When something cannot be undone the record keeps just that, so uninstall can be tried again.
    /// </summary>
    public static ShellUninstallResult Uninstall(string name)
    {
        var installed = Find(name) ?? throw new ShellPackageException($"{name} is not installed.");
        if (installed.Scope == ShellScope.AllUsers && !IsElevated)
            throw new ShellPackageException($"{name} was installed for all users: run Silo as an administrator to uninstall it.");

        int removed = 0;
        var left = new List<string>();
        var failed = new List<ShellWrite>();
        var failures = new List<string>();
        foreach (var write in installed.Writes.Reverse())
            try
            {
                if (Undo(write) is { } note) left.Add(note);
                removed++;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Undoing {Write} of the Shell package {Package} failed", write.Path, name);
                failed.Insert(0, write);
                failures.Add($"{write.Path}: {ex.Message}");
            }

        if (failed.Count == 0)
        {
            Registry.CurrentUser.DeleteSubKeyTree(TrackingRoot + "\\" + name, false);
        }
        else
        {
            Registry.CurrentUser.DeleteSubKeyTree(TrackingRoot + "\\" + name + @"\Writes", false);
            using var writes = Registry.CurrentUser.CreateSubKey(TrackingRoot + "\\" + name + @"\Writes");
            for (int i = 0; i < failed.Count; i++) writes.SetValue((i + 1).ToString("D5", CultureInfo.InvariantCulture), JsonConvert.SerializeObject(failed[i]));

            using var track = Registry.CurrentUser.OpenSubKey(TrackingRoot + "\\" + name, true);
            track!.SetValue("Status", "Partly removed");
        }

        Notify();
        return new ShellUninstallResult(removed, left, failures);
    }

    /// <summary>Undoes one write; returns a note when something was deliberately left.</summary>
    private static string? Undo(ShellWrite w)
    {
        switch (w.Kind)
        {
            case ShellWriteKind.KeyCreated:
            {
                var (root, _, sub) = Split(w.Path);
                using (root)
                {
                    using (var key = root.OpenSubKey(sub))
                    {
                        if (key is null) return null;
                        if (key.ValueCount > 0 || key.SubKeyCount > 0) return $"{w.Path} was kept: something else is in it now";
                    }

                    root.DeleteSubKey(sub, false);
                }

                return null;
            }
            case ShellWriteKind.ValueSet:
            {
                var (root, _, sub) = Split(w.Path);
                using (root)
                using (var key = root.OpenSubKey(sub, true))
                {
                    if (key is null) return null;

                    string name = w.Name ?? string.Empty;
                    if (!w.Existed) key.DeleteValue(name, false);
                    else Restore(key, name, w.PreviousKind, w.PreviousData);
                }

                return null;
            }
            default:
                if (w.Existed)
                    File.WriteAllBytes(w.Path, Convert.FromBase64String(w.PreviousData ?? string.Empty));
                else if (File.Exists(w.Path))
                    File.Delete(w.Path);

                return null;
        }
    }

    // ---- registry helpers ----------------------------------------------------------------------

    private static (RegistryKey Root, string Prefix, string Sub) Split(string path)
    {
        int slash = path.IndexOf('\\');
        string hive = (slash < 0 ? path : path[..slash]).ToUpperInvariant();
        string sub = slash < 0 ? string.Empty : path[(slash + 1)..];
        var root = hive switch
                   {
                       "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
                       "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
                       _      => throw new ShellPackageException("Only HKCU and HKLM can be written: " + path)
                   };
        return (root, hive, sub);
    }

    private static string Encode(object? value, RegistryValueKind kind)
    {
        return value switch
               {
                   null            => string.Empty,
                   string s        => s,
                   int i           => i.ToString(CultureInfo.InvariantCulture),
                   long l          => l.ToString(CultureInfo.InvariantCulture),
                   byte[] b        => Convert.ToBase64String(b),
                   string[] m      => string.Join('\0', m),
                   _               => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
               };
    }

    private static void Restore(RegistryKey key, string name, string? kindName, string? data)
    {
        Enum.TryParse(kindName, out RegistryValueKind kind);
        data ??= string.Empty;
        switch (kind)
        {
            case RegistryValueKind.DWord:
                key.SetValue(name, int.Parse(data, CultureInfo.InvariantCulture), kind);
                break;
            case RegistryValueKind.QWord:
                key.SetValue(name, long.Parse(data, CultureInfo.InvariantCulture), kind);
                break;
            case RegistryValueKind.Binary:
            case RegistryValueKind.None:
                key.SetValue(name, Convert.FromBase64String(data), kind);
                break;
            case RegistryValueKind.MultiString:
                key.SetValue(name, data.Split('\0'), kind);
                break;
            default:
                key.SetValue(name, data, kind == RegistryValueKind.Unknown ? RegistryValueKind.String : kind);
                break;
        }
    }

    /// <summary>Tells the shell that file types and menus changed, so Explorer shows them without signing out.</summary>
    private static void Notify()
    {
        try
        {
            SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Telling the shell about the change failed");
        }
    }
}
