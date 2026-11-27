using System.Globalization;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.SupportOwners;

/// <summary>
/// Command line of the <c>support-order-owner</c> command. Same safety rules as the other backfills: sending is never
/// the default — exactly one of <c>--dry-run</c> / <c>--execute</c> is required.
/// </summary>
public sealed record SupportOrderOwnerOptions(bool Execute, IReadOnlySet<Guid> Tenants, int? Limit, bool List, bool SkipPreflight)
{
    public const string Command = "support-order-owner";

    public const string Usage = """
        order-confirmed-backfill support-order-owner — give Support's order copies projected before OrderConfirmed carried
        UserId their owner (support_order_ownership, ADR-0018 amendment 2026-11-27). Support's order-scoped customer
        endpoints (refundable lines, RMA, the order's tickets) serve only the order's owner and answer 404 on a snapshot
        without one, so until this runs a signed-in customer cannot self-serve a refund on an older order. Finds every
        ownerless snapshot, looks its order up in Ordering and SENDS the rebuilt OrderConfirmed to Support's own snapshot
        queue, whose consumer fills a missing owner and changes nothing else. Guest orders (no user) are reported, never sent.

          --dry-run | --execute                     report only / actually send (exactly one is required)
          --tenant <guid>                           only this tenant's orders (repeatable; default: all)
          --limit <n>                               send at most n orders (canary runs)
          --list                                    print every ownerless snapshot and its verdict (ids only — never an email)
          --skip-preflight                          send without the RabbitMQ management checks (not advised)

        Connections come from backfill.settings.json next to the binary, overridable by environment variables
        with the BACKFILL_ prefix (e.g. BACKFILL_ConnectionStrings__Support). Secrets are never printed.
        """;

    public static SupportOrderOwnerOptions Parse(IReadOnlyList<string> args)
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
            : new SupportOrderOwnerOptions(execute.Value, tenants, limit, list, skipPreflight);
    }
}
