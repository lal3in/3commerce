using ThreeCommerce.BuildingBlocks.Contracts.Payments;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.PaymentCustomers;

/// <summary>What the planner decided for one Payments vault.</summary>
public enum EmailOutcome
{
    /// <summary>The vault has no email and Identity has the member's account in the vault's tenant: send it.</summary>
    Missing = 1,

    /// <summary>The vault already knows the account's email — nothing to do (also after a successful run).</summary>
    Recorded = 2,

    /// <summary>
    /// The vault knows a DIFFERENT email than the account now has (the member changed it since they last managed their
    /// cards). Reported, never sent: Payments keeps an email learnt from the member's own claims, and the member's next
    /// visit to their saved cards records the current one.
    /// </summary>
    Differs = 3,

    /// <summary>Identity has no account with the vault's user id in the vault's tenant — reported, never sent.</summary>
    NoAccount = 4,
}

/// <summary>One vault, as the tool reads it from Payments (no secrets: the provider customer id is not read).</summary>
public sealed record VaultFact(Guid VaultId, Guid TenantId, Guid UserId, string Provider, string? Email, DateTimeOffset CreatedAt);

/// <summary>One Identity account (user id + tenant + email) — read only to build the command, never printed.</summary>
public sealed record AccountFact(Guid UserId, Guid TenantId, string Email);

public sealed record EmailVerdict(VaultFact Vault, AccountFact? Account, EmailOutcome Outcome);

/// <summary>Counts and the commands to send (one per member, capped by --limit; the counts are not). Pure — sends nothing.</summary>
public sealed record PaymentCustomerEmailPlan(IReadOnlyList<EmailVerdict> Verdicts, IReadOnlyList<RecordPaymentCustomerEmail> ToSend)
{
    public int Count(EmailOutcome outcome) => Verdicts.Count(v => v.Outcome == outcome);

    /// <summary>Distinct members with a vault missing its email (a member with two such vaults gets one command).</summary>
    public int MembersMissing => Verdicts.Where(v => v.Outcome == EmailOutcome.Missing).Select(v => (v.Vault.TenantId, v.Vault.UserId)).Distinct().Count();
}

/// <summary>
/// Which vaults learn their email (<c>payment_customer_email_backfill</c>). A usage auto top-up finds the member's default
/// saved card by tenant + email (owner decision 2026-11-06); a vault opened before #332 recorded no email until the member
/// next opened their saved cards, so its cards were invisible. Identity owns the account email: the tool reads it there
/// (per tenant, in the tenant's RLS scope) and SENDS it to Payments, which records it only on a vault that has none.
/// </summary>
public static class PaymentCustomerEmailPlanner
{
    public static IEnumerable<EmailVerdict> Plan(IEnumerable<VaultFact> vaults, IReadOnlyCollection<AccountFact> accounts)
    {
        var byUser = accounts.ToLookup(a => (a.TenantId, a.UserId));
        foreach (var vault in vaults.OrderBy(v => v.CreatedAt).ThenBy(v => v.VaultId))
        {
            var account = byUser[(vault.TenantId, vault.UserId)].FirstOrDefault();
            yield return new EmailVerdict(vault, account, Decide(vault, account));
        }
    }

    public static PaymentCustomerEmailPlan From(IEnumerable<EmailVerdict> verdicts, int? limit)
    {
        var all = verdicts.ToList();
        var commands = all
            .Where(v => v.Outcome == EmailOutcome.Missing)
            .DistinctBy(v => (v.Vault.TenantId, v.Vault.UserId))
            .Select(Rebuild);
        return new PaymentCustomerEmailPlan(all, (limit is { } n ? commands.Take(n) : commands).ToList());
    }

    /// <summary>The command Payments receives: the account's email, normalized the way Payments stores it.</summary>
    public static RecordPaymentCustomerEmail Rebuild(EmailVerdict verdict) =>
        verdict is { Outcome: EmailOutcome.Missing, Account: { } account }
            ? new RecordPaymentCustomerEmail(verdict.Vault.TenantId, verdict.Vault.UserId, Normalize(account.Email))
            : throw new InvalidOperationException($"vault {verdict.Vault.VaultId} is not missing its email");

    private static EmailOutcome Decide(VaultFact vault, AccountFact? account) => (vault.Email, account) switch
    {
        (_, null) => EmailOutcome.NoAccount,
        (null, _) => EmailOutcome.Missing,
        (var known, { } a) when known == Normalize(a.Email) => EmailOutcome.Recorded,
        _ => EmailOutcome.Differs,
    };

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
