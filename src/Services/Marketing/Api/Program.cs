using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Scheduling;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Marketing.Api.Endpoints;
using ThreeCommerce.Marketing.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.AddContainerConfig();

builder.AddServiceTelemetry("marketing");
builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddDbContext<MarketingDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database"), o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")));
builder.Services.AddServiceBus<MarketingDbContext>(builder.Configuration);
builder.Services.AddServiceHealth<MarketingDbContext>();
builder.Services.AddInternalClaimsAuth(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<PublishingService>();
// Scheduled publish sweep (def_5 / mt6_3): every minute; each fire recorded as a JobRun.
builder.Services.AddScoped<IJobRunStore, EfJobRunStore<MarketingDbContext>>();
if (builder.Configuration.GetValue("Scheduling:Enabled", true))
{
    builder.Services.AddScheduledJobs(jobs => jobs.Add<ScheduledPublishJob>("scheduled-publish", "0 * * * * ?"));
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
app.MapCampaigns();
app.MapShortLinks();
app.MapAnalytics();
app.MapContent();

app.Run();

public partial class Program;
