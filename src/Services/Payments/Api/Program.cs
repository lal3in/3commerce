using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Payments.Api;
using ThreeCommerce.Payments.Api.Endpoints;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Domain.Xero;
using ThreeCommerce.Payments.Infrastructure;
using ThreeCommerce.Payments.Infrastructure.Consumers;
using ThreeCommerce.Payments.Infrastructure.Idempotency;
using ThreeCommerce.Payments.Infrastructure.Providers;
using ThreeCommerce.Payments.Infrastructure.Providers.Afterpay;
using ThreeCommerce.Payments.Infrastructure.Providers.Mock;
using ThreeCommerce.Payments.Infrastructure.Providers.PayPal;
using ThreeCommerce.Payments.Infrastructure.Providers.Polar;
using ThreeCommerce.Payments.Infrastructure.Providers.Stripe;
using ThreeCommerce.Payments.Infrastructure.Xero;

var builder = WebApplication.CreateBuilder(args);
builder.AddContainerConfig();

builder.AddServiceTelemetry("payments");
builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddDbContext<PaymentsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database"), o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")));
builder.Services.AddServiceBus<PaymentsDbContext>(builder.Configuration, bus =>
{
    bus.AddConsumer<AuthorizePaymentConsumer>();
    bus.AddConsumer<ExecuteRefundConsumer>();
    bus.AddConsumer<RefundPostingConsumer>();
    bus.AddConsumer<SubscriptionRequestedConsumer>();
    bus.AddConsumer<UsageOverageChargeConsumer>();
});
builder.Services.AddServiceHealth<PaymentsDbContext>();
builder.Services.AddInternalClaimsAuth(builder.Configuration, builder.Environment);
builder.Services.AddAuditRecorder("payments");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<PaymentEventProcessor>();
builder.Services.AddScoped<WebhookSecretService>();
builder.Services.AddScoped<SubscriptionService>();

// Scheduled jobs (mt6_3): Quartz fires the daily Xero journal post; each run is recorded as a JobRun.
// The scheduler is gated by Scheduling:Enabled (default on) so integration tests — which boot many
// hosts in one process — can leave it off and avoid Quartz's process-global scheduler/logging state.
builder.Services.AddScoped<IJobRunStore, EfJobRunStore<PaymentsDbContext>>();
if (builder.Configuration.GetValue("Scheduling:Enabled", true))
{
    builder.Services.AddScheduledJobs(jobs => jobs.Add<DailyJournalScheduledJob>("daily-journal", "0 0 2 * * ?"));
}
builder.Services.AddSingleton<IXeroClient, LoggingXeroClient>();
builder.Services.AddScoped<DailyJournalJob>();

// Payment providers (ADR-0039): a keyed registry + 3-mode gate replaces the startup singleton.
// Adapters self-register as IPaymentProvider by lowercase ProviderKey; the registry resolves by
// account provider and applies the LocalMock|Sandbox|Production gate (fail-closed). The boot guard
// refuses a non-Development host that is mis-configured onto the mock/email path.
PaymentModeGuard.EnsureProductionSafe(builder.Configuration, builder.Environment);
builder.Services.AddScoped<PaymentModeResolver>();
builder.Services.AddScoped<PaymentSecretResolver>();
builder.Services.AddScoped<IPaymentProviderRegistry, PaymentProviderRegistry>();
builder.Services.AddScoped<IIdempotencyGuard, IdempotencyGuard>();
builder.Services.AddSingleton<IPaymentProvider, FakePaymentProvider>(); // ProviderKey "mock" (LocalMock; pay_3 layers MockEmailPaymentProvider)
builder.Services.AddSingleton<IPaymentProvider, StripePaymentProvider>(); // ProviderKey "stripe"

// pay_4 PSP adapters (ADR-0039): sandbox-ready skeletons behind the same seam, self-registered by
// lowercase ProviderKey and resolved per account by the registry; each is production-gated by the
// secret resolver (mode-appropriate creds + sandbox/production base URL). Apple/Google Pay are NOT
// adapters — they are PaymentMethodKind values tokenized through the account's PSP.
builder.Services.AddSingleton<IPaymentProvider, PolarPaymentProvider>();     // ProviderKey "polar"
builder.Services.AddSingleton<IPaymentProvider, PayPalPaymentProvider>();    // ProviderKey "paypal"
builder.Services.AddSingleton<IPaymentProvider, AfterpayPaymentProvider>();  // ProviderKey "afterpay"

var app = builder.Build();

app.UseApiProblemDetails();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapServiceHealth();
app.MapWebhooks();
app.MapWebhookSecretAdmin();
app.MapAdmin();
app.MapAdminXero();
app.MapXeroMappings();
app.MapPaymentAccounts();
app.MapSupplierPayouts();
app.MapCustomerPaymentMethods();
app.MapSubscriptions();
app.MapJobRuns();

await ChartOfAccountsSeeder.SeedAsync(app);

app.Run();

public partial class Program;
