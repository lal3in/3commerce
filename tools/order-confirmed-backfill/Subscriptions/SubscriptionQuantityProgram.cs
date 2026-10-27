using System.Globalization;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Subscriptions;

/// <summary>Loads Ordering and Payments per tenant and plans. Reads only — sending is the caller's step.</summary>
public sealed class SubscriptionQuantityRunner(OrderSource orders, PaymentsSubscriptionSource subscriptions)
{
    public async Task<SubscriptionQuantityPlan> PlanAsync(SubscriptionQuantityOptions options, CancellationToken ct)
    {
        var verdicts = new List<QuantityVerdict>();
        foreach (var tenant in (await orders.TenantsAsync(ct)).Where(t => options.Tenants.Count == 0 || options.Tenants.Contains(t)))
        {
            var tenantOrders = await orders.ConfirmedOrdersAsync(tenant, ct);
            if (!tenantOrders.Any(o => o.Lines.Any(l => l.Quantity > 1)))
            {
                continue;
            }

            verdicts.AddRange(SubscriptionQuantityPlanner.Plan(tenant, tenantOrders, await subscriptions.SubscriptionsAsync(tenant, ct)));
        }

        return SubscriptionQuantityPlanner.From(verdicts, options.Limit);
    }

    /// <summary>The human report: order / subscription ids, tenants and prices — no customer emails, no secrets.</summary>
    public static void WriteReport(SubscriptionQuantityPlan plan, SubscriptionQuantityOptions options, TextWriter output)
    {
        var inv = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(inv, $"multi-unit subscription lines      : {plan.Verdicts.Count}"));
        output.WriteLine(string.Create(inv, $"  already carry their quantity     : {plan.Count(QuantityOutcome.AlreadyCorrect)}"));
        output.WriteLine(string.Create(inv, $"  no subscription in Payments      : {plan.Count(QuantityOutcome.NoSubscription)}"));
        output.WriteLine(string.Create(inv, $"  cancelled (never renews)         : {plan.Count(QuantityOutcome.Cancelled)}"));
        output.WriteLine(string.Create(inv, $"  unexpected price (NOT sent)      : {plan.Count(QuantityOutcome.UnexpectedPrice)}"));
        var toCorrect = plan.Count(QuantityOutcome.ToCorrect);
        output.WriteLine(string.Create(inv, $"  TO CORRECT (to send)             : {toCorrect}")
            + (plan.ToSend.Count < toCorrect ? string.Create(inv, $"  (sending {plan.ToSend.Count} — --limit)") : string.Empty));
        output.WriteLine(string.Create(inv, $"→ queue:{SubscriptionQuantityQueues.QueueName}"));

        var shown = options.List
            ? plan.Verdicts
            : plan.Verdicts.Where(v => v.Outcome == QuantityOutcome.UnexpectedPrice).ToList();
        if (shown.Count == 0)
        {
            return;
        }

        output.WriteLine();
        foreach (var v in shown)
        {
            var current = v.Subscription is { } s ? string.Create(inv, $"{s.Id} qty {s.Quantity} at {s.PriceMinor}") : "-";
            output.WriteLine(string.Create(inv,
                $"  {v.Outcome,-15} order={v.Order.Id} #{v.Order.PublicOrderNumber} tenant={v.TenantId} product={v.Line.ProductId} qty={v.Line.Quantity}"
                + $"  subscription={current}  correct={v.CorrectPriceMinor} {v.Order.Currency}"));
        }
    }
}

