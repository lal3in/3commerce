using System.Text.RegularExpressions;

namespace ThreeCommerce.Entity.Domain;

/// <summary>
/// A tenant's supported currency (currency_1) — the managed registry that gates which currency codes the
/// platform will price/sell/settle in, and carries each one's display metadata. The Entity service owns it
/// as tenant reference/master data (ADR-0027); other services validate against a projected read model.
/// <see cref="DecimalPlaces"/> is honoured across all money display + parsing (currency_3), so JPY (0),
/// USD (2) and KWD (3) all render and round correctly.
/// </summary>
public sealed partial class Currency
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }

    /// <summary>ISO-4217 alphabetic code, uppercase (e.g. USD, JPY). Unique per tenant.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Display name (e.g. "US Dollar").</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Currency symbol for display (e.g. "$", "¥", "kr"). Falls back to the code when blank.</summary>
    public string Symbol { get; private set; } = string.Empty;

    /// <summary>Fraction digits: how many decimals a major amount has (USD=2, JPY=0, KWD=3). 0–4.</summary>
    public int DecimalPlaces { get; private set; } = 2;

    /// <summary>Enabled currencies can be chosen for new storefronts/prices; disabling is forward-only —
    /// history keeps displaying (the dashboards are data-driven), the code just can't be newly selected.</summary>
    public bool Enabled { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Currency() { }

    public static Currency Create(Guid tenantId, string code, string name, string symbol, int decimalPlaces, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainRuleException("TenantId is required.");
        }

        var normalizedCode = NormalizeCode(code);
        return new Currency
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Code = normalizedCode,
            Name = NormalizeName(name),
            Symbol = symbol?.Trim() ?? string.Empty,
            DecimalPlaces = ValidateDecimals(decimalPlaces),
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Edit the display metadata. The code is immutable (it identifies the currency); to change a
    /// code you disable the old one and add a new one, so historical postings keep referencing a stable code.</summary>
    public void UpdateDetails(string name, string symbol, int decimalPlaces, DateTimeOffset now)
    {
        Name = NormalizeName(name);
        Symbol = symbol?.Trim() ?? string.Empty;
        DecimalPlaces = ValidateDecimals(decimalPlaces);
        UpdatedAt = now;
    }

    public void Enable(DateTimeOffset now)
    {
        Enabled = true;
        UpdatedAt = now;
    }

    public void Disable(DateTimeOffset now)
    {
        Enabled = false;
        UpdatedAt = now;
    }

    private static string NormalizeCode(string code)
    {
        var value = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(value))
        {
            throw new DomainRuleException("Currency code must be a 3-letter ISO-4217 alphabetic code.");
        }

        return value;
    }

    private static string NormalizeName(string name)
    {
        var value = (name ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new DomainRuleException("Currency name is required.");
        }

        return value;
    }

    private static int ValidateDecimals(int decimalPlaces)
    {
        if (decimalPlaces is < 0 or > 4)
        {
            throw new DomainRuleException("Decimal places must be between 0 and 4.");
        }

        return decimalPlaces;
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CodePattern();
}
