using System.Globalization;

namespace Silo.Model;

/// <summary>How a preference's value is written into the <c>Pref</c> table's <c>Data</c> column, and read back. Culture-independent.</summary>
/// <remarks>
///     An empty text is an empty <see cref="string" />, and no value (null) for anything nullable (a <see cref="Uri" />, an
///     <c>int?</c>); a <c>string?</c> property cannot tell the two apart.
/// </remarks>
public static class PreferenceValues
{
    private static readonly HashSet<Type> Simple =
    [
        typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(float), typeof(double), typeof(decimal), typeof(char), typeof(string), typeof(Uri), typeof(DateTime),
        typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid)
    ];

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Whether a preference property can have this type.</summary>
    public static bool IsSupported(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsEnum || Simple.Contains(t);
    }

    /// <summary>The text that stands for <paramref name="value" />.</summary>
    public static string ToText(object? value)
    {
        return value switch
               {
                   null             => string.Empty,
                   bool b           => b ? "true" : "false",
                   string s         => s,
                   Uri u            => u.OriginalString,
                   DateTime d       => d.ToString("O", Inv),
                   DateTimeOffset o => o.ToString("O", Inv),
                   TimeSpan t       => t.ToString("c", Inv),
                   Enum e           => e.ToString(),
                   float f          => f.ToString("R", Inv),
                   double d         => d.ToString("R", Inv),
                   IFormattable x   => x.ToString(null, Inv),
                   _                => value.ToString() ?? string.Empty
               };
    }

    /// <summary>The value of type <paramref name="type" /> that <paramref name="text" /> stands for; false if it stands for none.</summary>
    public static bool TryFromText(Type type, string? text, out object? value)
    {
        value = null;
        text ??= string.Empty;

        var nullable = Nullable.GetUnderlyingType(type);
        var t        = nullable ?? type;

        if (t == typeof(string))
        {
            value = text;
            return true;
        }

        if (text.Length == 0) return nullable is not null || !t.IsValueType; // nothing: fine for a type that can be nothing

        try
        {
            if (t == typeof(bool))
            {
                if (!bool.TryParse(text, out bool b)) return false;
                value = b;
                return true;
            }

            if (t.IsEnum)
            {
                if (!Enum.TryParse(t, text, true, out object? e)) return false;
                value = e;
                return true;
            }

            if (t == typeof(Uri))
            {
                if (!Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out var u)) return false;
                value = u;
                return true;
            }

            if (t == typeof(DateTime))
            {
                if (!DateTime.TryParse(text, Inv, DateTimeStyles.RoundtripKind, out var d)) return false;
                value = d;
                return true;
            }

            if (t == typeof(DateTimeOffset))
            {
                if (!DateTimeOffset.TryParse(text, Inv, DateTimeStyles.RoundtripKind, out var o)) return false;
                value = o;
                return true;
            }

            if (t == typeof(TimeSpan))
            {
                if (!TimeSpan.TryParse(text, Inv, out var ts)) return false;
                value = ts;
                return true;
            }

            if (t == typeof(Guid))
            {
                if (!Guid.TryParse(text, out var g)) return false;
                value = g;
                return true;
            }

            if (t == typeof(char))
            {
                if (text.Length != 1) return false;
                value = text[0];
                return true;
            }

            value = Convert.ChangeType(text, t, Inv); // the numbers
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        {
            value = null;
            return false;
        }
    }
}
