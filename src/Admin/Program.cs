using Microsoft.AspNetCore.Authentication.Cookies;
using ThreeCommerce.Admin.Components;
using ThreeCommerce.Admin.Services;

var builder = WebApplication.CreateBuilder(args);
// Containerized launch: load host wiring without coupling to ASPNETCORE_ENVIRONMENT (see ContainerConfig).
if (string.Equals(Environment.GetEnvironmentVariable("USE_CONTAINER_CONFIG"), "true", StringComparison.OrdinalIgnoreCase))
    builder.Configuration.AddJsonFile("appsettings.Container.json", optional: true, reloadOnChange: false);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Local cookie auth for the admin app itself; the gateway session token is stored as a claim
// and forwarded to the gateway by GatewayClient (ADR-0019).
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy("admin", p => p.RequireRole("admin")));
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddHttpClient("gateway", c =>
    c.BaseAddress = new Uri(builder.Configuration["Gateway:BaseUrl"] ?? "http://localhost:8080"));
builder.Services.AddScoped<GatewayClient>();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// Container liveness probe — terminal + before the IP allowlist so internal healthchecks pass.
app.Use(async (context, next) =>
{
    if (string.Equals(context.Request.Path.Value, "/health", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsync("ok");
        return;
    }

    await next();
});

app.UseMiddleware<IpAllowlistMiddleware>(); // network posture, before auth
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapLoginEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
