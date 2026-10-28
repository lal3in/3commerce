using System.Globalization;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Subscriptions;

/// <summary>
/// Command line of the <c>subscription-quantity</c> command. Same safety rules as the other backfills: sending is never
/// the default — exactly one of <c>--dry-run</c> / <c>--execute</c> is required.
/// </summary>
public sealed record SubscriptionQuantityOptions(bool Execute, IReadOnlySet<Guid> Tenants, int? Limit, bool List, bool SkipPreflight)
{
    public const string Command = "subscription-quantity";

    public const string Usage = """
        order-confirmed-backfill subscription-quantity — re-price subscriptions set up before quantity was carried
        (ADR-0057 amendment 2026-10-27): a recurring line of quantity n renewed at ONE unit's price less the whole
        line's renewal discount. Re-sends the line's SubscriptionRequested (unit × n − the line's renewal discount,
        Quantity n) to Payments, which corrects only a still-legacy single-unit subscription.

          --dry-run | --execute                     report only / actually send (exactly one is required)
          --tenant <guid>                           only this tenant's orders (repeatable; default: all)
          --limit <n>                               send at most n subscriptions (canary runs)
          --list                                    print every multi-unit subscription line and its verdict
          --skip-preflight                          send without the RabbitMQ management checks (not advised)

        Connections come from backfill.settings.json next to the binary, overridable by environment variables
        with the BACKFILL_ prefix (e.g. BACKFILL_ConnectionStrings__Payments). Secrets are never printed.
        """;

    public static SubscriptionQuantityOptions Parse(IReadOnlyList<string> args)
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
            : new SubscriptionQuantityOptions(execute.Value, tenants, limit, list, skipPreflight);
    }
}
