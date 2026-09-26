using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using Silo.Connectors;
using Silo.Model;
using Telefrag.DI;
using Telerik.Windows.Controls;

namespace Silo.Tools;

/// <summary>
///     Shows a live <see cref="RadDiagram"/> with a shape for every open Silo
///     (<see cref="OpenSilo.OpenSilos"/>) and a shape for each of that Silo's active
///     connections (<see cref="OpenSilo.Connections"/>), with the connecting edge labeled
///     with the connection's connector name (<see cref="IConnector.DisplayName"/>).
///
///     Registered as a singleton: there is only ever one instance of this tool, so its
///     window (and diagram) is reused every time it's toggled open again rather than being
///     rebuilt from scratch.
/// </summary>
[Singleton(typeof(ITool))]
public class ConnectionDiagramTool : ToolBase
{
    private static readonly Brush SiloBrush       = new SolidColorBrush(Color.FromRgb(0x2D, 0x5F, 0x8A));
    private static readonly Brush ConnectionBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0xA5, 0x39));
    private static readonly Brush EmptyBrush      = Brushes.Gray;

    private const double SiloWidth        = 170;
    private const double SiloHeight       = 50;
    private const double ConnectionWidth  = 170;
    private const double ConnectionHeight = 44;
    private const double ColumnGap        = 90;
    private const double RowGap           = 18;

    private readonly Dictionary<OpenSilo, NotifyCollectionChangedEventHandler> _hooked = new();

    private RadDiagram? _diagram;

    public ConnectionDiagramTool() : base("connectiondiagram")
    {
        DisplayName = "Connection Diagram";
        GroupName   = "Diagrams";
    }

    protected override UIElement OnBuildContent()
    {
        _diagram = new RadDiagram
                   {
                       MinWidth  = 400,
                       MinHeight = 300
                   };

        OpenSilo.OpenSilos.CollectionChanged += OnSilosChanged;
        foreach (var silo in OpenSilo.OpenSilos)
            Hook(silo);

        Rebuild();

        return _diagram;
    }

    private void OnSilosChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (OpenSilo silo in e.OldItems)
                Unhook(silo);

        if (e.NewItems is not null)
            foreach (OpenSilo silo in e.NewItems)
                Hook(silo);

        App.RunSafe(Rebuild);
    }

    private void Hook(OpenSilo silo)
    {
        if (_hooked.ContainsKey(silo)) return;

        NotifyCollectionChangedEventHandler handler = (_, _) => App.RunSafe(Rebuild);
        silo.Connections.CollectionChanged += handler;
        _hooked[silo] = handler;
    }

    private void Unhook(OpenSilo silo)
    {
        if (_hooked.Remove(silo, out var handler))
            silo.Connections.CollectionChanged -= handler;
    }

    private void Rebuild()
    {
        if (_diagram is null) return;

        _diagram.Items.Clear();

        double y = 20;

        foreach (var silo in OpenSilo.OpenSilos)
        {
            var siloShape = MakeShape(silo.Name ?? silo.FilePath ?? "(unsaved Silo)", SiloWidth, SiloHeight, SiloBrush);
            siloShape.Position = new Point(20, y);
            _diagram.AddShape(siloShape);

            var connections = silo.Connections.ToList();
            if (connections.Count == 0)
            {
                y += SiloHeight + RowGap;
                continue;
            }

            double connectionY = y;
            foreach (var connection in connections)
            {
                var connectorName = connection.Connector?.DisplayName ?? "Connector";
                var label         = connection.GetHostname() ?? connectorName;

                var connectionShape = MakeShape(label, ConnectionWidth, ConnectionHeight, ConnectionBrush);
                connectionShape.Position = new Point(20 + SiloWidth + ColumnGap, connectionY);
                _diagram.AddShape(connectionShape);

                var edge = new RadDiagramConnection
                           {
                               Source  = siloShape,
                               Target  = connectionShape,
                               Content = connectorName
                           };
                _diagram.AddConnection(edge);

                connectionY += ConnectionHeight + RowGap;
            }

            y += Math.Max(SiloHeight + RowGap, connections.Count * (ConnectionHeight + RowGap));
        }

        if (_diagram.Items.Count == 0)
            _diagram.AddShape(MakeShape("No open Silos", 220, 50, EmptyBrush));
    }

    private static RadDiagramShape MakeShape(string text, double width, double height, Brush fill)
    {
        return new RadDiagramShape
               {
                   Content    = text,
                   Width      = width,
                   Height     = height,
                   Background = fill,
                   Stroke     = Brushes.Black
               };
    }
}
