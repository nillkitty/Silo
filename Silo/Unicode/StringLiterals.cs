using System.Globalization;
using System.Text;

namespace Silo.Unicode;

/// <summary>Writes code points as the text of a string literal, with escapes for everything that is not plain printable ASCII.</summary>
public static class StringLiterals
{
    /// <summary>A C# string literal: <c>"▶\U0001F7E2"</c>. Code points above U+FFFF use <c>\U</c> with eight digits.</summary>
    public static string CSharp(IEnumerable<CodePoint> codePoints)
    {
        return Build(codePoints, static cp => cp.Value > 0xFFFF
                                                  ? "\\U" + cp.Value.ToString("X8", CultureInfo.InvariantCulture)
                                                  : "\\u" + cp.Value.ToString("X4", CultureInfo.InvariantCulture));
    }

    /// <summary>A JSON (and JavaScript) string literal: <c>"▶🟢"</c>. Code points above U+FFFF are written as a surrogate pair.</summary>
    public static string Json(IEnumerable<CodePoint> codePoints)
    {
        return Build(codePoints, static cp =>
                                 {
                                     if (cp.Value <= 0xFFFF) return "\\u" + cp.Value.ToString("X4", CultureInfo.InvariantCulture);

                                     string pair = char.ConvertFromUtf32(cp.Value);
                                     return "\\u" + ((int)pair[0]).ToString("X4", CultureInfo.InvariantCulture) + "\\u" + ((int)pair[1]).ToString("X4", CultureInfo.InvariantCulture);
                                 });
    }

    private static string Build(IEnumerable<CodePoint> codePoints, Func<CodePoint, string> escape)
    {
        var sb = new StringBuilder("\"");
        foreach (var cp in codePoints)
        {
            switch (cp.Value)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case >= 0x20 and < 0x7F:
                    sb.Append((char)cp.Value);
                    break;
                default:
                    sb.Append(escape(cp));
                    break;
            }
        }

        return sb.Append('"').ToString();
    }
}
