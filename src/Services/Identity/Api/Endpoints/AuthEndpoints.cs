using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using ThreeCommerce.Identity.Domain;

namespace ThreeCommerce.Identity.Api.Endpoints;

public static class AuthEndpoints
{
    public const string SessionCookieName = "3c_session";

    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/").WithTags("Auth");

        group.MapPost("/register", Register);
        group.MapPost("/login", Login);
        group.MapPost("/logout", Logout);
        group.MapPost("/verify-email", VerifyEmail);
        group.MapPost("/password-reset/request", RequestPasswordReset);
        group.MapPost("/password-reset/confirm", ConfirmPasswordReset);

        return app;
    }

    /// <summary>202 in all cases — no user enumeration (ADR-0012).</summary>
    private static async Task<Accepted<MessageResponse>> Register(
        RegisterRequest request, IAuthService auth, CancellationToken cancellationToken)
    {
        await auth.RegisterAsync(request.Email, request.Password, cancellationToken);
        return TypedResults.Accepted((string?)null,
            new MessageResponse("Check your inbox to verify your email address."));
    }

    private static async Task<Results<Ok<MessageResponse>, UnauthorizedHttpResult>> Login(
        LoginRequest request, IAuthService auth, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await auth.LoginAsync(request.Email, request.Password, cancellationToken);
        if (result is null)
        {
            return TypedResults.Unauthorized();
        }

        httpContext.Response.Cookies.Append(SessionCookieName, result.RawSessionToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = result.ExpiresAt,
            Path = "/",
        });

        return TypedResults.Ok(new MessageResponse("Logged in."));
    }

    private static async Task<NoContent> Logout(
        IAuthService auth, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (httpContext.Request.Cookies.TryGetValue(SessionCookieName, out var token))
        {
            await auth.LogoutAsync(token, cancellationToken);
        }

        httpContext.Response.Cookies.Delete(SessionCookieName);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<MessageResponse>, BadRequest<MessageResponse>>> VerifyEmail(
        TokenRequest request, IAuthService auth, CancellationToken cancellationToken)
    {
        var ok = await auth.VerifyEmailAsync(request.Token, cancellationToken);
        return ok
            ? TypedResults.Ok(new MessageResponse("Email verified."))
            : TypedResults.BadRequest(new MessageResponse("Invalid or expired token."));
    }

    private static async Task<Accepted<MessageResponse>> RequestPasswordReset(
        EmailRequest request, IAuthService auth, CancellationToken cancellationToken)
    {
        await auth.RequestPasswordResetAsync(request.Email, cancellationToken);
        return TypedResults.Accepted((string?)null,
            new MessageResponse("If that address exists, a reset link is on its way."));
    }

    private static async Task<Results<Ok<MessageResponse>, BadRequest<MessageResponse>>> ConfirmPasswordReset(
        ResetConfirmRequest request, IAuthService auth, CancellationToken cancellationToken)
    {
        var ok = await auth.ConfirmPasswordResetAsync(request.Token, request.NewPassword, cancellationToken);
        return ok
            ? TypedResults.Ok(new MessageResponse("Password updated. All sessions were signed out."))
            : TypedResults.BadRequest(new MessageResponse("Invalid or expired token."));
    }
}

public record RegisterRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required, MinLength(10), MaxLength(256)] string Password);

public record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password);

public record TokenRequest([property: Required] string Token);

public record EmailRequest([property: Required, EmailAddress] string Email);

public record ResetConfirmRequest(
    [property: Required] string Token,
    [property: Required, MinLength(10), MaxLength(256)] string NewPassword);

public record MessageResponse(string Message);
