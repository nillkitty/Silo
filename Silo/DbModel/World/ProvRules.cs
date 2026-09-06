namespace Silo.DbModel.World;

public class ProvRules
{
    /// <summary>
    /// The type name of the provider which will provision this object into the Metaverse
    /// </summary>
    public string? ProviderType { get; set; }

    /// <summary>
    /// A unique name which can be used by the provider to identify this specific provisioning rule.
    /// </summary>
    public string? RuleName { get; set; }
}