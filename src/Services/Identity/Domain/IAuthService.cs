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
    public Task<RegisterResult> RegisterAsync(string email, string password, MemberProfile? profile, CancellationToken ct);

    public Task<LoginResult?> LoginAsync(string email, string password, CancellationToken ct);

    public Task LogoutAsync(string rawSessionToken, CancellationToken ct);

    public Task<bool> VerifyEmailAsync(string rawToken, CancellationToken ct);

    /// <summary>Always succeeds from the caller's perspective (no enumeration).</summary>
    public Task RequestPasswordResetAsync(string email, CancellationToken ct);

    /// <summary>On success revokes ALL of the user's sessions.</summary>
    public Task<bool> ConfirmPasswordResetAsync(string rawToken, string newPassword, CancellationToken ct);

    /// <summary>Gateway-only: resolves an opaque session token to claims. MFA-pending sessions resolve to null.</summary>
    public Task<SessionInfo?> IntrospectAsync(string rawSessionToken, CancellationToken ct);

    // MFA (mt6_10 enforcement half). All token-keyed like Logout — a pending session holds no claims,
    // so these are the only operations it can perform.

    /// <summary>Null when the token resolves to no live session (pending counts as live here).</summary>
    public Task<MfaStatus?> GetMfaStatusAsync(string rawSessionToken, CancellationToken ct);

    /// <summary>Starts (or restarts an unconfirmed) enrollment. Null: no fully-authenticated session, or already confirmed.</summary>
    public Task<MfaEnrollmentStart?> BeginMfaEnrollmentAsync(string rawSessionToken, CancellationToken ct);

    /// <summary>Proves authenticator possession; returns the one-time recovery codes exactly once. Null on failure.</summary>
    public Task<IReadOnlyList<string>?> ConfirmMfaEnrollmentAsync(string rawSessionToken, string code, CancellationToken ct);

    /// <summary>Completes an MFA-pending login with a TOTP code or a recovery code (which is consumed).</summary>
    public Task<bool> CompleteMfaChallengeAsync(string rawSessionToken, string code, CancellationToken ct);

    /// <summary>Re-verifies the factor on an active session, refreshing the StepUp freshness anchor.</summary>
    public Task<bool> StepUpAsync(string rawSessionToken, string code, CancellationToken ct);
}

/// <summary>Structured member profile captured at registration (mem_1). First/Last are the required legal name.</summary>
public sealed record MemberProfile(
    string? Title, string? FirstName, string? MiddleName, string? LastName, string? PreferredName,
    string? Phone, DateOnly? DateOfBirth, bool MarketingConsent);

public sealed record RegisterResult(bool IsNewUser);

public sealed record LoginResult(string RawSessionToken, Guid UserId, string Role, DateTimeOffset ExpiresAt, bool MfaPending = false);

public sealed record SessionInfo(
    Guid SessionId, Guid UserId, Guid TenantId, string Role, string Email, DateTimeOffset ExpiresAt,
    DateTimeOffset? StrongAuthAt = null, string Amr = "pwd");

/// <summary><paramref name="Required"/>: the effective MfaPolicy demands a factor for this user (nag/enroll signal).</summary>
public sealed record MfaStatus(bool Enrolled, bool Pending, bool Required);

public sealed record MfaEnrollmentStart(string SecretBase32, string OtpauthUri);
