using System.Net;

namespace ThreeCommerce.Admin.Services;

/// <summary>
/// Admin network posture (ADR-0019): only allowlisted IPs reach the app, checked before auth.
/// Empty allowlist = allow all (local dev). Configure Admin:AllowedIPs as comma-separated CIDRs/IPs.
/// </summary>
public sealed class IpAllowlistMiddleware(RequestDelegate next, IConfiguration config, ILogger<IpAllowlistMiddleware> logger)
{
    private readonly string[] _allowed = (config["Admin:AllowedIPs"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task InvokeAsync(HttpContext context)
    {
        if (_allowed.Length == 0)
        {
            await next(context); // dev: no restriction
            return;
        }

        var remote = context.Connection.RemoteIpAddress ?? IPAddress.None;
        if (_allowed.Any(a => Matches(a, remote)))
        {
            await next(context);
            return;
        }

        logger.LogWarning("Admin access denied for {Ip}", remote);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Forbidden");
    }

    private static bool Matches(string allowed, IPAddress remote)
    {
        if (IPAddress.TryParse(allowed, out var single))
        {
            return single.Equals(remote);
        }

        var parts = allowed.Split('/');
        if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var network) && int.TryParse(parts[1], out var prefix))
        {
            return InSubnet(remote, network, prefix);
        }

        return false;
    }

    private static bool InSubnet(IPAddress address, IPAddress network, int prefixLength)
    {
        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        if (a.Length != n.Length)
        {
            return false;
        }

        var bits = prefixLength;
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? (byte)0xFF : (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (n[i] & mask))
            {
                return false;
            }
        }

        return true;
    }
}
