using Microsoft.EntityFrameworkCore;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.BuildingBlocks.Infrastructure.Configuration;
using ThreeCommerce.BuildingBlocks.Infrastructure.Messaging;
using ThreeCommerce.BuildingBlocks.Infrastructure.Observability;
using ThreeCommerce.BuildingBlocks.Infrastructure.Web;
using ThreeCommerce.Identity.Api;
using ThreeCommerce.Identity.Api.Endpoints;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Infrastructure;
using ThreeCommerce.Identity.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
builder.AddContainerConfig();

builder.AddServiceTelemetry("identity");
builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidation();
builder.Services.AddDbContext<IdentityDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Database"), o => o.MigrationsHistoryTable("__EFMigrationsHistory", "public")));
builder.Services.AddServiceBus<IdentityDbContext>(builder.Configuration);
builder.Services.AddServiceHealth<IdentityDbContext>();
builder.Services.AddInternalClaimsAuth(builder.Configuration, builder.Environment);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IdentityBootstrapper>();
builder.Services.AddScoped<PolicyDecisionService>();
builder.Services.AddScoped<RbacManagementService>();

var app = builder.Build();

app.UseApiProblemDetails();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.UseAuthentication();
app.UseAuthorization();
app.MapServiceHealth();
app.MapAuth();
app.MapProfile();
app.MapIntrospection();
app.MapAuthz();
app.MapAdminRbac();

await DevAdminSeeder.SeedAsync(app);

app.Run();

public partial class Program;
