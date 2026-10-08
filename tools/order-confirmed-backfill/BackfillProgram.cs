using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.Fulfillment.Infrastructure;
using ThreeCommerce.Ordering.Infrastructure;
using ThreeCommerce.Workers.Notifications.Infrastructure;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill;

/// <summary>
/// Operator tool: re-delivers <c>OrderConfirmed</c> to the consumer that never got it while Fulfillment and
/// Notifications shared the <c>order-confirmed</c> queue (ADR-0060). Reads Ordering (source of truth), Fulfillment
/// and the Notifications delivery log; rebuilds each event with <see cref="OrderConfirmedFactory"/>; SENDS it to the
/// one queue that missed it. Exit codes: 0 ok, 1 error, 2 usage, 3 precondition/preflight refused.
/// </summary>
public static class BackfillProgram
{
    public static async Task<int> Main(string[] args)
    {
        BackfillOptions options;
        try
        {
            options = BackfillOptions.Parse(args);
        }
        catch (BackfillUsageException ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}\n\n{BackfillOptions.Usage}");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("backfill.settings.json", optional: false)
            .AddEnvironmentVariables("BACKFILL_")
            .Build();

        string Connection(string name) => config.GetConnectionString(name)
            ?? throw new BackfillPreconditionException($"ConnectionStrings:{name} is not configured");

        try
        {
            Console.WriteLine($"ordering      : {Describe(Connection("Ordering"))}");
            Console.WriteLine($"fulfillment   : {Describe(Connection("Fulfillment"))}");
            Console.WriteLine($"notifications : {Describe(Connection("Notifications"))}");
            Console.WriteLine($"mode          : {(options.Execute ? "EXECUTE (sends)" : "dry run (sends nothing)")}"
                + (options.Tenants.Count > 0 ? $", tenants {string.Join(",", options.Tenants)}" : ", all tenants"));
            Console.WriteLine();

            await using var provider = BuildServices(config, Connection);
            BackfillRun run;
            await using (var scope = provider.CreateAsyncScope())
            {
                run = await scope.ServiceProvider.GetRequiredService<BackfillRunner>().PlanAsync(options, cts.Token);
            }

            BackfillRunner.WriteReport(run, options, Console.Out);
            if (!options.Execute)
            {
                Console.WriteLine("\ndry run: nothing sent.");
                return 0;
            }

            return await ExecuteAsync(run, options, config, Connection("RabbitMq"), cts.Token);
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
        BackfillRun run, BackfillOptions options, IConfiguration config, string rabbitMq, CancellationToken ct)
    {
        var batches = options.Targets
            .Select(t => (Target: t, Orders: t == BackfillTarget.Fulfillment ? run.Plan.Fulfillment : run.Plan.Notifications))
            .Where(b => b.Orders.Count > 0)
            .ToList();
        if (batches.Count == 0)
        {
            Console.WriteLine("\nnothing to send.");
            return 0;
        }

        if (!options.SkipPreflight)
        {
            using var management = ManagementClient(config);
            var preflight = new BrokerPreflight(management);
            var problems = new List<string>();
            foreach (var (target, _) in batches)
            {
                problems.AddRange(await preflight.CheckAsync(target, ct));
            }

            if (problems.Count > 0)
            {
                throw new BackfillPreconditionException("broker preflight failed:\n  " + string.Join("\n  ", problems));
            }

            Console.WriteLine("\nbroker preflight ok.");
        }

        var busServices = new ServiceCollection();
        busServices.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        busServices.AddServiceBus(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:RabbitMq"] = rabbitMq })
            .Build());
        await using var busProvider = busServices.BuildServiceProvider();
        var bus = busProvider.GetRequiredService<IBusControl>();
        await bus.StartAsync(ct);
        try
        {
            var sender = new BackfillSender(bus);
            foreach (var (target, orders) in batches)
            {
                Console.WriteLine($"\nsending {orders.Count} to queue:{BackfillQueues.QueueName(target)} …");
                var sent = await sender.SendAsync(
                    target,
                    orders.Select(o => OrderConfirmedFactory.From(run.Orders[o.OrderId])),
                    n =>
                    {
                        if (n % 100 == 0 || n == orders.Count)
                        {
                            Console.WriteLine($"  {n}/{orders.Count}");
                        }
                    },
                    ct);
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

    private static ServiceProvider BuildServices(IConfiguration config, Func<string, string> connection)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<OrderingDbContext>(o => o.UseNpgsql(connection("Ordering")));
        services.AddDbContext<FulfillmentDbContext>(o => o.UseNpgsql(connection("Fulfillment")));
        services.AddDbContext<NotificationsDbContext>(o => o.UseNpgsql(connection("Notifications")));
        services.AddSingleton<ITenantScope, RlsTenantScope>();
        services.AddScoped<OrderSource>();
        services.AddScoped<FulfillmentSource>();
        services.AddScoped<DeliverySource>();
        services.AddScoped<BackfillRunner>();
        return services.BuildServiceProvider();
    }

    private static HttpClient ManagementClient(IConfiguration config)
    {
        var url = config["RabbitMqManagement:Url"] ?? "http://localhost:15672/";
        var user = config["RabbitMqManagement:Username"] ?? "guest";
        var password = config["RabbitMqManagement:Password"] ?? "guest";
        var client = new HttpClient { BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/"), Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        return client;
    }

    /// <summary>host:port/database as user — never the password.</summary>
    private static string Describe(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        return string.Create(CultureInfo.InvariantCulture, $"{b.Host}:{b.Port}/{b.Database} as {b.Username}");
    }
}
