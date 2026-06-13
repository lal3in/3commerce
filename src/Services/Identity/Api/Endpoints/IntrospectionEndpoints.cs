using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.Identity.Api.Endpoints;

/// <summary>
/// Gateway-only session introspection. Reachable ONLY because services bind to
/// localhost and the gateway has no /internal/* route — never expose publicly.
/// (Plan suggested a second listener port; single listener + no-route chosen for
/// simplicity, documented as deviation.)
/// </summary>
public static class IntrospectionEndpoints
{
    public static IEndpointRouteBuilder MapIntrospection(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/introspection", Introspect)
            .WithTags("Internal")
            .ExcludeFromDescription();
        return app;
    }

    private static async Task<Results<Ok<SessionInfo>, UnauthorizedHttpResult>> Introspect(
        IntrospectionRequest request, IAuthService auth, CancellationToken cancellationToken)
    {
        var session = await auth.IntrospectAsync(request.Token, cancellationToken);
        return session is null ? TypedResults.Unauthorized() : TypedResults.Ok(session);
    }
}

public record IntrospectionRequest([property: Required] string Token);
