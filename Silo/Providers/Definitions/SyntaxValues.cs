using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Silo.Providers.Definitions;

/// <summary>
///     A pattern with <c>*</c> (any run of characters, including none) and <c>?</c> (any one character) in it,
///     matched against a whole string without regard to case.
/// </summary>
public sealed class Wildcard
{
    private readonly Regex _regex;

    public string Pattern { get; }

    /// <summary>Whether the text matches the whole pattern.</summary>
    public bool IsMatch(string? text)
    {
        return text is not null && _regex.IsMatch(text);
    }

    public override string ToString()
    {
        return Pattern;
    }

    public Wildcard(string pattern)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        string body = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        _regex = new Regex($@"\A{body}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline,
                           TimeSpan.FromSeconds(1));
    }
}

/// <summary>
///     An address with a prefix length (<c>10.0.0.0/8</c>, <c>fe80::/10</c>). The address may have host bits set (
///     <c>10.1.2.3/8</c>); <see cref="Network" /> is the same subnet with them cleared.
/// </summary>
public sealed record Cidr(IPAddress Address, int PrefixLength)
{
    /// <summary>The widest prefix the address family allows: 32 or 128.</summary>
    public int MaxPrefixLength => Address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;

    /// <summary>The first address of the subnet (the address with the host bits cleared).</summary>
    public IPAddress Network
    {
        get
        {
            byte[] bytes                                                               = Address.GetAddressBytes();
            for (int bit = PrefixLength; bit < bytes.Length * 8; bit++) bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
            return new IPAddress(bytes);
        }
    }

    /// <summary>Whether the address (of the same family) is inside the subnet.</summary>
    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != Address.AddressFamily) return false;

        byte[] a = address.GetAddressBytes(), b = Address.GetAddressBytes();
        for (int bit = 0; bit < PrefixLength; bit++)
        {
            int mask = 0x80 >> (bit % 8);
            if ((a[bit / 8] & mask) != (b[bit / 8] & mask)) return false;
        }

        return true;
    }

    public override string ToString()
    {
        return $"{Address}/{PrefixLength}";
    }
}

/// <summary>A command and its arguments, as written on a command line.</summary>
public sealed record CliCommand(string Executable, string[] Arguments)
{
    /// <summary>The command line again: the executable, quoted if it needs to be, and the arguments.</summary>
    public override string ToString()
    {
        return Arguments.Length == 0 ? CliText.Quote(Executable) : CliText.Quote(Executable) + " " + CliText.Join(Arguments);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Executable, Arguments.Length);
    }

    public bool Equals(CliCommand? other)
    {
        return other is not null && Executable == other.Executable && Arguments.SequenceEqual(other.Arguments);
    }
}