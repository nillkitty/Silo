using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Silo.Shell;

public enum ShellOpKind
{
    /// <summary>A registry key that must exist.</summary>
    Key,

    /// <summary>A registry value.</summary>
    Value,

    /// <summary>A file (a New-menu template).</summary>
    File,

    /// <summary>A shortcut (.lnk) to a program (Send To).</summary>
    Shortcut
}

/// <summary>
///     One thing the shell package does to the system: make a key, set a value, write a file or a shortcut. A package is compiled to a list of
///     these (<see cref="ShellCompiler" />); installing runs them in order and records each, the .reg preview prints them.
/// </summary>
/// <param name="Path">The key from its hive ("HKCU\Software\Classes\x"), or the file's path (environment variables unexpanded).</param>
/// <param name="Name">The value's name; empty is the key's default value.</param>
/// <param name="Data">The value, the file's text, or the shortcut's target.</param>
/// <param name="Extra">The shortcut's arguments.</param>
/// <param name="Entry">The entry of the package it comes from (for the person reading the preview).</param>
public sealed record ShellOp(ShellOpKind Kind, string Path, string? Name, ShellValueKind ValueKind, string? Data, string? Extra, string Entry);

/// <summary>Turns a Shell package into the registry values and files that set it up.</summary>
public static partial class ShellCompiler
{
    public const string SendToFolder    = @"%APPDATA%\Microsoft\Windows\SendTo";
    public const string TemplatesFolder = @"%APPDATA%\Microsoft\Windows\Templates";

    /// <summary>The root of the classes of the scope ("HKCU\Software\Classes").</summary>
    public static string ClassesRoot(ShellScope scope) => (scope == ShellScope.AllUsers ? @"HKLM" : "HKCU") + @"\Software\Classes";

    private static string Hive(ShellScope scope) => scope == ShellScope.AllUsers ? "HKLM" : "HKCU";

    /// <summary>The key of a class under the classes root: an extension goes by "SystemFileAssociations" so it works whatever program owns it.</summary>
    public static string ClassKey(ShellScope scope, string target, bool direct = false)
    {
        string t = target.Trim().Trim('\\');
        if (!direct && t.StartsWith('.')) t = @"SystemFileAssociations\" + t;
        return ClassesRoot(scope) + "\\" + t;
    }

    public static IReadOnlyList<ShellOp> Compile(ShellPackage package)
    {
        var ops = new List<ShellOp>();
        foreach (var e in package.Entries.Where(e => e.Enabled))
            try
            {
                CompileEntry(package.Scope, e, ops);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new InvalidOperationException($"{e.Display}: {ex.Message}", ex);
            }

        return ops;
    }

