namespace Silo.DbModel.Global;

public class StubDb
{
}

/// <summary>
/// Provides key material protected by a password.
/// </summary>
public record Password(int Id, string Sid, byte[] Material);

/// <summary>
/// Provides key material protected by a user's public key
/// </summary>
public record UserKey(int Id, string Sid, byte[] Material);