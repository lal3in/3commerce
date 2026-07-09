using MassTransit;
using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Audit;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Support.Api.Endpoints;
using ThreeCommerce.Support.Infrastructure;
using ThreeCommerce.Support.Infrastructure.Sagas;

var builder = WebApplication.CreateBuilder(args);
builder.AddContainerConfig();

builder.AddServiceTelemetry("support");
builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddDbContext<SupportDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database"), o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")));
builder.Services.AddServiceBus<SupportDbContext>(builder.Configuration, bus =>
{
    bus.AddConsumer<ThreeCommerce.Support.Infrastructure.Consumers.OrderSnapshotConsumer>();
    bus.AddSagaStateMachine<RmaStateMachine, RmaState>()
        .EntityFrameworkRepository(r =>
        {
            r.ExistingDbContext<SupportDbContext>();
            r.UsePostgres();
            r.LockStatementProvider = new MassTransit.EntityFrameworkCoreIntegration.PostgresLockStatementProvider(enableSchemaCaching: false);
        });
});
builder.Services.AddServiceHealth<SupportDbContext>();
builder.Services.AddInternalClaimsAuth(builder.Configuration, builder.Environment);
builder.Services.AddAuditRecorder("support");
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

app.UseApiProblemDetails();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapServiceHealth();
app.MapTickets();
app.MapAdminRmas();

app.Run();

public partial class Program;
