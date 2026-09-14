using System.Windows;

namespace Silo.Model;

/// <summary>
/// Metadata associated with a Silo
/// </summary>
public class OpenSiloMeta : ModelBase
{
    public Version? VersionCreated { get; set; } = System.Reflection.Assembly.GetExecutingAssembly().GetName()?.Version;

    public Version?  VersionSaved       { get; set; }
    public DateTime? TimeSiloCreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? TimeLastSaved      { get; set; }
    public DateTime? TimeLastSynced     { get; set; }
    public DateTime? TimeLastSyncFail   { get; set; }
    public string?   CreatedHostname    { get; set; } = Environment.MachineName;
    public string?   SavedHostname      { get; set; }
    public string?   CreatedUsername    { get; set; } = Environment.UserName;
    public string?   SavedUsername      { get; set; }
}