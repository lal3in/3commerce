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
// Platform MFA floor (mt6_10): numeric MfaRequirement, default Disabled; tenants can only strengthen it.
builder.Services.AddSingleton(new MfaPlatformPolicy((MfaRequirement)builder.Configuration.GetValue("Mfa:PlatformMinimum", 0)));
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<AdminUserService>();
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
app.MapMfa();
app.MapProfile();
app.MapIntrospection();
app.MapAuthz();
app.MapAdminRbac();
app.MapAdminUsers();

await DevAdminSeeder.SeedAsync(app);

app.Run();

public partial class Program;
