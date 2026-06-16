using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace ThreeCommerce.Admin.Services;

/// <summary>
/// Calls the YARP gateway like any other client (ADR-0019) — forwards the admin's session
/// cookie. The admin app holds NO service references and no database access.
/// </summary>
public sealed class GatewayClient(IHttpClientFactory factory, AuthenticationStateProvider auth)
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

    public async Task<List<T>> GetListAsync<T>(string path)
    {
        var client = await ClientAsync();
        return await client.GetFromJsonAsync<List<T>>(path) ?? [];
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

    public async Task<HttpResponseMessage> PutAsync(string path, object body)
    {
        var client = await ClientAsync();
        return await client.PutAsJsonAsync(path, body);
    }

    public async Task<HttpResponseMessage> DeleteAsync(string path)
    {
        var client = await ClientAsync();
        return await client.DeleteAsync(path);
    }

    public async Task<HttpResponseMessage> PostAsync(string path, object? body = null, string? idempotencyKey = null)
    {
        var client = await ClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }
}
