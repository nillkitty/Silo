using Silo.Contracts.ConnectorServices;
using Silo.Model;

namespace Silo.Contracts.Core;

/// <summary>
///     The Credential system is a two-pronged approach to ensure the most secure transmission possible:
///     <list type="number">
///         <item>ICredential.Configure(ICredentialSink) - The credential configures the sink</item>
///         <item>ICredentialSink.ConfigureFrom(ICredential) - The sink confiugres itself from the credential</item>
///     </list>
/// </summary>
public interface ICredential : IDisplayName
{
    AuthRealm Realm { get; }

    /// <summary>
    ///     Asks the credential to configure the target, if it supports it.
    /// </summary>
    void Configure(ICredentialSink target);
}

public interface ICredentialSink
{
    /// <summary>
    ///     Asks the consuming object to configure itself given the provided credential
    /// </summary>
    /// <param name="credential"></param>
    void ConfigureFrom(ICredential credential);
}