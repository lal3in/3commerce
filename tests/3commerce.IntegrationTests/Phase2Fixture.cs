using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace ThreeCommerce.IntegrationTests;

/// <summary>
/// Phase 2 infra: Postgres (+ identity_db/catalog_db with required extensions),
/// RabbitMQ, and a self-contained ES256 keypair so tests can mint the internal
/// claims the gateway normally issues — without depending on the dev keys.
/// </summary>
public sealed class Phase2Fixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4").Build();
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly JsonWebTokenHandler _jwtHandler = new();

    public string PublicKeyPem { get; private set; } = string.Empty;
    public string RabbitMqUri { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());
        RabbitMqUri = _rabbitMq.GetConnectionString();
        PublicKeyPem = _ecdsa.ExportSubjectPublicKeyInfoPem();

        await _postgres.ExecScriptAsync("CREATE DATABASE identity_db;");
        await _postgres.ExecScriptAsync("CREATE DATABASE catalog_db;");
        // Extensions the migrations rely on (init script does this in real infra).
        await ExecInDbAsync("identity_db", "CREATE EXTENSION IF NOT EXISTS citext;");
        await ExecInDbAsync("catalog_db", "CREATE EXTENSION IF NOT EXISTS citext; CREATE EXTENSION IF NOT EXISTS pg_trgm;");
    }

    public async Task DisposeAsync()
    {
        _ecdsa.Dispose();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    public WebApplicationFactory<ThreeCommerce.Identity.Api.IApiMarker> CreateIdentityFactory() =>
        CreateFactory<ThreeCommerce.Identity.Api.IApiMarker, ThreeCommerce.Identity.Infrastructure.IdentityDbContext>("identity_db");

    public WebApplicationFactory<ThreeCommerce.Catalog.Api.IApiMarker> CreateCatalogFactory() =>
        CreateFactory<ThreeCommerce.Catalog.Api.IApiMarker, ThreeCommerce.Catalog.Infrastructure.CatalogDbContext>("catalog_db");

    /// <summary>Mints the ES256 internal-claims JWT a service expects in X-Internal-Claims.</summary>
    public string MintInternalClaims(Guid userId, string role, string? email = null, string? tenantId = null)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = userId.ToString(),
            ["role"] = role,
            ["sid"] = Guid.NewGuid().ToString(),
            ["tenant"] = tenantId ?? "00000000-0000-0000-0000-000000000001",
        };
        if (!string.IsNullOrWhiteSpace(email))
        {
            claims["email"] = email;
        }

        return _jwtHandler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "3commerce-gateway",
            Audience = "3commerce-internal",
            IssuedAt = now,
            Expires = now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(_ecdsa), SecurityAlgorithms.EcdsaSha256),
            Claims = claims,
        });
    }

    private WebApplicationFactory<TMarker> CreateFactory<TMarker, TDbContext>(string database)
        where TMarker : class
        where TDbContext : DbContext
    {
        var connectionString = _postgres.GetConnectionString()
            .Replace("Database=postgres", $"Database={database}", StringComparison.Ordinal);

        var factory = new WebApplicationFactory<TMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", connectionString);
            builder.UseSetting("ConnectionStrings:RabbitMq", RabbitMqUri);
            builder.UseSetting("InternalAuth:PublicKey", PublicKeyPem);
            // Disable the dev admin seeder during tests (each test owns its data).
            builder.UseSetting("Identity:SeedAdmin:Email", string.Empty);
        });

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TDbContext>().Database.Migrate();

        return factory;
    }

    private async Task ExecInDbAsync(string database, string sql)
    {
        // ExecScriptAsync runs against the default db; use psql -d to target another.
        var result = await _postgres.ExecAsync(["psql", "-U", "postgres", "-d", database, "-c", sql]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Setup SQL failed on {database}: {result.Stderr}");
        }
    }
}
