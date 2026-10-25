using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Silo.Model;
using Silo.Drawio.Host;
using Silo.Drawio.Mx;

namespace Silo.Drawio.Live;

/// <summary>
///     Produces the generated layer of a diagram -- the cells Silo owns -- and tells the pane when it
///     needs regenerating. The pane merges the model into the diagram with the person's own cells left alone.
/// </summary>
public interface IDiagramContentSource
{
    event Action? Changed;

    DiagramModel Build();
}

/// <summary>
///     The live map of what Silo currently has open: one box per open Silo and, to its right, one per
///     active connection, joined by an edge labelled with the connector's name. Boxes link back to the
///     construct (<see cref="DiagramLink" />, click-through), and each connection box's colour is a
///     live binding to its connection state, so it flips without any regeneration.
/// </summary>
public sealed class SiloModelDiagramSource : IDiagramContentSource, IDisposable
{
    private const double RowHeight = 64;

    private readonly List<Action> _unhook = [];

    private ObservableCollection<OpenSilo>? _silos;

    public SiloModelDiagramSource()
    {
        SiloModelData.Instance.EnsureRegistered();
        _silos                   =  OpenSilo.OpenSilos;
        _silos.CollectionChanged += OnCollectionChanged;
    }

    public event Action? Changed;

    public void Dispose()
    {
        if (_silos is not null) _silos.CollectionChanged -= OnCollectionChanged;
        _silos = null;
        Unhook();
    }

    public DiagramModel Build()
    {
        Unhook();

        var model  = new DiagramModel();
        var values = new Dictionary<string, Func<object?>>();
        var silos  = OpenSilo.OpenSilos.ToList();

        values["silos.count"] = () => OpenSilo.OpenSilos.Count;

        if (silos.Count == 0)
        {
            model.Nodes.Add(new DiagramNode("silo-none", "No Silos are open", 0, 0, 200, 56,
                                            "shape=note;whiteSpace=wrap;html=1;fontStyle=2;fillColor=#f5f5f5;strokeColor=#999999;"));
            SiloModelData.Instance.Publish(values);
            return model;
        }

        double y = 0;

        for (int s = 0; s < silos.Count; s++)
        {
            var    silo     = silos[s];
            string siloId   = $"silo{s}";
            var    conns    = silo.Connections.ToList();
            double rows     = Math.Max(1, conns.Count);
            double siloTop  = y + (rows * RowHeight - RowHeight) / 2;

            values[siloId + ".name"]        = () => silo.Name ?? "(unsaved)";
            values[siloId + ".connections"] = () => silo.Connections.Count;

            WatchCollection(silo.Connections, () => Changed?.Invoke());

            model.Nodes.Add(new DiagramNode(siloId, silo.Name ?? "(unsaved)", 0, siloTop, 200, 48,
                                            "rounded=1;whiteSpace=wrap;html=1;fillColor=#dae8fc;strokeColor=#6c8ebf;fontStyle=1;",
                                            new Dictionary<string, string>
                                            {
                                                ["siloKind"]         = "silo",
                                                ["siloId"]           = silo.FilePath ?? siloId,
                                                [DiagramBindings.LabelAttribute] = "{silo:" + siloId + ".name}"
                                            },
                                            new DiagramLink("silo", silo.FilePath ?? siloId).ToUrl()));

            for (int c = 0; c < conns.Count; c++)
            {
                var    conn   = conns[c];
                string connId = $"conn{s}_{c}";

                values[connId + ".name"] = () => ConnectionName(conn);
                values[connId + ".state"] = () => conn.ConnectionState switch
                                                  {
                                                      true  => "connected",
                                                      false => "disconnected",
                                                      null  => "connectionless"
                                                  };

                if (conn is INotifyPropertyChanged npc) WatchProperties(npc, () => Changed?.Invoke());

                model.Nodes.Add(new DiagramNode(connId, ConnectionName(conn), 320, y + c * RowHeight, 220, 44,
                                                "rounded=1;whiteSpace=wrap;html=1;strokeColor=#666666;",
                                                new Dictionary<string, string>
                                                {
                                                    ["siloKind"] = "connection",
                                                    ["siloId"]   = $"{silo.FilePath ?? siloId}|{c}",
                                                    [DiagramBindings.LabelAttribute] = "{silo:" + connId + ".name}",
                                                    [DiagramBindings.StylePrefix + "fillColor"] =
                                                        "{silo:" + connId + ".state|map:connected=#d5e8d4,disconnected=#f8cecc,*=#f5f5f5}"
                                                },
                                                new DiagramLink("connection", $"{silo.FilePath ?? siloId}|{c}").ToUrl()));

                model.Edges.Add(new DiagramEdge($"edge{s}_{c}", siloId, connId, conn.Connector.DisplayName,
                                                "endArrow=classic;html=1;strokeColor=#666666;rounded=1;"));
            }

            y += rows * RowHeight + 24;
        }

        SiloModelData.Instance.Publish(values);
        return model;
    }

