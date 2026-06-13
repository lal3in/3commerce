namespace ThreeCommerce.Identity.Domain;

/// <summary>
/// The replaceability seam required by ADR-0012: if a security audit fails the
/// custom implementation, this interface is reimplemented over ASP.NET Identity
/// or an external IdP without touching callers.
/// All failure results are deliberately generic — no user enumeration.
/// </summary>
public interface IAuthService
{
    /// <summary>Always behaves identically whether or not the email already exists.</summary>
    public Task<RegisterResult> RegisterAsync(string email, string password, CancellationToken ct);

    public Task<LoginResult?> LoginAsync(string email, string password, CancellationToken ct);

    public Task LogoutAsync(string rawSessionToken, CancellationToken ct);

    public Task<bool> VerifyEmailAsync(string rawToken, CancellationToken ct);

    /// <summary>Always succeeds from the caller's perspective (no enumeration).</summary>
    public Task RequestPasswordResetAsync(string email, CancellationToken ct);

    /// <summary>On success revokes ALL of the user's sessions.</summary>
    public Task<bool> ConfirmPasswordResetAsync(string rawToken, string newPassword, CancellationToken ct);

    /// <summary>Gateway-only: resolves an opaque session token to claims.</summary>
    public Task<SessionInfo?> IntrospectAsync(string rawSessionToken, CancellationToken ct);
}

public sealed record RegisterResult(bool IsNewUser);

public sealed record LoginResult(string RawSessionToken, Guid UserId, string Role, DateTimeOffset ExpiresAt);

public sealed record SessionInfo(Guid SessionId, Guid UserId, string Role, DateTimeOffset ExpiresAt);