    private static void CompileEntry(ShellScope scope, ShellEntry e, List<ShellOp> ops)
    {
        string who = e.Kind + " " + e.Display;
        string root = ClassesRoot(scope);

        void Key(string path) => ops.Add(new ShellOp(ShellOpKind.Key, path, null, ShellValueKind.String, null, null, who));

        void Value(string path, string? name, string? data, ShellValueKind kind = ShellValueKind.String)
        {
            ops.Add(new ShellOp(ShellOpKind.Value, path, name ?? string.Empty, kind, data ?? string.Empty, null, who));
        }

        void Command(string key, string? command)
        {
            if (!string.IsNullOrWhiteSpace(command)) Value(key + @"\shell\open\command", null, command, ShellValueKind.String);
        }

        void Verb(string shellKey, ShellEntry v)
        {
            string key = shellKey + "\\" + Required(v.Name, "a verb needs a name");
            Key(key);
            Value(key, "MUIVerb", Required(v.Label, "a verb needs a label"));
            if (!string.IsNullOrWhiteSpace(v.Icon)) Value(key, "Icon", v.Icon);
            if (v.Position != ShellPosition.None) Value(key, "Position", v.Position.ToString());
            if (v.Extended) Value(key, "Extended", string.Empty);
            Value(key + @"\command", null, Required(v.Command, "a verb needs a command"));
        }

        switch (e.Kind)
        {
            case ShellEntryKind.Protocol:
            {
                string key = root + "\\" + Required(e.Name, "a protocol needs a scheme");
                Key(key);
                Value(key, null, "URL:" + (string.IsNullOrWhiteSpace(e.Description) ? e.Name + " Protocol" : e.Description));
                Value(key, "URL Protocol", string.Empty);
                if (!string.IsNullOrWhiteSpace(e.Icon)) Value(key + @"\DefaultIcon", null, e.Icon);
                Value(key + @"\shell\open\command", null, Required(e.Command, "a protocol needs a command"));
                break;
            }
            case ShellEntryKind.FileType:
            {
                string key = root + "\\" + Required(e.Name, "a file type needs a ProgID");
                Key(key);
                if (!string.IsNullOrWhiteSpace(e.Description)) Value(key, null, e.Description);
                if (!string.IsNullOrWhiteSpace(e.Icon)) Value(key + @"\DefaultIcon", null, e.Icon);
                Command(key, e.Command);
                foreach (string ext in e.Extensions)
                {
                    string extKey = root + "\\" + NormalizeExtension(ext);
                    Key(extKey);
                    Value(extKey + @"\OpenWithProgids", e.Name, string.Empty);
                    if (e.MakeDefault) Value(extKey, null, e.Name);
                }

                break;
            }
            case ShellEntryKind.Verb:
            {
                string cls = ClassKey(scope, Required(e.Target, "a verb needs a class to belong to"));
                Verb(cls + @"\shell", e);
                if (e.MakeDefault) Value(cls + @"\shell", null, e.Name);
                break;
            }
            case ShellEntryKind.Submenu:
            {
                string cls = ClassKey(scope, Required(e.Target, "a submenu needs a class to belong to"));
                string key = cls + @"\shell\" + Required(e.Name, "a submenu needs a name");
                Key(key);
                Value(key, "MUIVerb", Required(e.Label, "a submenu needs a label"));
                if (!string.IsNullOrWhiteSpace(e.Icon)) Value(key, "Icon", e.Icon);
                if (e.Position != ShellPosition.None) Value(key, "Position", e.Position.ToString());
                if (e.Extended) Value(key, "Extended", string.Empty);
                Value(key, "SubCommands", string.Empty);
                foreach (var child in e.Children.Where(c => c.Enabled)) Verb(key + @"\shell", child);
                break;
            }
            case ShellEntryKind.ClassIcon:
                Value(ClassKey(scope, Required(e.Target, "an icon needs a class"), true) + @"\DefaultIcon", null, Required(e.Icon, "an icon is needed"));
                break;
            case ShellEntryKind.DefaultVerb:
                Value(ClassKey(scope, Required(e.Target, "a default action needs a class")) + @"\shell", null, Required(e.Name, "a default action needs the verb's name"));
                break;
            case ShellEntryKind.SendTo:
                ops.Add(new ShellOp(ShellOpKind.Shortcut, SendToFolder + "\\" + Required(e.Name, "a Send To item needs a name") + ".lnk", null, ShellValueKind.String,
                                    Required(e.Command, "a Send To item needs a program"), e.Arguments, who));
                break;
            case ShellEntryKind.NewFile:
            {
                string ext = NormalizeExtension(Required(e.Name, "a New item needs an extension"));
                string key = root + "\\" + ext + @"\ShellNew";
                Key(key);
                if (string.IsNullOrEmpty(e.Content))
                {
                    Value(key, "NullFile", string.Empty);
                }
                else
                {
                    string file = TemplatesFolder + "\\" + (string.IsNullOrWhiteSpace(e.Label) ? "New File" : ShellText.PlainLabel(e.Label)) + ext;
                    ops.Add(new ShellOp(ShellOpKind.File, file, null, ShellValueKind.String, e.Content, null, who));
                    Value(key, "FileName", file, ShellValueKind.ExpandString);
                }

                break;
            }
            case ShellEntryKind.AppPath:
            {
                string key = Hive(scope) + @"\Software\Microsoft\Windows\CurrentVersion\App Paths\" + Required(e.Name, "a program name is needed");
                string exe = Required(e.Command, "the program's path is needed").Trim().Trim('"');
                Key(key);
                Value(key, null, exe);
                if (Path.GetDirectoryName(exe) is { Length: > 0 } dir) Value(key, "Path", dir);
                break;
            }
            case ShellEntryKind.Application:
            {
                string key = root + @"\Applications\" + Required(e.Name, "a program name is needed");
                Key(key);
                if (!string.IsNullOrWhiteSpace(e.Label)) Value(key, "FriendlyAppName", e.Label);
                if (!string.IsNullOrWhiteSpace(e.Icon)) Value(key + @"\DefaultIcon", null, e.Icon);
                Command(key, Required(e.Command, "an open command is needed"));
                foreach (string ext in e.Extensions) Value(key + @"\SupportedTypes", NormalizeExtension(ext), string.Empty);
                break;
            }
            case ShellEntryKind.Registry:
            {
                string key = Required(e.Key, "a registry entry needs a key").Trim().Trim('\\');
                Key(key);
                if (e.Data is not null) Value(key, e.ValueName, e.Data, e.ValueKind);
                break;
            }
            default:
                throw new InvalidOperationException("unknown kind of entry");
        }
    }

