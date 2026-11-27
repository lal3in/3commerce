using System.Globalization;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ThreeCommerce.BuildingBlocks.Contracts.Ordering;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Support.Infrastructure;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.SupportOwners;

/// <summary>Loads Support's ownerless snapshots and Ordering's view of them, and plans. Reads only.</summary>
public sealed class SupportOrderOwnerRunner(SupportSnapshotSource snapshots, OrderOwnerSource orders)
{
    public async Task<SupportOrderOwnerPlan> PlanAsync(SupportOrderOwnerOptions options, CancellationToken ct)
    {
        var ownerless = await snapshots.OwnerlessAsync(ct);
        var facts = ownerless.Count == 0 ? [] : await orders.OwnersAsync(ownerless, ct);
        return SupportOrderOwnerPlanner.Plan(ownerless, facts, options.Tenants, options.Limit);
    }

    /// <summary>The events to send: each selected order rebuilt by <see cref="OrderConfirmedFactory"/> — the mapping the
    /// live confirmation publishes with — so the copy Support receives is the event the order would have produced.</summary>
    public async Task<List<OrderConfirmed>> MessagesAsync(SupportOrderOwnerPlan plan, CancellationToken ct)
    {
        var messages = new List<OrderConfirmed>();
        foreach (var tenant in plan.ToSend.GroupBy(v => v.TenantId!.Value))
        {
            var loaded = await orders.OrdersAsync(tenant.Key, tenant.Select(v => v.OrderId).ToList(), ct);
            messages.AddRange(loaded.OrderBy(o => o.Id).Select(OrderConfirmedFactory.From));
        }

        return messages;
    }

    /// <summary>The human report: order / tenant ids and verdicts — never an email address, never a secret.</summary>
    public static void WriteReport(SupportOrderOwnerPlan plan, SupportOrderOwnerOptions options, TextWriter output)
    {
        var inv = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(inv, $"ownerless support snapshots         : {plan.Verdicts.Count}"));
        output.WriteLine(string.Create(inv, $"  guest order (NOT sent)            : {plan.Count(OwnerOutcome.Guest)}"));
        output.WriteLine(string.Create(inv, $"  no order in Ordering (NOT sent)   : {plan.Count(OwnerOutcome.NoOrder)}"));
        output.WriteLine(string.Create(inv, $"  MISSING owner (to send)           : {plan.Missing}")
            + (plan.ToSend.Count < plan.Missing ? string.Create(inv, $"  (sending {plan.ToSend.Count} — --limit)") : string.Empty));
        output.WriteLine(string.Create(inv, $"→ queue:{SupportOrderOwnerQueues.QueueName}"));

        if (!options.List || plan.Verdicts.Count == 0)
        {
            return;
        }

        output.WriteLine();
        foreach (var v in plan.Verdicts)
        {
            output.WriteLine(string.Create(inv, $"  {v.Outcome,-8} order={v.OrderId} tenant={(v.TenantId is { } t ? t.ToString() : "-")}"));
        }
    }
}

/// <summary>
/// The <c>support-order-owner</c> command (<c>support_order_ownership</c>): fills the owner of Support's order copies
/// projected before <c>OrderConfirmed.UserId</c> existed. Exit codes as the other backfills: 0 ok, 1 error, 2 usage,
/// 3 precondition/preflight refused.
/// </summary>
public static class SupportOrderOwnerProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        SupportOrderOwnerOptions options;
        try
        {
            options = SupportOrderOwnerOptions.Parse(args);
        }
        catch (BackfillUsageException ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}\n\n{SupportOrderOwnerOptions.Usage}");
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
            Console.WriteLine($"support       : {BackfillProgram.Describe(Connection("Support"))}");
            Console.WriteLine($"ordering      : {BackfillProgram.Describe(Connection("Ordering"))}");
            Console.WriteLine($"mode          : {(options.Execute ? "EXECUTE (sends)" : "dry run (sends nothing)")}"
                + (options.Tenants.Count > 0 ? $", tenants {string.Join(",", options.Tenants)}" : ", all tenants"));
            Console.WriteLine();

            await using var provider = BuildServices(Connection);
            await using var scope = provider.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<SupportOrderOwnerRunner>();
            var plan = await runner.PlanAsync(options, cts.Token);

            SupportOrderOwnerRunner.WriteReport(plan, options, Console.Out);
            if (!options.Execute)
            {
                Console.WriteLine("\ndry run: nothing sent.");
                return 0;
            }

            return await ExecuteAsync(runner, plan, options, config, Connection("RabbitMq"), cts.Token);
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
        SupportOrderOwnerRunner runner, SupportOrderOwnerPlan plan, SupportOrderOwnerOptions options, IConfiguration config,
        string rabbitMq, CancellationToken ct)
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
                SupportOrderOwnerQueues.QueueName, SupportOrderOwnerQueues.ExpectedConnectionName, ct);
            if (problems.Count > 0)
            {
                throw new BackfillPreconditionException("broker preflight failed:\n  " + string.Join("\n  ", problems));
            }

            Console.WriteLine("\nbroker preflight ok.");
        }

        var messages = await runner.MessagesAsync(plan, ct);
        await using var busProvider = BackfillProgram.SendBus(rabbitMq);
        var bus = busProvider.GetRequiredService<IBusControl>();
        await bus.StartAsync(ct);
        try
        {
            Console.WriteLine($"\nsending {messages.Count} to queue:{SupportOrderOwnerQueues.QueueName} …");
            var sent = await new SupportOrderOwnerSender(bus).SendAsync(messages, ct);
            Console.WriteLine($"  sent {sent}.");
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }

        Console.WriteLine("\ndone. Once the queue drains, re-run with --dry-run: MISSING owner should be 0.");
        return 0;
    }

    private static ServiceProvider BuildServices(Func<string, string> connection)
    {
        var services = new ServiceCollection();
        // EF model-validation warnings of the services' own models are not the tool's.
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning)
            .AddFilter("Microsoft.EntityFrameworkCore.Model.Validation", LogLevel.Error));
        services.AddDbContext<SupportDbContext>(o => o.UseNpgsql(connection("Support")));
        services.AddDbContext<OrderingDbContext>(o => o.UseNpgsql(connection("Ordering")));
        services.AddSingleton<ITenantScope, RlsTenantScope>();
        services.AddScoped<SupportSnapshotSource>();
        services.AddScoped<OrderOwnerSource>();
        services.AddScoped<SupportOrderOwnerRunner>();
        return services.BuildServiceProvider();
    }
}