/// <summary>
/// The <c>subscription-quantity</c> command (owner decision 2026-10-27, <c>subscription_quantity_backfill</c>): re-sends
/// each legacy multi-unit subscription's <c>SubscriptionRequested</c> to Payments. Exit codes as the other backfills:
/// 0 ok, 1 error, 2 usage, 3 precondition/preflight refused.
/// </summary>
public static class SubscriptionQuantityProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        SubscriptionQuantityOptions options;
        try
        {
            options = SubscriptionQuantityOptions.Parse(args);
        }
        catch (BackfillUsageException ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}\n\n{SubscriptionQuantityOptions.Usage}");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var config = BackfillProgram.LoadConfiguration();
        string Connection(string name) => BackfillProgram.ConnectionString(config, name);

        try
        {
            Console.WriteLine($"ordering      : {BackfillProgram.Describe(Connection("Ordering"))}");
            Console.WriteLine($"payments      : {BackfillProgram.Describe(Connection("Payments"))}");
            Console.WriteLine($"mode          : {(options.Execute ? "EXECUTE (sends)" : "dry run (sends nothing)")}"
                + (options.Tenants.Count > 0 ? $", tenants {string.Join(",", options.Tenants)}" : ", all tenants"));
            Console.WriteLine();

            await using var provider = BuildServices(Connection);
            SubscriptionQuantityPlan plan;
            await using (var scope = provider.CreateAsyncScope())
            {
                plan = await scope.ServiceProvider.GetRequiredService<SubscriptionQuantityRunner>().PlanAsync(options, cts.Token);
            }

            SubscriptionQuantityRunner.WriteReport(plan, options, Console.Out);
            if (!options.Execute)
            {
                Console.WriteLine("\ndry run: nothing sent.");
                return 0;
            }

            return await ExecuteAsync(plan, options, config, Connection("RabbitMq"), cts.Token);
        }
        catch (BackfillPreconditionException ex)
        {
            await Console.Error.WriteLineAsync($"refused: {ex.Message}");
            return 3;
        }
        catch (Exception ex) when (ex is NpgsqlException or HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync($"error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(
        SubscriptionQuantityPlan plan, SubscriptionQuantityOptions options, IConfiguration config, string rabbitMq, CancellationToken ct)
    {
        if (plan.ToSend.Count == 0)
        {
            Console.WriteLine("\nnothing to send.");
            return 0;
        }

        if (!options.SkipPreflight)
        {
            using var management = BackfillProgram.ManagementClient(config);
            var problems = await new BrokerPreflight(management).CheckAsync(
                SubscriptionQuantityQueues.QueueName, SubscriptionQuantityQueues.ExpectedConnectionName, ct);
            if (problems.Count > 0)
            {
                throw new BackfillPreconditionException("broker preflight failed:\n  " + string.Join("\n  ", problems));
            }

            Console.WriteLine("\nbroker preflight ok.");
        }

        await using var busProvider = BackfillProgram.SendBus(rabbitMq);
        var bus = busProvider.GetRequiredService<IBusControl>();
        await bus.StartAsync(ct);
        try
        {
            Console.WriteLine($"\nsending {plan.ToSend.Count} to queue:{SubscriptionQuantityQueues.QueueName} …");
            var sent = await new SubscriptionQuantitySender(bus).SendAsync(plan.ToSend, ct);
            Console.WriteLine($"  sent {sent}.");
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }

        Console.WriteLine("\ndone. Once the queue drains, re-run with --dry-run: TO CORRECT should be 0.");
        return 0;
    }

    private static ServiceProvider BuildServices(Func<string, string> connection)
    {
        var services = new ServiceCollection();
        // EF model-validation warnings of the services' own models (e.g. Payments' sentinel notice) are not the tool's.
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning)
            .AddFilter("Microsoft.EntityFrameworkCore.Model.Validation", LogLevel.Error));
        services.AddDbContext<OrderingDbContext>(o => o.UseNpgsql(connection("Ordering")));
        services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(connection("Payments")));
        services.AddSingleton<ITenantScope, RlsTenantScope>();
        services.AddScoped<OrderSource>();
        services.AddScoped<PaymentsSubscriptionSource>();
        services.AddScoped<SubscriptionQuantityRunner>();
        return services.BuildServiceProvider();
    }
}
