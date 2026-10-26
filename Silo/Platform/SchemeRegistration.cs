using Microsoft.Win32;
using Serilog;

namespace Silo.Platform;

/// <summary>Who opens links of a URI scheme, as far as Settings ▸ App Settings ▸ Registered Handlers is concerned.</summary>
public enum SchemeOwner
{
    /// <summary>No program is registered for it.</summary>
    None,

    /// <summary>This copy of Silo (another copy of Silo counts too).</summary>
    Silo,

    /// <summary>Another program.</summary>
    Other
}

/// <summary>
///     Registers this copy of Silo as what opens a URI scheme's links for the current Windows user (under
///     <c>HKEY_CURRENT_USER\Software\Classes\&lt;scheme&gt;</c>, which wins over a machine-wide registration), and takes the
///     registration away again. Only Silo's own registration is ever removed.
/// </summary>
public static class SchemeRegistration
{
    private const string ClassesRoot = @"Software\Classes\";

    /// <summary>The command the shell runs for a link: this program with the link as its argument.</summary>
    public static string? OwnCommand => Environment.ProcessPath is { Length: > 0 } exe ? $"\"{exe}\" \"%1\"" : null;

    /// <summary>Whether <paramref name="scheme" /> is a valid scheme name (a letter, then letters, digits, <c>+</c>, <c>-</c> or <c>.</c>).</summary>
    public static bool IsValidScheme(string? scheme)
    {
        return !string.IsNullOrEmpty(scheme) && char.IsAsciiLetter(scheme[0]) &&
               scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
    }

    /// <summary>Who opens the scheme's links now, and the command that does it (empty when none does).</summary>
    public static (SchemeOwner Owner, string Command) OwnerOf(string scheme)
    {
        try
        {
            var info = App.Resolve<IUrlInfo>()?.GetInfo(new Uri(scheme + ":x"));
            if (info is null) return (SchemeOwner.None, string.Empty);
            return (info.IsOurs ? SchemeOwner.Silo : SchemeOwner.Other, info.HandlerCommand);
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            Log.Debug(ex, "Could not tell who opens {scheme}", scheme);
            return (SchemeOwner.None, string.Empty);
        }
    }

    /// <summary>Makes this program what opens the scheme's links for the current user.</summary>
    /// <exception cref="ArgumentException">Not a valid scheme name.</exception>
    /// <exception cref="InvalidOperationException">The program's own path is not known.</exception>
    public static void Register(string scheme)
    {
        if (!IsValidScheme(scheme)) throw new ArgumentException($"\"{scheme}\" is not a valid URI scheme.", nameof(scheme));
        string command = OwnCommand ?? throw new InvalidOperationException("Silo's own program file could not be found.");

        using (var key = Registry.CurrentUser.CreateSubKey(ClassesRoot + scheme, true))
        {
            key.SetValue(string.Empty, $"URL:{scheme} Protocol");
            key.SetValue("URL Protocol", string.Empty);
            using var cmd = key.CreateSubKey(@"shell\open\command", true);
            cmd.SetValue(string.Empty, command);
        }

        Forget();
    }

    /// <summary>
    ///     Removes the current user's registration of the scheme when it is Silo's (another program's is left alone).
    ///     Returns whether one was removed.
    /// </summary>
    public static bool Unregister(string scheme)
    {
        if (!IsValidScheme(scheme)) return false;

        string? command;
        using (var cmd = Registry.CurrentUser.OpenSubKey(ClassesRoot + scheme + @"\shell\open\command"))
            command = cmd?.GetValue(null) as string;

        if (command is null || !IsSiloCommand(command)) return false;

        Registry.CurrentUser.DeleteSubKeyTree(ClassesRoot + scheme, false);
        Forget();
        return true;
    }

    private static bool IsSiloCommand(string command)
    {
        return command.Contains("silo.exe", StringComparison.OrdinalIgnoreCase) ||
               (Environment.ProcessPath is { } exe && command.Contains(exe, StringComparison.OrdinalIgnoreCase));
    }

    // the shell's answers are cached (SystemUrlHandler); they changed
    private static void Forget()
    {
        if (App.Resolve<IUrlInfo>() is SystemUrlHandler handlers) handlers.Refresh();
    }
}
