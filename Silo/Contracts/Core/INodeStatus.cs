namespace Silo.Contracts.Core;

/// <summary>
///     A node model that knows its thing does not exist (a domain name that DNS answered NXDOMAIN for): the Explorer draws a red X
///     badge on its icon.
/// </summary>
public interface INodeStatus
{
    /// <summary>True when the thing the node stands for is known not to exist.</summary>
    bool IsMissing { get; }
}