    internal static string ConnectionName(IConnection conn)
    {
        string? host = null;
        try
        {
            host = conn.GetHostname();
        }
        catch
        {
            /* a connection that can't say where it points is just labelled by its connector */
        }

        string connector = conn.Connector.DisplayName;
        return string.IsNullOrWhiteSpace(host) ? connector : $"{connector}: {host}";
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Changed?.Invoke();
    }

    private void WatchProperties(INotifyPropertyChanged source, Action onChange)
    {
        PropertyChangedEventHandler h = (_, _) => onChange();
        source.PropertyChanged += h;
        _unhook.Add(() => source.PropertyChanged -= h);
    }

    private void WatchCollection(INotifyCollectionChanged source, Action onChange)
    {
        NotifyCollectionChangedEventHandler h = (_, _) => onChange();
        source.CollectionChanged += h;
        _unhook.Add(() => source.CollectionChanged -= h);
    }

    private void Unhook()
    {
        foreach (var u in _unhook) u();
        _unhook.Clear();
    }
}

/// <summary>
///     The <c>silo</c> data provider: values about the open Silos and their connections, keyed by the
///     cell ids <see cref="SiloModelDiagramSource" /> generates (<c>{silo:conn0_1.state}</c>) plus
///     <c>silos.count</c>. Reads go straight to the live objects, so a binding always sees current state.
/// </summary>
public sealed class SiloModelData : IDiagramDataProvider
{
    private readonly object _gate = new();

    private Dictionary<string, Func<object?>> _values = new();
    private bool                              _registered;

    public static SiloModelData Instance { get; } = new();

    public event Action? Changed;

    public string Scheme => "silo";

    public bool TryGet(string path, out object? value)
    {
        Func<object?>? getter;
        lock (_gate)
        {
            _values.TryGetValue(path, out getter);
        }

        value = null;
        if (getter is null) return false;

        try
        {
            value = getter();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;
        DiagramData.Register(this);
    }

    internal void Publish(Dictionary<string, Func<object?>> values)
    {
        lock (_gate)
        {
            _values = values;
        }

        Changed?.Invoke();
    }
}

/// <summary>Click-through for the cells <see cref="SiloModelDiagramSource" /> generates.</summary>
public sealed class SiloModelLinkHandler : IDiagramLinkHandler
{
    public bool Handle(DiagramLink link, DrawioView source)
    {
        switch (link.Kind)
        {
            case "silo":
            {
                var silo = OpenSilo.OpenSilos.FirstOrDefault(s => s.FilePath == link.Id);
                if (silo is null || App.Instance is not { } app) return false;
                app.ActiveSilo = silo;
                return true;
            }

            case "connection":
            {
                string[] parts = link.Id.Split('|');
                if (parts.Length != 2 || !int.TryParse(parts[1], out int index)) return false;

                var silo = OpenSilo.OpenSilos.FirstOrDefault(s => s.FilePath == parts[0]);
                if (silo is null || index < 0 || index >= silo.Connections.Count) return false;

                var window = silo.Connections[index].BuildWindow();
                window?.Show();
                return window is not null;
            }

            default:
                return false;
        }
    }
}
