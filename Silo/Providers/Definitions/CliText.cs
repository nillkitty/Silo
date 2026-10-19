namespace Silo.Providers.Definitions;

/// <summary>
///     Splits and joins command lines by the Windows rules (the ones <c>CommandLineToArgvW</c> and the C runtime follow): arguments are
///     separated by spaces or tabs, double quotes group, a backslash only counts when it comes right before a quote (two backslashes and a quote
///     make one backslash and start or end a group, three make a backslash and a literal quote), and two quotes inside a group make one.
/// </summary>
public static class CliText
{
    /// <summary>Splits the text into arguments. Returns false, with the problem, when a quote is not closed.</summary>
    public static bool TrySplit(string? text, out string[] args, out string? error)
    {
        args  = [];
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        var result = new List<string>();
        int i = 0, n = text.Length;
        while (true)
        {
            while (i < n && (text[i] == ' ' || text[i] == '\t')) i++;
            if (i >= n) break;

            var sb = new StringBuilder();
            bool inQuote = false;
            while (i < n)
            {
                char c = text[i];
                if (c == '\\')
                {
                    int slashes = 0;
                    while (i < n && text[i] == '\\')
                    {
                        slashes++;
                        i++;
                    }

                    if (i < n && text[i] == '"')
                    {
                        sb.Append('\\', slashes / 2);
                        if (slashes % 2 == 1)
                        {
                            sb.Append('"');
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append('\\', slashes);
                    }

                    continue;
                }

                if (c == '"')
                {
                    if (inQuote && i + 1 < n && text[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                    }
                    else
                    {
                        inQuote = !inQuote;
                        i++;
                    }

                    continue;
                }

                if (!inQuote && (c == ' ' || c == '\t')) break;

                sb.Append(c);
                i++;
            }

            if (inQuote)
            {
                error = "A quote is never closed.";
                return false;
            }

            result.Add(sb.ToString());
        }

        args = result.ToArray();
        return true;
    }

    /// <summary>One argument written so that <see cref="TrySplit" /> gives it back: quoted only when it is empty or has a space, tab or quote in it.</summary>
    public static string Quote(string argument)
    {
        argument ??= string.Empty;
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"', '\n', '\r']) < 0) return argument;

        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', slashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', slashes).Append(c);
            }

            slashes = 0;
        }

        sb.Append('\\', slashes * 2).Append('"'); // backslashes at the end must not escape the closing quote
        return sb.ToString();
    }

    /// <summary>The arguments joined into a command line.</summary>
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));
}
