using Microsoft.EntityFrameworkCore;
using ThreeCommerce.Identity.Domain;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.Identity.Api;

/// <summary>
/// Development convenience only: seeds one admin user from config so admin
/// endpoints are testable. No-ops outside Development or without config.
/// </summary>
public static class DevAdminSeeder
{
    public static async Task SeedAsync(WebApplication app)
    {
        var email = app.Configuration["Identity:SeedAdmin:Email"];
        var password = app.Configuration["Identity:SeedAdmin:Password"];
        if (!app.Environment.IsDevelopment() || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            return;
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        if (!await db.Database.CanConnectAsync() || await db.Users.AnyAsync(u => u.Role == Roles.Admin))
        {
            return;
        }

        db.Users.Add(new User
        {
            Id = Guid.CreateVersion7(),
            Email = email.ToLowerInvariant(),
            PasswordHash = hasher.Hash(password),
            EmailVerified = true,
            Role = Roles.Admin,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        app.Logger.LogWarning("DEV: seeded admin user {Email}", email);
    }
}
