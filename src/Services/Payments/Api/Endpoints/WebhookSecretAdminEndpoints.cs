using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.BuildingBlocks.Infrastructure.Auth;
using ThreeCommerce.Payments.Infrastructure;

namespace ThreeCommerce.Payments.Api.Endpoints;

/// <summary>
/// Webhook signing-secret registry admin (def_2 / mt6_7). Secrets are pasted from the provider
/// dashboard and NEVER leave this service again — every response is masked. Rotation: create the
/// new secret (both verify), cut the provider over, deactivate the old one.
/// </summary>
public static class WebhookSecretAdminEndpoints
{
    public static IEndpointRouteBuilder MapWebhookSecretAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/webhook-secrets").WithTags("Admin Webhook Secrets")
            .RequireAuthorization(InternalClaimsAuth.AdminPolicy);

        group.MapGet("/", List).WithSummary("List registered signing secrets (masked).");
        group.MapPost("/", Create).WithSummary("Register a provider signing secret (value is never returned).");
        group.MapPost("/{id:guid}/deactivate", Deactivate).WithSummary("Deactivate a secret after rotation cut-over.");
        return app;
    }

    private static async Task<Ok<List<WebhookSecretDto>>> List(
        string? provider, WebhookSecretService service, CancellationToken ct)
    {
        var secrets = await service.ListAsync(provider, ct);
        return TypedResults.Ok(secrets.Select(s =>
            new WebhookSecretDto(s.Id, s.Provider, s.Masked, s.Label, s.Active, s.CreatedAt, s.DeactivatedAt)).ToList());
    }

    private static async Task<Results<Created<WebhookSecretDto>, BadRequest<string>>> Create(
        CreateWebhookSecretRequest request, WebhookSecretService service, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.Secret))
        {
            return TypedResults.BadRequest("Provider and secret are required.");
        }

        var created = await service.CreateAsync(request.Provider, request.Secret, request.Label, ct);
        return TypedResults.Created($"/admin/webhook-secrets/{created.Id}",
            new WebhookSecretDto(created.Id, created.Provider, created.Masked, created.Label, created.Active, created.CreatedAt, created.DeactivatedAt));
    }

    private static async Task<Results<NoContent, NotFound>> Deactivate(
        Guid id, WebhookSecretService service, CancellationToken ct) =>
        await service.DeactivateAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound();
}

public record CreateWebhookSecretRequest(
    [property: Required, MaxLength(32)] string Provider,
    [property: Required, MaxLength(256)] string Secret,
    [property: MaxLength(128)] string? Label = null);

public record WebhookSecretDto(
    Guid Id, string Provider, string Masked, string? Label, bool Active,
    DateTimeOffset CreatedAt, DateTimeOffset? DeactivatedAt);
