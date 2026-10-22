using System.IO;
using System.Text.RegularExpressions;

namespace Silo.Platform;

/// <summary>
///     Reads the command lines the shell keeps for what opens a URL or a file type
///     (<c>"C:\Program Files\Browser\browser.exe" --single-argument %1</c>) -- pure text work, so it can be checked without a
///     registry.
/// </summary>
public static class ShellCommand
{
    private static readonly Regex Placeholder = new("%(?:1|L|l|\\*)", RegexOptions.Compiled);

    /// <summary>
    ///     Splits a registered command into the program and its arguments. The program is quoted or, when it is not, runs
    ///     to the first <c>.exe</c> that ends a word (programs under <c>Program Files</c> are registered unquoted now and
    ///     then), else to the first space. Environment variables (<c>%SystemRoot%</c>) are expanded. Null when the command
    ///     names nothing.
    /// </summary>
    public static (string Program, string Arguments)? Split(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        string text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.Length == 0) return null;

        string program;
        string arguments;
        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            if (end < 0)
            {
                program   = text[1..];
                arguments = string.Empty;
            }
            else
            {
                program   = text[1..end];
                arguments = text[(end + 1)..];
            }
        }
        else
        {
            int cut = ProgramEnd(text);
            program   = text[..cut];
            arguments = text[cut..];
        }

        program = program.Trim();
        return program.Length == 0 ? null : (program, arguments.Trim());
    }

    /// <summary>The program of a registered command (<see cref="Split" />'s first half), or null.</summary>
    public static string? ProgramOf(string? command)
    {
        return Split(command)?.Program;
    }

    /// <summary>
    ///     Whether two program paths are the same file (full paths compared without regard to case, quotes and environment
    ///     variables ignored). Two blanks are not the same.
    /// </summary>
    public static bool SameProgram(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        return string.Equals(FullPath(a), FullPath(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether two program paths have the same file name (the same program installed somewhere else, or a debug build of it).</summary>
    public static bool SameProgramName(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

        return string.Equals(FileNameOf(a), FileNameOf(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The file name of a program path, with quotes dropped. A registered command is Windows text, so both kinds of slash end a folder
    ///     whichever system reads it.
    /// </summary>
    public static string FileNameOf(string path)
    {
        string text = path.Trim().Trim('"');
        return text[(text.LastIndexOfAny(['\\', '/']) + 1)..];
    }

    /// <summary>
    ///     Puts <paramref name="target" /> where the arguments of a registered command say (<c>%1</c>, <c>%L</c>, <c>%l</c>,
    ///     <c>%*</c>); with none of those, it is added at the end in quotes, which is what the shell does for a command that
    ///     does not say.
    /// </summary>
    public static string Substitute(string? arguments, Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);

        string text  = target.IsAbsoluteUri ? target.AbsoluteUri : target.OriginalString;
        string given = arguments ?? string.Empty;
        if (Placeholder.IsMatch(given)) return Placeholder.Replace(given, _ => text);

        return given.Length == 0 ? $"\"{text}\"" : $"{given} \"{text}\"";
    }

    private static int ProgramEnd(string text)
    {
        for (int at = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(".exe", at + 4, StringComparison.OrdinalIgnoreCase))
        {
            int after = at + 4;
            if (after == text.Length || char.IsWhiteSpace(text[after])) return after;
        }

        int space = text.IndexOf(' ');
        return space < 0 ? text.Length : space;
    }

    /// <summary>The full path of a program path, with quotes and environment variables dealt with; the text as it was when it is not a path.</summary>
    public static string FullPath(string path)
    {
        string text = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        try
        {
            return Path.GetFullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return text;
        }
    }
}
