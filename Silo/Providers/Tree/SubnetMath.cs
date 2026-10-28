using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;

namespace Silo.Providers.Tree;

/// <summary>
///     Arithmetic on <see cref="IPSubnet" />s (the type itself knows its network address, prefix length and whether it contains
///     an address): first and last address, size, parents and cells, the reverse-DNS zone, and reading one from text. An
///     <see cref="IPSubnet" /> is a class without value equality: compare two with <see cref="SameAs" />.
/// </summary>
/// <remarks>The properties (<c>MaxPrefixLength</c>, <c>Network</c>) are in <see cref="SubnetExtensions" />.</remarks>
public static class SubnetMath
{
    /// <summary>True for an IPv6 subnet.</summary>
    public static bool IsV6(this IPSubnet subnet) => subnet.NetworkAddress.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>True when both are the same block (same network address, same prefix length).</summary>
    public static bool SameAs(this IPSubnet? subnet, IPSubnet? other) =>
        subnet is not null && other is not null && subnet.PrefixLength == other.PrefixLength &&
        subnet.Network.Equals(other.Network);

    /// <summary>The same subnet with the host bits of its address cleared (10.1.2.3/8 becomes 10.0.0.0/8): an <see cref="IPSubnet" /> keeps the address it was made with.</summary>
    public static IPSubnet Normalized(this IPSubnet subnet) => subnet.Network.SubnetOf(subnet.PrefixLength);

    /// <summary>The address with all but the first <paramref name="prefix" /> bits cleared (10.1.2.3 with 8 gives 10.0.0.0).</summary>
    public static IPAddress Mask(this IPAddress address, int prefix)
    {
        byte[] bytes = address.GetAddressBytes();
        for (int bit = prefix; bit < bytes.Length * 8; bit++) bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        return new IPAddress(bytes);
    }

    /// <summary>The subnet of <paramref name="prefix" /> bits that holds <paramref name="address" />.</summary>
    public static IPSubnet SubnetOf(this IPAddress address, int prefix) => new(address.Mask(prefix), prefix);

    /// <summary>The address as a number (big-endian, unsigned).</summary>
    public static BigInteger ToNumber(this IPAddress address) => new(address.GetAddressBytes(), true, true);

    /// <summary>The address of <paramref name="family" /> with the value <paramref name="value" />.</summary>
    public static IPAddress FromNumber(BigInteger value, AddressFamily family)
    {
        int    length = family == AddressFamily.InterNetwork ? 4 : 16;
        byte[] raw    = value.ToByteArray(true, true);
        byte[] bytes  = new byte[length];
        Array.Copy(raw, Math.Max(0, raw.Length - length), bytes, Math.Max(0, length - raw.Length), Math.Min(raw.Length, length));
        return new IPAddress(bytes);
    }

    /// <summary>How many addresses the subnet holds.</summary>
    public static BigInteger Size(this IPSubnet subnet) => BigInteger.One << (subnet.MaxPrefixLength - subnet.PrefixLength);

    /// <summary>The first address (the network address).</summary>
    public static IPAddress First(this IPSubnet subnet) => subnet.Network;

    /// <summary>The last address (the broadcast address of an IPv4 subnet).</summary>
    public static IPAddress Last(this IPSubnet subnet) =>
        FromNumber(subnet.Network.ToNumber() + subnet.Size() - 1, subnet.NetworkAddress.AddressFamily);

    /// <summary>True when <paramref name="inner" /> is the same subnet as, or inside, <paramref name="outer" />.</summary>
    public static bool Covers(this IPSubnet outer, IPSubnet inner) =>
        inner.PrefixLength >= outer.PrefixLength && outer.Contains(inner.Network);

    /// <summary>True when <paramref name="inner" /> is inside <paramref name="outer" /> and smaller than it.</summary>
    public static bool StrictlyCovers(this IPSubnet outer, IPSubnet inner) =>
        inner.PrefixLength > outer.PrefixLength && outer.Contains(inner.Network);

    /// <summary>The subnet that is one bit shorter and holds this one, or null for /0.</summary>
    public static IPSubnet? Parent(this IPSubnet subnet) =>
        subnet.PrefixLength == 0 ? null : subnet.Network.SubnetOf(subnet.PrefixLength - 1);

    /// <summary>
    ///     The equal subnets of <paramref name="newPrefix" /> bits that make up this one, in address order. The caller keeps
    ///     the count small (it is 2 to the power of the difference).
    /// </summary>
    public static IEnumerable<IPSubnet> Subdivide(this IPSubnet subnet, int newPrefix)
    {
        if (newPrefix < subnet.PrefixLength || newPrefix > subnet.MaxPrefixLength)
            throw new ArgumentOutOfRangeException(nameof(newPrefix));

        var family = subnet.NetworkAddress.AddressFamily;
        var start  = subnet.Network.ToNumber();
        var step   = BigInteger.One << (subnet.MaxPrefixLength - newPrefix);
        int count  = 1 << (newPrefix - subnet.PrefixLength);
        for (int i = 0; i < count; i++)
            yield return new IPSubnet(FromNumber(start + step * i, family), newPrefix);
    }

