using System.Globalization;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ThreeCommerce.Identity.Infrastructure;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Tools.OrderConfirmedBackfill.PaymentCustomers;

/// <summary>Loads Payments' vaults and Identity's accounts per tenant and plans. Reads only — sending is the caller's step.</summary>
public sealed class PaymentCustomerEmailRunner(PaymentsVaultSource vaults, IdentityAccountSource accounts)
{
    public async Task<PaymentCustomerEmailPlan> PlanAsync(PaymentCustomerEmailOptions options, CancellationToken ct)
    {
        var verdicts = new List<EmailVerdict>();
        foreach (var tenant in (await vaults.TenantsAsync(ct)).Where(t => options.Tenants.Count == 0 || options.Tenants.Contains(t)))
        {
            var tenantVaults = await vaults.VaultsAsync(tenant, ct);
            var tenantAccounts = await accounts.AccountsAsync(tenant, tenantVaults.Select(v => v.UserId).ToList(), ct);
            verdicts.AddRange(PaymentCustomerEmailPlanner.Plan(tenantVaults, tenantAccounts));
        }

        return PaymentCustomerEmailPlanner.From(verdicts, options.Limit);
    }

    /// <summary>The human report: vault / user / tenant ids and verdicts — never an email address, never a secret.</summary>
    public static void WriteReport(PaymentCustomerEmailPlan plan, PaymentCustomerEmailOptions options, TextWriter output)
    {
        var inv = CultureInfo.InvariantCulture;
        output.WriteLine(string.Create(inv, $"payment customer vaults             : {plan.Verdicts.Count}"));
        output.WriteLine(string.Create(inv, $"  email already recorded            : {plan.Count(EmailOutcome.Recorded)}"));
        output.WriteLine(string.Create(inv, $"  different email (NOT sent)        : {plan.Count(EmailOutcome.Differs)}"));
        output.WriteLine(string.Create(inv, $"  no account in Identity (NOT sent) : {plan.Count(EmailOutcome.NoAccount)}"));
        output.WriteLine(string.Create(inv, $"  MISSING (to send)                 : {plan.Count(EmailOutcome.Missing)} vault(s), {plan.MembersMissing} member(s)")
            + (plan.ToSend.Count < plan.MembersMissing ? string.Create(inv, $"  (sending {plan.ToSend.Count} — --limit)") : string.Empty));
        output.WriteLine(string.Create(inv, $"→ queue:{PaymentCustomerEmailQueues.QueueName}"));

        var shown = options.List
            ? plan.Verdicts
            : plan.Verdicts.Where(v => v.Outcome is EmailOutcome.Differs or EmailOutcome.NoAccount).ToList();
        if (shown.Count == 0)
        {
            return;
        }

        output.WriteLine();
        foreach (var v in shown)
        {
            output.WriteLine(string.Create(inv,
                $"  {v.Outcome,-10} vault={v.Vault.VaultId} provider={v.Vault.Provider} user={v.Vault.UserId} tenant={v.Vault.TenantId} opened={v.Vault.CreatedAt:yyyy-MM-dd}"));
        }
    }
}

/// <summary>
/// The <c>payment-customer-email</c> command (<c>payment_customer_email_backfill</c>): sends each member whose vault never
/// recorded an email their Identity account email, to Payments' own queue. Exit codes as the other backfills:
/// 0 ok, 1 error, 2 usage, 3 precondition/preflight refused.
/// </summary>
public static class PaymentCustomerEmailProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        PaymentCustomerEmailOptions options;
        try
        {
            options = PaymentCustomerEmailOptions.Parse(args);
        }
        catch (BackfillUsageException ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}\n\n{PaymentCustomerEmailOptions.Usage}");
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
            Console.WriteLine($"identity      : {BackfillProgram.Describe(Connection("Identity"))}");
            Console.WriteLine($"payments      : {BackfillProgram.Describe(Connection("Payments"))}");
            Console.WriteLine($"mode          : {(options.Execute ? "EXECUTE (sends)" : "dry run (sends nothing)")}"
                + (options.Tenants.Count > 0 ? $", tenants {string.Join(",", options.Tenants)}" : ", all tenants"));
            Console.WriteLine();

            await using var provider = BuildServices(Connection);
            PaymentCustomerEmailPlan plan;
            await using (var scope = provider.CreateAsyncScope())
            {
                plan = await scope.ServiceProvider.GetRequiredService<PaymentCustomerEmailRunner>().PlanAsync(options, cts.Token);
            }

            PaymentCustomerEmailRunner.WriteReport(plan, options, Console.Out);
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
        PaymentCustomerEmailPlan plan, PaymentCustomerEmailOptions options, IConfiguration config, string rabbitMq, CancellationToken ct)
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
                PaymentCustomerEmailQueues.QueueName, PaymentCustomerEmailQueues.ExpectedConnectionName, ct);
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
            Console.WriteLine($"\nsending {plan.ToSend.Count} to queue:{PaymentCustomerEmailQueues.QueueName} …");
            var sent = await new PaymentCustomerEmailSender(bus).SendAsync(plan.ToSend, ct);
            Console.WriteLine($"  sent {sent}.");
        }
        finally
        {
            await bus.StopAsync(CancellationToken.None);
        }

        Console.WriteLine("\ndone. Once the queue drains, re-run with --dry-run: MISSING should be 0.");
        return 0;
    }

    private static ServiceProvider BuildServices(Func<string, string> connection)
    {
        var services = new ServiceCollection();
        // EF model-validation warnings of the services' own models are not the tool's.
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning)
            .AddFilter("Microsoft.EntityFrameworkCore.Model.Validation", LogLevel.Error));
        services.AddDbContext<IdentityDbContext>(o => o.UseNpgsql(connection("Identity")));
        services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(connection("Payments")));
        services.AddSingleton<ITenantScope, RlsTenantScope>();
        services.AddScoped<PaymentsVaultSource>();
        services.AddScoped<IdentityAccountSource>();
        services.AddScoped<PaymentCustomerEmailRunner>();
        return services.BuildServiceProvider();
    }
}
