using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;
using ThreeCommerce.Admin;
using ThreeCommerce.Admin.Components;
using ThreeCommerce.Admin.Services;

var builder = WebApplication.CreateBuilder(args);
// Containerized launch: load host wiring without coupling to ASPNETCORE_ENVIRONMENT (see ContainerConfig).
if (string.Equals(Environment.GetEnvironmentVariable("USE_CONTAINER_CONFIG"), "true", StringComparison.OrdinalIgnoreCase))
    builder.Configuration.AddJsonFile("appsettings.Container.json", optional: true, reloadOnChange: false);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// i18n (i18n_2): all admin UI text comes from Resources/SharedResource[.<culture>].resx via
// IStringLocalizer<SharedResource> (injected as `L` in _Imports.razor). English is the neutral base.
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

// The operator's UI language is a per-session choice held in the culture cookie — NOT derived from the
// browser's Accept-Language, and NOT tied to currency/tax/financial config. Hence the cookie provider is the
// ONLY provider: everyone starts in English and stays there until they pick a language in the switcher.
// AdminCultures.Supported is discovered from the satellite assemblies, so a new .<culture>.resx is picked up
// here with no code change.
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    string[] cultures = [.. AdminCultures.Supported.Select(c => c.Name)];
    options.SetDefaultCulture(AdminCultures.Default)
        .AddSupportedCultures(cultures)
        .AddSupportedUICultures(cultures);
    options.RequestCultureProviders = [new CookieRequestCultureProvider()];
});

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
// Mission-control live bus stats (def_6): RabbitMQ management API, read-only + best-effort.
builder.Services.AddHttpClient("rabbitmq-mgmt", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["MessageBus:ManagementUrl"] ?? "http://localhost:15672");
    c.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddScoped<BusStatsService>();
// Mission-control observability stats: Loki/Tempo/Mimir HTTP APIs, read-only + best-effort.
// Short timeouts so a dead backend degrades its tiles instead of stalling the dashboard.
builder.Services.AddHttpClient("loki", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Observability:LokiUrl"] ?? "http://localhost:3100");
    c.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddHttpClient("tempo", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Observability:TempoUrl"] ?? "http://localhost:3200");
    c.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddHttpClient("mimir", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Observability:MimirUrl"] ?? "http://localhost:9009");
    c.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddScoped<ObservabilityStatsService>();
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

// Reads the culture cookie and sets CurrentCulture/CurrentUICulture for the request — including the request
// that opens a Blazor circuit, which is where an interactive page's IStringLocalizer picks its language up.
// Must run before the components (and before the /culture/set endpoint's redirect lands back here).
app.UseRequestLocalization();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapLoginEndpoints();
app.MapCultureEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
