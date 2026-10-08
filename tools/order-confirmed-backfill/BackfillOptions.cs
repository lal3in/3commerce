using System.Globalization;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill;

/// <summary>Command line. Sending is never the default: exactly one of <c>--dry-run</c> / <c>--execute</c> is required.</summary>
public sealed record BackfillOptions(
    IReadOnlyList<BackfillTarget> Targets,
    bool Execute,
    IReadOnlySet<Guid> Tenants,
    int? Limit,
    bool List,
    bool IncludeUnprovenEmail,
    bool SkipPreflight,
    EmailMatchOptions EmailMatch)
{
    public const string Usage = """
        order-confirmed-backfill — re-deliver OrderConfirmed to the consumer that missed it (ADR-0060)

          --target fulfillment|notifications|both   which consumer(s) to backfill (required)
          --dry-run | --execute                     report only / actually send (exactly one is required)
          --tenant <guid>                           only orders of this tenant (repeatable; default: all)
          --limit <n>                               send at most n orders per target (canary runs)
          --list                                    print every selected order (id, number, tenant — no emails)
          --email-window <seconds>                  legacy delivery-log time match window (default 1)
          --email-skew <seconds>                    allowed clock skew, delivery before order (default 1)
          --email-orphan-reach <minutes>            how far back an unexplained email may belong (default 30)
          --include-unproven-email                  ALSO email Ambiguous / PredatesDeliveryLog orders — this
                                                    re-emails some customers who already got their email
          --skip-preflight                          send without the RabbitMQ management checks (not advised)

        Connections come from backfill.settings.json next to the binary, overridable by environment variables
        with the BACKFILL_ prefix (e.g. BACKFILL_ConnectionStrings__Ordering). Secrets are never printed.
        """;

    public static BackfillOptions Parse(IReadOnlyList<string> args)
    {
        var targets = new List<BackfillTarget>();
        bool? execute = null;
        var tenants = new HashSet<Guid>();
        int? limit = null;
        var list = false;
        var includeUnproven = false;
        var skipPreflight = false;
        var match = EmailMatchOptions.Default;

        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new BackfillUsageException($"{args[i]} needs a value");

            switch (args[i])
            {
                case "--target":
                    targets = Value() switch
                    {
                        "fulfillment" => [BackfillTarget.Fulfillment],
                        "notifications" => [BackfillTarget.Notifications],
                        "both" => [BackfillTarget.Fulfillment, BackfillTarget.Notifications],
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
                case "--email-window":
                    match = match with { Window = TimeSpan.FromSeconds(PositiveInt(Value(), "--email-window")) };
                    break;
                case "--email-skew":
                    match = match with { Skew = TimeSpan.FromSeconds(NonNegativeInt(Value(), "--email-skew")) };
                    break;
                case "--email-orphan-reach":
                    match = match with { OrphanReach = TimeSpan.FromMinutes(PositiveInt(Value(), "--email-orphan-reach")) };
                    break;
                case "--include-unproven-email":
                    includeUnproven = true;
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

        return new BackfillOptions(targets, execute.Value, tenants, limit, list, includeUnproven, skipPreflight, match);
    }

    private static int PositiveInt(string value, string name) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : throw new BackfillUsageException($"{name} needs a positive integer");

    private static int NonNegativeInt(string value, string name) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new BackfillUsageException($"{name} needs a non-negative integer");
}

public sealed class BackfillUsageException(string message) : Exception(message);
