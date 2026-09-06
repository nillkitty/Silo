using System;
using System.Collections.Generic;
using System.Text;
using Silo.DbModel.Global;

namespace Silo.DbModel;

/// <summary>
/// Singleton table which contains the header data for the Silo
/// </summary>
public record Header(
    SiloFileType Type,
    DateTime     Created,
    DateTime     Modified,
    DateTime     Opened,
    DateTime     LastCheck,
    int          Version,
    int          Build,
    string       Creator,
    string       Owner);