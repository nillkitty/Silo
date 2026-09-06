using System.Data;
using System.Drawing;
using Telefrag.Common;

namespace Silo.Connectors;

[Flags]
public enum ConnectorSetupFlags
{
    None,
    RequiresConfig      = 1,
    RequiresFile        = 2,
    Connectionless      = 4,
    BackgroundReconnect = 8,
}