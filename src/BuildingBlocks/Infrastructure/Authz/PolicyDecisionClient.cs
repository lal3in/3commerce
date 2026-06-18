using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ThreeCommerce.BuildingBlocks.Infrastructure.Authz;

/// <summary>
/// Service-side Policy Enforcement Point helper (ADR-0025). Services ask Identity/Authz for
/// batched action/field decisions, then enforce the returned decision locally.
/// </summary>
public sealed class PolicyDecisionClient(HttpClient httpClient)
{
    public async Task<PolicyDecisionResponse?> DecideAsync(PolicyDecisionRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/internal/authz/decide", request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PolicyDecisionResponse>(cancellationToken);
    }
}

public static class PolicyDecisionClientExtensions
{
    public static IServiceCollection AddPolicyDecisionClient(this IServiceCollection services, string identityBaseUrl)
    {
        services.AddHttpClient<PolicyDecisionClient>(client =>
        {
            client.BaseAddress = new Uri(identityBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        return services;
    }
}

public sealed record PolicyDecisionRequest(
    Guid PrincipalId,
    Guid? TenantId,
    IReadOnlyList<string> Actions,
    IReadOnlyList<FieldPolicy> Fields);

public sealed record FieldPolicy(
    string Field,
    string? ViewPermission,
    string? EditPermission,
    bool Sensitive);

public sealed record PolicyDecisionResponse(
    Guid DecisionId,
    bool IsPlatformAdmin,
    IReadOnlyList<ActionDecision> Actions,
    IReadOnlyList<FieldDecision> Fields);

public sealed record ActionDecision(
    string PermissionKey,
    bool Allowed,
    PermissionRiskLevel Risk,
    bool RequiresReason,
    bool RequiresApproval);

public sealed record FieldDecision(
    string Field,
    FieldAccess Access,
    bool RequiresRevealReason);

public enum PermissionRiskLevel
{
    Low = 1,
    Medium = 2,
    High = 3,
}

public enum FieldAccess
{
    Hidden = 0,
    Masked = 1,
    ReadOnly = 2,
    Editable = 3,
}
