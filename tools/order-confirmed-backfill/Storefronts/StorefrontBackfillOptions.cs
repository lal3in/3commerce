using System.Globalization;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Storefronts;

/// <summary>
/// Command line of the <c>storefront-duplicated</c> command. Same safety rules as the order backfill: sending is never
/// the default — exactly one of <c>--dry-run</c> / <c>--execute</c> is required.
/// </summary>
public sealed record StorefrontBackfillOptions(
    IReadOnlyList<StorefrontBackfillTarget> Targets,
    bool Execute,
    IReadOnlySet<Guid> Tenants,
    int? Limit,
    bool List,
    bool SkipPreflight,
    TimeSpan CloneWindow)
{
    public const string Command = "storefront-duplicated";

    public const string Usage = """
        order-confirmed-backfill storefront-duplicated — re-deliver StorefrontDuplicated to the consumer whose copy is
        missing (ADR-0060): Payments (payment accounts) or Fulfillment (carrier integrations)

          --target payments|fulfillment|both        which copy to backfill (required)
          --dry-run | --execute                     report only / actually send (exactly one is required)
          --tenant <guid>                           only storefronts of this tenant (repeatable; default: all)
          --limit <n>                               send at most n storefronts per target (canary runs)
          --list                                    print every duplicated storefront and its verdict
          --clone-window <seconds>                  how long after a duplicate's creation a config row still counts
                                                    as the copy that DID run (default 600)
          --skip-preflight                          send without the RabbitMQ management checks (not advised)

        Connections come from backfill.settings.json next to the binary, overridable by environment variables
        with the BACKFILL_ prefix (e.g. BACKFILL_ConnectionStrings__Catalog). Secrets are never printed.
        """;

    public static StorefrontBackfillOptions Parse(IReadOnlyList<string> args)
    {
        var targets = new List<StorefrontBackfillTarget>();
        bool? execute = null;
        var tenants = new HashSet<Guid>();
        int? limit = null;
        var list = false;
        var skipPreflight = false;
        var window = StorefrontBackfillPlanner.DefaultCloneWindow;

        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new BackfillUsageException($"{args[i]} needs a value");

            switch (args[i])
            {
                case "--target":
                    targets = Value() switch
                    {
                        "payments" => [StorefrontBackfillTarget.Payments],
                        "fulfillment" => [StorefrontBackfillTarget.Fulfillment],
                        "both" => [StorefrontBackfillTarget.Payments, StorefrontBackfillTarget.Fulfillment],
                        var other => throw new BackfillUsageException($"unknown --target '{other}'"),
                    };
                    break;
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
                    limit = PositiveInt(Value(), "--limit");
                    break;
                case "--list":
                    list = true;
                    break;
                case "--clone-window":
                    window = TimeSpan.FromSeconds(PositiveInt(Value(), "--clone-window"));
                    break;
                case "--skip-preflight":
                    skipPreflight = true;
                    break;
                default:
                    throw new BackfillUsageException($"unknown argument '{args[i]}'");
            }
        }

        if (targets.Count == 0)
        {
            throw new BackfillUsageException("--target is required");
        }

        if (execute is null)
        {
            throw new BackfillUsageException("one of --dry-run or --execute is required");
        }

        return new StorefrontBackfillOptions(targets, execute.Value, tenants, limit, list, skipPreflight, window);
    }

    private static int PositiveInt(string value, string name) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : throw new BackfillUsageException($"{name} needs a positive integer");
}
