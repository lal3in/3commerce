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
                request.Fields?.Select(f => new FieldPolicy(f.Field, f.ViewPermission, f.EditPermission, f.Sensitive)).ToArray() ?? []),
            cancellationToken);

        return response is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(AuthzDecisionResponse.From(response));
    }
}

public sealed record AuthzDecisionRequest(
    [property: Required] Guid PrincipalId,
    Guid? TenantId,
    IReadOnlyList<string>? Actions,
    IReadOnlyList<FieldPolicyRequest>? Fields);

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
