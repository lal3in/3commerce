using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Auth;

/// <summary>
/// Service-side half of ADR-0012: validates the short-lived ES256 JWT the gateway
/// forwards in X-Internal-Claims. Services hold ONLY the public key — they can
/// verify claims but never mint them.
/// </summary>
public static class InternalClaimsAuth
{
    public const string HeaderName = "X-Internal-Claims";
    public const string Issuer = "3commerce-gateway";
    public const string Audience = "3commerce-internal";

    public const string CustomerPolicy = "Customer";
    public const string AdminPolicy = "Admin";

    public static IServiceCollection AddInternalClaimsAuth(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment? environment = null)
    {
        var publicKeyPem = configuration["InternalAuth:PublicKey"]
            ?? throw new InvalidOperationException("InternalAuth:PublicKey is not configured.");

        // Launch gate (BL-11): never run the committed dev key outside Development.
        DevSecretGuard.EnsureProductionKey(publicKeyPem, IsDevelopment(environment));

        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Issuer,
                    ValidAudience = Audience,
                    IssuerSigningKey = new ECDsaSecurityKey(ecdsa),
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (context.Request.Headers.TryGetValue(HeaderName, out var value))
                        {
                            context.Token = value.ToString();
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(CustomerPolicy, policy => policy.RequireRole("customer", "admin"));
            options.AddPolicy(AdminPolicy, policy => policy.RequireRole("admin"));
        });

        return services;
    }

    private static bool IsDevelopment(IHostEnvironment? environment) =>
        environment?.IsDevelopment()
        ?? string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                ?? "Production",
            "Development",
            StringComparison.OrdinalIgnoreCase);
}