    /// <summary>The CIDR text: <c>10.0.0.0/8</c>, <c>2001:db8::/32</c> (the network address, whatever address the subnet was made with).</summary>
    public static string Cidr(this IPSubnet subnet) => $"{subnet.Network}/{subnet.PrefixLength}";

    /// <summary>The IPv4 netmask of the subnet (255.0.0.0 for /8); null for IPv6.</summary>
    public static IPAddress? Netmask(this IPSubnet subnet)
    {
        if (subnet.IsV6()) return null;

        uint mask = subnet.PrefixLength == 0 ? 0 : uint.MaxValue << (32 - subnet.PrefixLength);
        return FromNumber(mask, AddressFamily.InterNetwork);
    }

    /// <summary>The IPv4 wildcard mask (the netmask turned over: 0.255.255.255 for /8); null for IPv6.</summary>
    public static IPAddress? Wildcard(this IPSubnet subnet)
    {
        if (subnet.Netmask() is not { } mask) return null;

        byte[] bytes = mask.GetAddressBytes();
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)~bytes[i];
        return new IPAddress(bytes);
    }

    /// <summary>
    ///     The reverse-DNS zone that holds the subnet's PTR records: <c>10.in-addr.arpa</c> for 10.0.0.0/8, a name down to the
    ///     nibble for IPv6. A subnet that does not end on an octet (IPv4) or nibble (IPv6) boundary is not a zone of its own; the
    ///     zone returned is then the longest one that holds it, and <paramref name="exact" /> is false.
    /// </summary>
    public static string ReverseZone(this IPSubnet subnet, out bool exact)
    {
        byte[] bytes = subnet.Network.GetAddressBytes();
        if (!subnet.IsV6())
        {
            int octets = subnet.PrefixLength / 8;
            exact = subnet.PrefixLength % 8 == 0;
            var parts = new List<string>();
            for (int i = octets - 1; i >= 0; i--) parts.Add(bytes[i].ToString());
            parts.Add("in-addr.arpa");
            return string.Join('.', parts);
        }

        int nibbles = subnet.PrefixLength / 4;
        exact = subnet.PrefixLength % 4 == 0;
        var sb = new StringBuilder();
        for (int i = nibbles - 1; i >= 0; i--)
        {
            int value = (bytes[i / 2] >> (i % 2 == 0 ? 4 : 0)) & 0xF;
            sb.Append(value.ToString("x")).Append('.');
        }

        sb.Append("ip6.arpa");
        return sb.ToString();
    }

    /// <summary>The name a PTR record of a single address has (<c>1.0.0.127.in-addr.arpa</c>).</summary>
    public static string ReverseName(this IPAddress address) =>
        address.SubnetOf(address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128).ReverseZone(out _);

    /// <summary>Reads <c>10.0.0.0/8</c>, <c>fe80::/10</c> or a bare address (taken as a single-address subnet).</summary>
    public static bool TryParse(string? text, out IPSubnet subnet)
    {
        subnet = default!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim();
        int    slash   = text.IndexOf('/');
        string address = slash < 0 ? text : text[..slash];
        if (address.StartsWith('[') && address.EndsWith(']')) address = address[1..^1];
        if (!IPAddress.TryParse(address, out var ip)) return false;
        if (ip.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return false;

        int max    = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        int prefix = max;
        if (slash >= 0 && (!int.TryParse(text[(slash + 1)..], out prefix) || prefix < 0 || prefix > max)) return false;

        subnet = ip.SubnetOf(prefix);
        return true;
    }

    /// <summary>Reads an <c>ip4://10.0.0.0/8</c> or <c>ip6://[2001:db8::]/32</c> address (see <see cref="NetworkTree" />).</summary>
    public static bool TryParse(Uri? uri, out IPSubnet subnet)
    {
        subnet = default!;
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (uri.Scheme is not ("ip4" or "ipv4" or "ip6" or "ipv6")) return false;

        string host = uri.Host.Trim('[', ']');
        string path = uri.AbsolutePath.Trim('/');
        return TryParse(path.Length > 0 ? host + "/" + path : host, out subnet);
    }

    /// <summary>The address of the subnet as a URI: <c>ip4://10.0.0.0/8</c>, <c>ip6://[2001:db8::]/32</c>.</summary>
    public static Uri ToUri(this IPSubnet subnet)
    {
        string host = subnet.IsV6() ? $"[{subnet.Network}]" : subnet.Network.ToString();
        return new Uri($"{(subnet.IsV6() ? "ip6" : "ip4")}://{host}/{subnet.PrefixLength}", UriKind.Absolute);
    }
}
