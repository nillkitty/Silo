using System.Windows;
using Silo.Contracts.Core;
using Silo.Contracts.UI;
using Silo.Explorer;
using Silo.Model;
using Silo.ViewModel;

namespace Silo.Providers.Tree;

/// <summary>The trees of the Internet's name and number spaces that can be shown in a Silo's Explorer.</summary>
public enum AddressSpace
{
    /// <summary>The IPv4 address space (<see cref="NetworkTree.CanonicalIpv4" />).</summary>
    Ipv4,

    /// <summary>The IPv6 address space (<see cref="NetworkTree.CanonicalIpv6" />).</summary>
    Ipv6,

    /// <summary>The domain name space (<see cref="DnsTree.Canonical" />).</summary>
    Dns
}

/// <summary>
///     Which of the <see cref="AddressSpace" /> trees a Silo shows. That is a preference of the Silo (see
///     <c>OpenSilo.ShowIpv4AddressSpace</c>), so it is kept in the Silo and comes back when the Silo is opened again.
/// </summary>
public static class AddressSpaces
{
    /// <summary>The key of the Silo preference that says whether the space is shown.</summary>
    public static string KeyOf(AddressSpace space) => space switch
                                                      {
                                                          AddressSpace.Ipv4 => "silo.addressSpace.ipv4",
                                                          AddressSpace.Ipv6 => "silo.addressSpace.ipv6",
                                                          _                 => "silo.addressSpace.dns"
                                                      };

    /// <summary>What the space brings to the Explorer.</summary>
    public static INodeProvider RootOf(AddressSpace space) => space switch
                                                              {
                                                                  AddressSpace.Ipv4 => NetworkTree.CanonicalIpv4.Root,
                                                                  AddressSpace.Ipv6 => NetworkTree.CanonicalIpv6.Root,
                                                                  _                 => DnsTree.Canonical.Root
                                                              };

    /// <summary>Puts the space in, or takes it out of, <paramref name="nodes" />.</summary>
    public static void Sync(NodeHost nodes, AddressSpace space, bool shown)
    {
        var root = RootOf(space);
        if (shown)
            nodes.Add(root);
        else
            nodes.Remove(root);
    }

    /// <summary>Shows or hides the space in the Silo's Explorer, and keeps that choice in the Silo.</summary>
    public static void Show(OpenSilo silo, AddressSpace space, bool shown)
    {
        var preference = silo.Preferences.FirstOrDefault(p => p.Key == KeyOf(space));
        if (preference is not null)
            preference.Value = shown;
        else
            Sync(silo.Nodes, space, shown);
    }

    /// <summary>True when the Silo's Explorer shows the space.</summary>
    public static bool IsShown(OpenSilo silo, AddressSpace space) => silo.Nodes.Provides(RootOf(space));

    /// <summary>Selects the Explorer node of <paramref name="model" /> in the main window (see <see cref="ExplorerView.Reveal" />).</summary>
    public static bool Reveal(object model)
    {
        return Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault()?.ExplorerControl.Reveal(model) ?? false;
    }
}

/// <summary>An Explorer node for one of the tree models: its words, icon and address, and its children as they are asked for.</summary>
public record TreeSiloNode : SiloNode
{
    public TreeSiloNode(object model, string text, Uri uri, IconRef? icon, string typeName,
                        Func<CancellationToken, Task<IReadOnlyList<SiloNode>>>? loader, int? expand = null) : base(model, text, uri, icon, expand)
    {
        TypeName = typeName;
        if (loader is not null) ChildLoader = loader;
    }
}
