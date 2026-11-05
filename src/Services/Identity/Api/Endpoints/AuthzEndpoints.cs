using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.Identity.Domain.Authz;
using ThreeCommerce.Identity.Infrastructure;

namespace ThreeCommerce.Identity.Api.Endpoints;

/// <summary>
/// Internal Policy Decision Point endpoint (ADR-0025). Not routed publicly by the gateway;
/// services call it to obtain batched action/field decisions and enforce them as PEPs.
/// </summary>
public static class AuthzEndpoints
{
    public static IEndpointRouteBuilder MapAuthz(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/authz/decide", Decide)
            .WithTags("Internal")
            .ExcludeFromDescription();
        return app;
    }

    private static async Task<Results<Ok<AuthzDecisionResponse>, NotFound>> Decide(
        AuthzDecisionRequest request,
        PolicyDecisionService policy,
        CancellationToken cancellationToken)
    {
        var response = await policy.DecideAsync(
            new PolicyDecisionRequest(
                request.PrincipalId,
                request.TenantId,
                request.Actions ?? [],
                request.Fields?.Select(f => new FieldPolicy(f.Field, f.ViewPermission, f.EditPermission, f.Sensitive)).ToArray() ?? [],
                request.UserId),
            cancellationToken);

        return response is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(AuthzDecisionResponse.From(response));
    }
}

/// <summary>
/// <paramref name="PrincipalId"/> names the principal directly; a service PEP, which only knows the caller's
/// internal-claims <c>sub</c>, sends an empty PrincipalId plus <paramref name="UserId"/> and the PDP resolves that
/// user's principal inside <paramref name="TenantId"/> (refund_permission_enforcement).
/// </summary>
public sealed record AuthzDecisionRequest(
    [property: Required] Guid PrincipalId,
    Guid? TenantId,
    IReadOnlyList<string>? Actions,
    IReadOnlyList<FieldPolicyRequest>? Fields,
    Guid? UserId = null);

public sealed record FieldPolicyRequest(
    [property: Required] string Field,
    string? ViewPermission,
    string? EditPermission,
    bool Sensitive);

public sealed record AuthzDecisionResponse(
    Guid DecisionId,
    bool IsPlatformAdmin,
    IReadOnlyList<ActionDecision> Actions,
    IReadOnlyList<FieldDecision> Fields)
{
    public static AuthzDecisionResponse From(PolicyDecisionResponse response) =>
        new(response.DecisionId, response.IsPlatformAdmin, response.Actions, response.Fields);
}
