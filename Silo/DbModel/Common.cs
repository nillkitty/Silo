using Silo.DbModel.Global;

namespace Silo.DbModel;

/// <summary>
///     Singleton table which contains the header data for the Silo
/// </summary>
public class Header
{
    public SiloFileType Type      { get; set; }
    public DateTime     Created   { get; set; } = DateTime.UtcNow;
    public DateTime     Modified  { get; set; } = DateTime.UtcNow;
    public DateTime     Opened    { get; set; } = DateTime.UtcNow;
    public DateTime     LastCheck { get; set; } = DateTime.UtcNow;
    public int          Version   { get; set; }
    public int          Build     { get; set; }
    public string       Creator   { get; set; } = string.Empty;
    public string       Owner     { get; set; } = string.Empty;
}