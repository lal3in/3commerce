namespace ThreeCommerce.BuildingBlocks.Contracts.Abstractions;

/// <summary>
/// A domain exception that maps to a specific HTTP status and a machine-readable error code
/// (RFC 9457). Implement this on typed failures (payment declines, idempotency conflicts, …) so
/// the shared problem-details handler returns a precise problem+json instead of a bare 500.
/// Lives in Contracts (the lowest shared layer) so domain projects can implement it without a
/// dependency on the web layer. The exception's <see cref="System.Exception.Message"/> is
/// surfaced as the problem Detail, so it MUST be client-safe — never secrets/PII.
/// </summary>
public interface IProblemException
{
    public int StatusCode { get; }
    public string ErrorCode { get; }
}