    private static string Required(string? value, string why)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(why);

        return value.Trim();
    }

    /// <summary>".md" for "md" or ".MD".</summary>
    public static string NormalizeExtension(string ext)
    {
        string t = ext.Trim();
        if (t.Length == 0) throw new ArgumentException("an extension is empty");

        return (t.StartsWith('.') ? t : "." + t).ToLowerInvariant();
    }

    // ---- .reg text -------------------------------------------------------------------------------

    /// <summary>The operations as a Registration Entries (.reg) file; files and shortcuts are noted as comments, since a .reg file cannot make them.</summary>
    public static string ToRegText(IEnumerable<ShellOp> ops)
    {
        var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        string? current = null;
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case ShellOpKind.File:
                    sb.Append("\r\n; file ").Append(op.Path).Append("  (").Append(op.Entry).Append(")\r\n");
                    continue;
                case ShellOpKind.Shortcut:
                    sb.Append("\r\n; shortcut ").Append(op.Path).Append(" -> ").Append(op.Data).Append(string.IsNullOrEmpty(op.Extra) ? string.Empty : " " + op.Extra)
                      .Append("  (").Append(op.Entry).Append(")\r\n");
                    continue;
            }

            if (!string.Equals(current, op.Path, StringComparison.OrdinalIgnoreCase))
            {
                current = op.Path;
                sb.Append("\r\n[").Append(RegHive(op.Path)).Append("]\r\n");
            }

            if (op.Kind == ShellOpKind.Value) sb.Append(RegLine(op)).Append("\r\n");
        }

        return sb.ToString();
    }

    private static string RegHive(string path)
    {
        int slash = path.IndexOf('\\');
        string hive = slash < 0 ? path : path[..slash];
        string rest = slash < 0 ? string.Empty : path[slash..];
        return (hive.ToUpperInvariant() == "HKLM" ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER") + rest;
    }

    private static string RegLine(ShellOp op)
    {
        string name = string.IsNullOrEmpty(op.Name) ? "@" : "\"" + Escape(op.Name) + "\"";
        switch (op.ValueKind)
        {
            case ShellValueKind.DWord:
                return name + "=dword:" + (int.TryParse(op.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0).ToString("x8", CultureInfo.InvariantCulture);
            case ShellValueKind.ExpandString:
                var bytes = Encoding.Unicode.GetBytes((op.Data ?? string.Empty) + "\0");
                return name + "=hex(2):" + string.Join(",", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            default:
                return name + "=\"" + Escape(op.Data ?? string.Empty) + "\"";
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.\\-]*$")]
    public static partial Regex SchemePattern();
}
