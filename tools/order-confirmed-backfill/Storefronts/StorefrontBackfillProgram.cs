using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ThreeCommerce.Audit.Infrastructure;
using ThreeCommerce.Catalog.Infrastructure;
using ThreeCommerce.Fulfillment.Infrastructure;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.Storefronts;

/// <summary>
/// The <c>storefront-duplicated</c> command: re-delivers <c>StorefrontDuplicated</c> to the consumer whose copy was lost
/// while Payments and Fulfillment shared the <c>storefront-duplicated</c> queue (ADR-0060). Reads Catalog, the audit
/// timeline, Payments and Fulfillment; SENDS a rebuilt event to the one queue whose copy is missing. Exit codes as the
/// order backfill: 0 ok, 1 error, 2 usage, 3 precondition/preflight refused.
/// </summary>
public static class StorefrontBackfillProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        StorefrontBackfillOptions options;
        try
        {
            options = StorefrontBackfillOptions.Parse(args);
        }
        catch (BackfillUsageException ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}\n\n{StorefrontBackfillOptions.Usage}");
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
            Console.WriteLine($"catalog       : {BackfillProgram.Describe(Connection("Catalog"))}");
            Console.WriteLine($"audit         : {BackfillProgram.Describe(Connection("Audit"))}");
            Console.WriteLine($"payments      : {BackfillProgram.Describe(Connection("Payments"))}");
            Console.WriteLine($"fulfillment   : {BackfillProgram.Describe(Connection("Fulfillment"))}");
            Console.WriteLine($"mode          : {(options.Execute ? "EXECUTE (sends)" : "dry run (sends nothing)")}"
                + (options.Tenants.Count > 0 ? $", tenants {string.Join(",", options.Tenants)}" : ", all tenants")
                + $", clone window {options.CloneWindow.TotalSeconds}s");
            Console.WriteLine();

            await using var provider = BuildServices(Connection);
            StorefrontBackfillPlan plan;
            await using (var scope = provider.CreateAsyncScope())
            {
                plan = await scope.ServiceProvider.GetRequiredService<StorefrontBackfillRunner>().PlanAsync(options, cts.Token);
            }

            StorefrontBackfillRunner.WriteReport(plan, options, Console.Out);
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
        StorefrontBackfillPlan plan, StorefrontBackfillOptions options, IConfiguration config, string rabbitMq, CancellationToken ct)
    {
        var batches = options.Targets
            .Select(t => (Target: t, Verdicts: plan.Selected(t)))
            .Where(b => b.Verdicts.Count > 0)
            .ToList();
        if (batches.Count == 0)
        {
            Console.WriteLine("\nnothing to send.");
            return 0;
        }

        if (!options.SkipPreflight)
        {
            using var management = BackfillProgram.ManagementClient(config);
            var preflight = new BrokerPreflight(management);
            var problems = new List<string>();
            foreach (var (target, _) in batches)
            {
                problems.AddRange(await preflight.CheckAsync(
                    StorefrontBackfillQueues.QueueName(target), StorefrontBackfillQueues.ExpectedConnectionName(target), ct));
            }

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
            var sender = new StorefrontBackfillSender(bus);
            foreach (var (target, verdicts) in batches)
            {
                Console.WriteLine($"\nsending {verdicts.Count} to queue:{StorefrontBackfillQueues.QueueName(target)} …");
                var sent = await sender.SendAsync(target, verdicts.Select(v => StorefrontBackfillQueues.Rebuild(v, target)), ct);
                Console.WriteLine($"  sent {sent} ({target}).");
            }
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }

        Console.WriteLine("\ndone. Once the queues drain, re-run with --dry-run: every target should report 0 to send.");
        return 0;
    }

    private static ServiceProvider BuildServices(Func<string, string> connection)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<CatalogDbContext>(o => o.UseNpgsql(connection("Catalog")));
        services.AddDbContext<AuditDbContext>(o => o.UseNpgsql(connection("Audit")));
        services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(connection("Payments")));
        services.AddDbContext<FulfillmentDbContext>(o => o.UseNpgsql(connection("Fulfillment")));
        services.AddSingleton<ITenantScope, RlsTenantScope>();
        services.AddScoped<DuplicationAuditSource>();
        services.AddScoped<StorefrontCatalogSource>();
        services.AddScoped<PaymentAccountSource>();
        services.AddScoped<CarrierIntegrationSource>();
        services.AddScoped<StorefrontBackfillRunner>();
        return services.BuildServiceProvider();
    }
}
