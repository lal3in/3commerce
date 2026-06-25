using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace ThreeCommerce.SupplierPortal.Services;

/// <summary>
/// Supplier portal gateway client. Like Admin, it forwards only the opaque gateway session
/// cookie and never calls services or databases directly.
/// </summary>
public sealed class SupplierGatewayClient(IHttpClientFactory factory, AuthenticationStateProvider auth)
{
    public const string SessionClaim = "3c_session";

    private async Task<HttpClient> ClientAsync()
    {
        var client = factory.CreateClient("gateway");
        var state = await auth.GetAuthenticationStateAsync();
        var token = state.User.FindFirstValue(SessionClaim);
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Add("Cookie", $"3c_session={token}");
        }

        return client;
    }

    public async Task<T?> GetAsync<T>(string path)
    {
        var client = await ClientAsync();
        var response = await client.GetAsync(path);
        if (!response.IsSuccessStatusCode)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>();
    }

    public async Task<HttpResponseMessage> PostAsync(string path, object? body = null)
    {
        var client = await ClientAsync();
        return body is null ? await client.PostAsync(path, null) : await client.PostAsJsonAsync(path, body);
    }
}
