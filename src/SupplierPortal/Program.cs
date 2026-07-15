using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using ThreeCommerce.SupplierPortal.Components;
using ThreeCommerce.SupplierPortal.Services;

var builder = WebApplication.CreateBuilder(args);
if (string.Equals(Environment.GetEnvironmentVariable("USE_CONTAINER_CONFIG"), "true", StringComparison.OrdinalIgnoreCase))
{
    builder.Configuration.AddJsonFile("appsettings.Container.json", optional: true, reloadOnChange: false);
}

// Session language: resx-backed strings (Resources/SharedResource[.<culture>].resx), culture chosen
// per supplier session by the culture cookie. Adding a language = drop a new .<culture>.resx in and
// list the culture code under Localization:SupportedCultures — no code change here.
var (defaultCulture, supportedCultures) = CultureEndpoints.ReadLocalizationConfig(builder.Configuration);
builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.SetDefaultCulture(defaultCulture)
        .AddSupportedCultures(supportedCultures)
        .AddSupportedUICultures(supportedCultures);

    // Cookie only: the browser's Accept-Language must not override the portal default (English) —
    // the supplier picks the language explicitly via the switcher.
    options.RequestCultureProviders = [new CookieRequestCultureProvider()];
});

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => options.LoginPath = "/login");
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpClient("gateway", c =>
    c.BaseAddress = new Uri(builder.Configuration["Gateway:BaseUrl"] ?? "http://localhost:8080"));
builder.Services.AddScoped<SupplierGatewayClient>();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

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

// Ahead of routing/components so every rendered component sees the session culture; behind the
// /health short-circuit so probes skip it.
app.UseRequestLocalization(app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);

app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

app.MapCultureEndpoints();
app.MapLoginEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
