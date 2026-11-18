using Microsoft.Extensions.Logging;

namespace ThreeCommerce.Identity.Infrastructure;

/// <summary>
/// Structured SECURITY log lines of the authentication flows (ADR-0065 Q2: kept 12 months). Written through
/// <c>ILogger&lt;AuthService&gt;</c>, whose category <c>ThreeCommerce.Identity.Infrastructure.AuthService</c> the
/// collector's <c>transform/security_category</c> labels <c>log_category=security</c>. Opaque ids only — the user and
/// tenant GUIDs, counts and times — NEVER the email, the password or a code.
/// </summary>
internal static partial class AuthSecurityLog
{
    [LoggerMessage(EventId = 4101, EventName = "LoginUnknownAccount", Level = LogLevel.Warning,
        Message = "Security: login failed — no such account (tenant {TenantId})")]
    public static partial void LoginUnknownAccount(this ILogger logger, Guid tenantId);

    [LoggerMessage(EventId = 4102, EventName = "LoginWrongPassword", Level = LogLevel.Warning,
        Message = "Security: login failed — wrong password (user {UserId}, tenant {TenantId}, failed attempts {FailedAttempts})")]
    public static partial void LoginWrongPassword(this ILogger logger, Guid userId, Guid? tenantId, int failedAttempts);

    [LoggerMessage(EventId = 4103, EventName = "LoginRefusedLockedOut", Level = LogLevel.Warning,
        Message = "Security: login refused — account locked out until {LockedUntil:o} (user {UserId}, tenant {TenantId})")]
    public static partial void LoginRefusedLockedOut(this ILogger logger, Guid userId, Guid? tenantId, DateTimeOffset lockedUntil);

    [LoggerMessage(EventId = 4104, EventName = "AccountLockedOut", Level = LogLevel.Warning,
        Message = "Security: account locked out after {FailedAttempts} failed attempts until {LockedUntil:o} (user {UserId}, tenant {TenantId}, via {Factor})")]
    public static partial void AccountLockedOut(this ILogger logger, Guid userId, Guid? tenantId, int failedAttempts, DateTimeOffset lockedUntil, string factor);

    [LoggerMessage(EventId = 4105, EventName = "LoginSucceeded", Level = LogLevel.Information,
        Message = "Security: login succeeded (user {UserId}, tenant {TenantId}, MFA pending {MfaPending})")]
    public static partial void LoginSucceeded(this ILogger logger, Guid userId, Guid? tenantId, bool mfaPending);

    [LoggerMessage(EventId = 4106, EventName = "MfaChallengeFailed", Level = LogLevel.Warning,
        Message = "Security: MFA challenge failed — wrong code (user {UserId}, tenant {TenantId}, failed attempts {FailedAttempts})")]
    public static partial void MfaChallengeFailed(this ILogger logger, Guid userId, Guid? tenantId, int failedAttempts);

    [LoggerMessage(EventId = 4107, EventName = "MfaChallengeRefusedLockedOut", Level = LogLevel.Warning,
        Message = "Security: MFA challenge refused — account locked out until {LockedUntil:o} (user {UserId}, tenant {TenantId})")]
    public static partial void MfaChallengeRefusedLockedOut(this ILogger logger, Guid userId, Guid? tenantId, DateTimeOffset lockedUntil);

    [LoggerMessage(EventId = 4108, EventName = "MfaStepUpFailed", Level = LogLevel.Warning,
        Message = "Security: MFA step-up failed — wrong code or no confirmed factor (user {UserId}, tenant {TenantId})")]
    public static partial void MfaStepUpFailed(this ILogger logger, Guid userId, Guid? tenantId);

    [LoggerMessage(EventId = 4109, EventName = "MfaEnrollmentConfirmFailed", Level = LogLevel.Warning,
        Message = "Security: MFA enrollment confirmation failed — wrong code (user {UserId}, tenant {TenantId})")]
    public static partial void MfaEnrollmentConfirmFailed(this ILogger logger, Guid userId, Guid? tenantId);
}
