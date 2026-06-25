using System.Security.Cryptography;
using System.Text;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Export;

/// <summary>
/// Anonymization for data-subject erasure (mt6_8 GOTCHA). "Deletion" means redact/anonymize where the
/// record must legally be retained (orders, ledger, audit) — the row stays, the PII is removed. A stable
/// <see cref="Pseudonym"/> keeps retained rows correlatable for accounting/audit without identity.
/// </summary>
public static class Redaction
{
    public const string Placeholder = "[redacted]";

    /// <summary>Mask the local part, keep the domain (enough to reconcile a record, not to identify a person).</summary>
    public static string Email(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return Placeholder;
        }

        var at = email.IndexOf('@');
        return at > 0 ? $"{Placeholder}{email[at..]}" : Placeholder;
    }

    public static string Name(string? _) => Placeholder;

    /// <summary>Keep only the last few digits of a phone number.</summary>
    public static string Phone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return Placeholder;
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length <= 4 ? Placeholder : $"***{digits[^4..]}";
    }

    /// <summary>
    /// A stable, opaque pseudonym for a data subject — the same input always yields the same token (so a
    /// retained order still ties to its accounting), but it cannot be reversed to the original id.
    /// </summary>
    public static string Pseudonym(string secret, string subjectId)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(subjectId));
        return Convert.ToHexStringLower(mac)[..16];
    }
}
