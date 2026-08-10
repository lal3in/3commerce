using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Usage.Api.Endpoints;
using ThreeCommerce.Usage.Infrastructure;
using ThreeCommerce.Usage.Infrastructure.Scheduling;

var builder = WebApplication.CreateBuilder(args);
builder.AddContainerConfig();

builder.AddServiceTelemetry("usage");
builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddDbContext<UsageDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database"), o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")));
builder.Services.AddServiceBus<UsageDbContext>(builder.Configuration);
builder.Services.AddServiceHealth<UsageDbContext>();
builder.Services.AddInternalClaimsAuth(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<UsageService>();

// Auto-close due usage periods on a cron (mt7_5), recording each run as a JobRun. Gated by
// Scheduling:Enabled (default on) so integration tests — which boot many hosts in one process — can leave
// Quartz's process-global scheduler off.
builder.Services.AddScoped<IJobRunStore, EfJobRunStore<UsageDbContext>>();
if (builder.Configuration.GetValue("Scheduling:Enabled", true))
{
    builder.Services.AddScheduledJobs(builder.Configuration, jobs => jobs.Add<UsagePeriodCloseScheduledJob>("usage-period-close", "0 0 * * * ?"));
}

var app = builder.Build();
app.UseApiProblemDetails();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.UseAuthentication();
app.UseAuthorization();
app.MapServiceHealth();
app.MapUsage();
app.Run();

public partial class Program;
