using System.Net;

namespace Silo.Providers.Tree;

/// <summary>
///     The properties <see cref="IPSubnet" /> does not have; its methods (first and last address, size, covers, reverse
///     zone...) are in <see cref="SubnetMath" />.
/// </summary>
public static class SubnetExtensions
{
    extension(IPSubnet cidr)
    {
        /// <summary>The widest prefix the address family allows: 32 or 128.</summary>
        public int MaxPrefixLength => cidr.AddressBytes.Length * 8;

        /// <summary>
        ///     The first address of the subnet: <see cref="IPSubnet.NetworkAddress" /> with the host bits cleared, which it does not
        ///     do itself (10.1.2.3/8 has the network address 10.1.2.3).
        /// </summary>
        public IPAddress Network => cidr.NetworkAddress.Mask(cidr.PrefixLength);
    }
}
