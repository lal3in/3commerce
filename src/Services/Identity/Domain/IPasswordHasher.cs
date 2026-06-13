namespace ThreeCommerce.Identity.Domain;

/// <summary>
/// Implementations MUST use a vetted memory-hard algorithm (Argon2id) — hand-rolled
/// crypto is prohibited (ADR-0012).
/// </summary>
public interface IPasswordHasher
{
    public string Hash(string password);
    public bool Verify(string password, string encodedHash);
}
