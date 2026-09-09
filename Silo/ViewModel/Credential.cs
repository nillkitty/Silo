using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Documents;
using Silo.DbModel.Global;

namespace Silo.Model;

public class SiloItem(OpenSilo parent)
{
    public OpenSilo          Parent { get; } = parent.Required();
    public IDatabaseProvider Data { get; } = parent.Required().Data.Required();
}

/// <summary>
/// An item stored in the silo's global database.
/// </summary>
/// <param name="parent"></param>
public class GlobalItem(OpenSilo parent) : SiloItem(parent)
{
}

public class Credential : GlobalItem
{
    public string? Username { get; set; }
    public string? Domain   { get; set; }
    public string? Password { get; set; }
    public Secret? Secret   { get; set; }

    protected Credential(OpenSilo parent) : base(parent)
    {
    }

    public static Credential FromSecret(OpenSilo parent, Secret s)
    {
        var c = new Credential(parent) { Secret = s.Required() };
        c.Decrypt();
        return c;
    }

    public static Credential Create(OpenSilo parent, string? user, string? pass,
                                    string?  domain = null)
    {
        var c = new Credential(parent)
                {
                    Username = user,
                    Password = pass,
                    Domain   = domain,
                };
        c.Encrypt();
        return c;
    }

    public bool Encrypt()
    {
        Secret =
            Parent.Encryption.GetOrCreateSecret(Username, Password, Domain);
        return (Secret != null);
    }

    public bool Decrypt()
    {
        Secret.Required();
        (Username, Password, Domain) = Parent.Encryption.GetCredential(Secret);
        return (Username != null || Password != null || Domain != null);
    }

    public static Credential Update(Credential existing, string  user,
                                    string     pass,     string? domain = null)
    {
        if (existing.Required().Parent is null)
            throw new
                InvalidOperationException("The credential provided is not owned by a Silo.");

        if (existing.Secret is { } s && existing.Parent.Data.ItemExists(s))
        {
            // update secret
            var ns = s.UpdateCredential(user, pass, domain);
            return new Credential(ns);
        }

        return Create(existing.Parent, user, pass, domain);
    }
}