using System.Globalization;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.PaymentCustomers;

/// <summary>
/// Command line of the <c>payment-customer-email</c> command. Same safety rules as the other backfills: sending is never
/// the default — exactly one of <c>--dry-run</c> / <c>--execute</c> is required.
/// </summary>
public sealed record PaymentCustomerEmailOptions(bool Execute, IReadOnlySet<Guid> Tenants, int? Limit, bool List, bool SkipPreflight)
{
    public const string Command = "payment-customer-email";

    public const string Usage = """
        order-confirmed-backfill payment-customer-email — give Payments vaults opened before #332 their member's email
        (payment_customer_email_backfill). A usage auto top-up finds the member's default saved card by tenant + email;
        a vault that never recorded one hides the card, and the top-up is skipped. Reads each vault's account email from
        Identity and SENDS it to Payments, which records it only on a vault that has none.

          --dry-run | --execute                     report only / actually send (exactly one is required)
          --tenant <guid>                           only this tenant's vaults (repeatable; default: all)
          --limit <n>                               send at most n members (canary runs)
          --list                                    print every vault and its verdict (ids only — never an email)
          --skip-preflight                          send without the RabbitMQ management checks (not advised)

        Connections come from backfill.settings.json next to the binary, overridable by environment variables
        with the BACKFILL_ prefix (e.g. BACKFILL_ConnectionStrings__Identity). Secrets are never printed.
        """;

    public static PaymentCustomerEmailOptions Parse(IReadOnlyList<string> args)
    {
        bool? execute = null;
        var tenants = new HashSet<Guid>();
        int? limit = null;
        var list = false;
        var skipPreflight = false;

        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new BackfillUsageException($"{args[i]} needs a value");

            switch (args[i])
            {
                case "--dry-run":
                    execute = execute is true ? throw new BackfillUsageException("--dry-run and --execute are exclusive") : false;
                    break;
                case "--execute":
                    execute = execute is false ? throw new BackfillUsageException("--dry-run and --execute are exclusive") : true;
                    break;
                case "--tenant":
                    tenants.Add(Guid.TryParse(Value(), out var tenant) ? tenant : throw new BackfillUsageException("--tenant needs a GUID"));
                    break;
                case "--limit":
                    limit = int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                        ? n
                        : throw new BackfillUsageException("--limit needs a positive integer");
                    break;
                case "--list":
                    list = true;
                    break;
                case "--skip-preflight":
                    skipPreflight = true;
                    break;
                default:
                    throw new BackfillUsageException($"unknown argument '{args[i]}'");
            }
        }

        return execute is null
            ? throw new BackfillUsageException("one of --dry-run or --execute is required")
            : new PaymentCustomerEmailOptions(execute.Value, tenants, limit, list, skipPreflight);
    }
}
