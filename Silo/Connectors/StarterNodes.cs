using Serilog;
using Silo.Connectors.File;
using Silo.Connectors.System;
using Silo.Connectors.Virtual;
using Silo.Contracts.Core;
using Silo.Labs;
using Silo.Model;
using Silo.Providers.Tree;
using Silo.Tools.UnicodeExplorer;
using Silo.ViewModel;

namespace Silo.Connectors;

/// <summary>
///     The starting point of the Explorer, next to the Silo's own node: <c>System</c> (this computer: Network, Filesystem, Registry, WMI)
///     and <c>Universe</c> (reference material the same for everyone: Schema, Unicode, DNS, IPv4, IPv6), so nobody has to make these
///     connections by hand. They are made each time a Silo is opened and are not saved in it, so they follow what the app offers
///     (the Silo preference <c>silo.starterNodes</c> turns them off).
/// </summary>
internal static class StarterNodes
{
    public static void Install(OpenSilo silo)
    {
        var system   = Group("System", "system", "PcMonitor");
        var universe = Group("Universe", "universe", "Nodes");
        silo.Nodes.AddNode(system, null);
        silo.Nodes.AddNode(universe, null);

        // System: connections to this computer
        Try("Network", () =>
                       {
                           var network = new NetInfoConnector();
                           silo.Connections.Add(network);
                           silo.Nodes.Add(network, system);
                       });

        Try("Filesystem", () =>
                          {
                              var files = FileConnection.FromPath(Find<FileConnector>(), FileConnection.GetRootPath(),
                                                                  new ConnectionRequest { DisplayName = "Filesystem" });
                              silo.Connections.Add(files);
                              silo.Nodes.Add(files, system);
                          });

        Try("Registry", () =>
                        {
                            if (Find<RegistryConnector>().CreateAutoConnection() is not RegistryConnection registry) return;

                            registry.DisplayText = "Registry";
                            silo.Connections.Add(registry);
                            silo.Nodes.Add(registry, system);
                        });

        Try("WMI", () =>
                   {
                       var wmi = new WmiConnection(Find<WmiConnector>(), WmiLocation.FromUri(new Uri("wmi:///")), [], null, system);
                       silo.Connections.Add(wmi);
                       if (wmi.Node is { } node) silo.Nodes.AddNode(node, system);
                   });

        // Universe: tools and labs, then the name and number spaces (which the Silo's preferences show and hide)
        Try("Schema", () =>
                      {
                          if (LabHost.Labs.FirstOrDefault(l => l.GetType().Name == "SchemaLab") is { } lab)
                              silo.Nodes.AddNode(Leaf(new LabLauncher(lab, "Schema", "silo:///starter/schema", "DataStructure")), universe);
                      });

        Try("Unicode", () =>
                       {
                           if (App.Resolve<IEnumerable<ITool>>()?.OfType<UnicodeExplorerTool>().FirstOrDefault() is { } tool)
                               silo.Nodes.AddNode(new SiloNode(tool, "Unicode", new Uri("silo:///starter/unicode"), IconRef.FromKey("Unicode"), null), universe);
                       });

        foreach (var space in new[] { AddressSpace.Dns, AddressSpace.Ipv4, AddressSpace.Ipv6 })
        {
            var root = AddressSpaces.RootOf(space);
            silo.Nodes.Place(root, universe);

            // already in at the top, because the Silo's preferences were read before these nodes existed
            if (silo.Nodes.Remove(root)) silo.Nodes.Add(root);
        }
    }

    private static SiloNode Group(string name, string key, string icon)
    {
        var uri = new Uri("silo:///starter/" + key);
        return new SiloNode(new FolderModel(name, "Group", uri, IconRef.FromKey(icon)), name, uri, IconRef.FromKey(icon), 1);
    }

    private static SiloNode Leaf(LabLauncher model)
    {
        return new SiloNode(model, model.Name, model.Uri, model.Icon, null);
    }

    /// <summary>The connector the app has registered for <typeparamref name="T" />, else a new one.</summary>
    private static T Find<T>() where T : class, IConnector, new()
    {
        return App.Resolve<IEnumerable<IConnector>>()?.OfType<T>().FirstOrDefault() ?? new T();
    }

    /// <summary>One starter node that cannot be made (a machine without WMI, say) must not keep the others out.</summary>
    private static void Try(string what, Action make)
    {
        try
        {
            make();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The starter node {What} could not be made", what);
        }
    }

    /// <summary>A node that opens a Lab when it is activated.</summary>
    private sealed class LabLauncher(ILab lab, string name, string uri, string iconKey) : INodeDetails, INodeActivation
    {
        public IconRef? Icon     { get; } = IconRef.FromKey(iconKey);
        public string   Name     { get; } = name;
        public string   TypeName => "Lab";
        public Uri      Uri      { get; } = new(uri);

        public void Activate() => LabHost.Open(lab);

        public IEnumerable<(string Name, string Value)> GetDetails()
        {
            yield return ("Opens", lab.Name + " Lab");
        }
    }
}
