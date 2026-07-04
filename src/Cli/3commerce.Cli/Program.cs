using System.Text;
using System.Text.Json;
using ThreeCommerce.Cli;

var command = args.FirstOrDefault();
if (command is null or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

var options = CliOptions.Parse(args.Skip(1));
return command switch
{
    "auth" => await HandleAuth(args.Skip(1).ToArray(), options),
    "context" => HandleContext(args.Skip(1).ToArray(), options),
    "rbac" => await HandleRbac(args.Skip(1).ToArray(), options),
    "entity" => await HandleEntity(args.Skip(1).ToArray(), options),
    "supplier" => await HandleSupplier(args.Skip(1).ToArray(), options),
    "storefront" => await HandleStorefront(args.Skip(1).ToArray(), options),
    "catalog" => await HandleCatalog(args.Skip(1).ToArray(), options),
    "pricing" => HandlePricing(args.Skip(1).ToArray(), options),
    "payment" => await HandlePayment(args.Skip(1).ToArray(), options),
    "payout" => await HandlePayout(args.Skip(1).ToArray(), options),
    "xero" => await HandleXero(args.Skip(1).ToArray(), options),
    "version" => WriteOutput(new { name = "3commerce.Cli", version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev" }, options),
    _ => Unknown(command),
};

// ---------------------------------------------------------------------------
// Auth (def_3): real gateway login persisting the opaque session token.

static async Task<int> HandleAuth(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    switch (sub)
    {
        case "login":
            {
                if (options.Email is null || options.Password is null)
                {
                    Console.Error.WriteLine("auth login requires --email and --password.");
                    return 2;
                }

                var gateway = ResolveGateway(options);
                using var client = new HttpClient { BaseAddress = new Uri(gateway) };
                var login = await client.PostAsync("/api/identity/login", JsonBody(new { email = options.Email, password = options.Password }));
                var token = ExtractSessionToken(login);
                if (!login.IsSuccessStatusCode || token is null)
                {
                    Console.Error.WriteLine($"Login failed ({(int)login.StatusCode}).");
                    return 1;
                }

                // MFA-enrolled account: the session is pending until the challenge passes (def_1).
                var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
                if (body.RootElement.TryGetProperty("mfaRequired", out var mfa) && mfa.GetBoolean())
                {
                    if (string.IsNullOrWhiteSpace(options.Code))
                    {
                        Console.Error.WriteLine("This account has MFA enabled — re-run with --code <authenticator code>.");
                        return 2;
                    }

                    using var challenge = new HttpRequestMessage(HttpMethod.Post, "/api/identity/mfa/challenge") { Content = JsonBody(new { code = options.Code }) };
                    challenge.Headers.Add("Cookie", $"3c_session={token}");
                    var challenged = await client.SendAsync(challenge);
                    if (!challenged.IsSuccessStatusCode)
                    {
                        Console.Error.WriteLine("MFA code not accepted.");
                        return 1;
                    }
                }

                new CliConfig(gateway, token, options.Email, DateTimeOffset.UtcNow).Save();
                return WriteOutput(new { status = "logged-in", gateway, email = options.Email }, options);
            }

        case "logout":
            {
                var config = CliConfig.Load();
                if (config.SessionToken is not null)
                {
                    using var client = new HttpClient { BaseAddress = new Uri(config.Gateway) };
                    using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/identity/logout");
                    logout.Headers.Add("Cookie", $"3c_session={config.SessionToken}");
                    await client.SendAsync(logout);
                }

                CliConfig.Delete();
                return WriteOutput(new { status = "logged-out" }, options);
            }

        case "whoami":
            return await Call(HttpMethod.Get, "/api/identity/me", options, requireTenant: false);

        default:
            return Unknown($"auth {sub}");
    }
}

static async Task<int> HandleRbac(string[] args, CliOptions options) => args.FirstOrDefault() switch
{
    "permissions" => await Call(HttpMethod.Get, Q("/api/identity/admin/rbac/permissions", options), options),
    "roles" => await Call(HttpMethod.Get, Q("/api/identity/admin/rbac/roles", options), options),
    "effective" when options.Principal is not null =>
        await Call(HttpMethod.Get, Q($"/api/identity/admin/rbac/principals/{options.Principal}/effective-permissions", options), options),
    "effective" => MissingOption("--principal"),
    var sub => Unknown($"rbac {sub}"),
};

static async Task<int> HandleEntity(string[] args, CliOptions options) => args.FirstOrDefault() switch
{
    "list" => await Call(HttpMethod.Get, Q("/api/entity/entities", options), options),
    "create" => await Call(HttpMethod.Post, "/api/entity/entities", options, requireBody: true),
    "archive" when options.Id is not null => await Call(HttpMethod.Delete, Q($"/api/entity/entities/{options.Id}", options), options),
    "customer-links" when options.Id is not null => await Call(HttpMethod.Get, Q($"/api/entity/entities/{options.Id}/customer-links", options), options),
    "link-customer" when options.Id is not null => await Call(HttpMethod.Post, $"/api/entity/entities/{options.Id}/customer-links", options, requireBody: true),
    "unlink-customer" when options.Id is not null => await Call(HttpMethod.Post, Q($"/api/entity/entities/customer-links/{options.Id}/unlink", options), options),
    "archive" or "customer-links" or "link-customer" or "unlink-customer" => MissingOption("--id"),
    var sub => Unknown($"entity {sub}"),
};

static async Task<int> HandleSupplier(string[] args, CliOptions options) => args.FirstOrDefault() switch
{
    "onboard" when options.Id is not null => await Call(HttpMethod.Post, $"/api/entity/entities/{options.Id}/suppliers", options, requireBody: true),
    "readiness" when options.Id is not null => await Call(HttpMethod.Get, Q($"/api/entity/entities/{options.Id}/suppliers/readiness", options), options),
    "activate" when options.Id is not null => await Call(HttpMethod.Post, Q($"/api/entity/entities/{options.Id}/suppliers/activate", options), options),
    "requests" => await Call(HttpMethod.Get, Q("/api/entity/entities/suppliers/change-requests", options), options),
    "approve" when options.Id is not null => await Call(HttpMethod.Post, Q($"/api/entity/entities/suppliers/change-requests/{options.Id}/approve", options), options),
    "reject" when options.Id is not null => await Call(HttpMethod.Post, Q($"/api/entity/entities/suppliers/change-requests/{options.Id}/reject", options), options),
    "onboard" or "readiness" or "activate" or "approve" or "reject" => MissingOption("--id"),
    var sub => Unknown($"supplier {sub}"),
};

static async Task<int> HandleStorefront(string[] args, CliOptions options) => args.FirstOrDefault() switch
{
    "list" => await Call(HttpMethod.Get, Q("/api/catalog/admin/storefronts", options), options),
    "create" => await Call(HttpMethod.Post, "/api/catalog/admin/storefronts", options, requireBody: true),
    "domain" => await CallStorefront(HttpMethod.Post, "domains", options, requireBody: true),
    "readiness" => await CallStorefront(HttpMethod.Get, "readiness", options),
    "activate" => await CallStorefront(HttpMethod.Post, "activate", options),
    "pause" => await CallStorefront(HttpMethod.Post, "pause", options),
    var sub => Unknown($"storefront {sub}"),
};

static async Task<int> HandleCatalog(string[] args, CliOptions options) => args.FirstOrDefault() switch
{
    "products" => await Call(HttpMethod.Get, Q("/api/catalog/admin/products", options), options),
    "assign" => await CallStorefront(HttpMethod.Post, "products", options, requireBody: true),
    "publish" when options.Id is not null => await CallStorefront(HttpMethod.Post, $"products/{options.Id}/publish", options),
    "unpublish" when options.Id is not null => await CallStorefront(HttpMethod.Post, $"products/{options.Id}/unpublish", options),
    "publish" or "unpublish" => MissingOption("--id"),
    var sub => Unknown($"catalog {sub}"),
};

static int HandlePricing(string[] args, CliOptions options) => args.FirstOrDefault() switch
{
    // No Ordering admin pricing API exists yet — honest not-implemented, not a fake 404.
    "promotions" or "preview" => WriteOutput(new { status = "not-implemented", message = "Ordering admin pricing API is not built yet." }, options),
    var sub => Unknown($"pricing {sub}"),
};

static async Task<int> HandlePayment(string[] args, CliOptions options) => args.FirstOrDefault() switch
{
    "accounts" => await Call(HttpMethod.Get, Q("/api/payments/admin/payment-accounts", options), options),
    "create" => await Call(HttpMethod.Post, "/api/payments/admin/payment-accounts", options, requireBody: true),
    "activate" when options.Id is not null => await Call(HttpMethod.Post, Q($"/api/payments/admin/payment-accounts/{options.Id}/activate", options), options),
    "activate" => MissingOption("--id"),
    var sub => Unknown($"payment {sub}"),
};

static async Task<int> HandlePayout(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    if (sub is "approve-bank" && string.IsNullOrWhiteSpace(options.Reason))
    {
        Console.Error.WriteLine("This high-risk command requires --reason.");
        return 2;
    }

    return sub switch
    {
        "bank-accounts" => await Call(HttpMethod.Get, Q("/api/payments/admin/supplier-payouts/bank-accounts", options), options),
        "approve-bank" when options.Id is not null =>
            await Call(HttpMethod.Post, Q($"/api/payments/admin/supplier-payouts/bank-accounts/{options.Id}/approve", options), options),
        "approve-bank" => MissingOption("--id"),
        "instructions" => await Call(HttpMethod.Get, Q("/api/payments/admin/supplier-payouts/instructions", options), options),
        _ => Unknown($"payout {sub}"),
    };
}

static async Task<int> HandleXero(string[] args, CliOptions options) => args.FirstOrDefault() switch
{
    "mappings" => await Call(HttpMethod.Get, Q("/api/payments/admin/xero/mappings", options), options),
    "sync" when options.Id is not null => await Call(HttpMethod.Post, $"/api/payments/admin/xero/sync/{options.Id}", options),
    "sync" => MissingOption("--id (the date, e.g. --id 2026-07-04)"),
    var sub => Unknown($"xero {sub}"),
};

// ---------------------------------------------------------------------------
// HTTP plumbing: every command is a real gateway call with the stored session.

static async Task<int> Call(HttpMethod method, string path, CliOptions options, bool requireTenant = true, bool requireBody = false)
{
    if (requireTenant && string.IsNullOrWhiteSpace(options.Tenant))
    {
        Console.Error.WriteLine("This command requires explicit --tenant.");
        return 2;
    }

    if (requireBody && string.IsNullOrWhiteSpace(options.Body))
    {
        Console.Error.WriteLine("This command requires --body '<json>' (the request payload, see docs/api).");
        return 2;
    }

    var config = CliConfig.Load();
    if (config.SessionToken is null)
    {
        Console.Error.WriteLine("Not logged in — run: 3commerce auth login --email … --password …");
        return 2;
    }

    using var client = new HttpClient { BaseAddress = new Uri(options.GatewayUrl ?? config.Gateway) };
    using var request = new HttpRequestMessage(method, path);
    request.Headers.Add("Cookie", $"3c_session={config.SessionToken}");
    if (!string.IsNullOrWhiteSpace(options.Body))
    {
        request.Content = new StringContent(options.Body, Encoding.UTF8, "application/json");
    }

    HttpResponseMessage response;
    try
    {
        response = await client.SendAsync(request);
    }
    catch (HttpRequestException ex)
    {
        Console.Error.WriteLine($"Gateway unreachable: {ex.Message}");
        return 1;
    }

    var text = await response.Content.ReadAsStringAsync();
    Console.Error.WriteLine($"{(int)response.StatusCode} {method} {path}");
    if (text.Length > 0)
    {
        Console.WriteLine(PrettyJson(text));
    }

    return response.IsSuccessStatusCode ? 0 : 1;
}

static async Task<int> CallStorefront(HttpMethod method, string tail, CliOptions options, bool requireBody = false)
{
    if (string.IsNullOrWhiteSpace(options.Storefront))
    {
        return MissingOption("--storefront");
    }

    return await Call(method, $"/api/catalog/admin/storefronts/{options.Storefront}/{tail}", options, requireBody: requireBody);
}

/// <summary>Appends tenantId= for the admin endpoints that take the tenant as a query parameter.</summary>
static string Q(string path, CliOptions options) =>
    string.IsNullOrWhiteSpace(options.Tenant) ? path : $"{path}{(path.Contains('?') ? '&' : '?')}tenantId={Uri.EscapeDataString(options.Tenant)}";

static string ResolveGateway(CliOptions options) =>
    options.GatewayUrl ?? Environment.GetEnvironmentVariable("THREECOMMERCE_GATEWAY_URL") ?? CliConfig.Load().Gateway;

static string? ExtractSessionToken(HttpResponseMessage response)
{
    if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
    {
        return null;
    }

    var joined = string.Join(";", cookies);
    var start = joined.IndexOf("3c_session=", StringComparison.Ordinal);
    if (start < 0)
    {
        return null;
    }

    start += "3c_session=".Length;
    var end = joined.IndexOf(';', start);
    return end < 0 ? joined[start..] : joined[start..end];
}

static StringContent JsonBody(object payload) =>
    new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

static string PrettyJson(string text)
{
    try
    {
        return JsonSerializer.Serialize(JsonDocument.Parse(text).RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
    catch (JsonException)
    {
        return text;
    }
}

static int MissingOption(string option)
{
    Console.Error.WriteLine($"This command requires {option}.");
    return 2;
}

static int HandleContext(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    var config = CliConfig.Load();
    return sub switch
    {
        "show" => WriteOutput(new
        {
            gateway = options.GatewayUrl ?? Environment.GetEnvironmentVariable("THREECOMMERCE_GATEWAY_URL") ?? config.Gateway,
            loggedInAs = config.Email ?? "<not logged in>",
            tenant = options.Tenant ?? "<required for mutations>",
            storefront = options.Storefront ?? "<not selected>",
            output = options.Output,
        }, options),
        _ => Unknown($"context {sub}"),
    };
}

static int Unknown(string? command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintHelp();
    return 2;
}

static int WriteOutput<T>(T payload, CliOptions options)
{
    if (options.Output == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    foreach (var property in typeof(T).GetProperties())
    {
        Console.WriteLine($"{property.Name}: {property.GetValue(payload)}");
    }

    return 0;
}

static void PrintHelp()
{
    Console.WriteLine("""
        3commerce CLI — talks to the Gateway with a real operator session (def_3).

        Session:
          3commerce auth login --email EMAIL --password PASSWORD [--code MFA_CODE] [--gateway URL]
          3commerce auth logout
          3commerce auth whoami
          3commerce context show [--tenant TENANT_ID] [--storefront STOREFRONT_ID]

        Commands (all issue real gateway HTTP; mutations take --body '<json>'):
          3commerce rbac permissions|roles --tenant TENANT_ID
          3commerce rbac effective --tenant TENANT_ID --principal PRINCIPAL_ID
          3commerce entity list|create|archive|customer-links|link-customer|unlink-customer --tenant TENANT_ID [--id ID] [--body JSON]
          3commerce supplier onboard|readiness|activate|requests|approve|reject --tenant TENANT_ID [--id ID] [--body JSON]
          3commerce storefront list|create|domain|readiness|activate|pause --tenant TENANT_ID [--storefront ID] [--body JSON]
          3commerce catalog products|assign|publish|unpublish --tenant TENANT_ID [--storefront ID] [--id PRODUCT_ID] [--body JSON]
          3commerce payment accounts|create|activate --tenant TENANT_ID [--id ID] [--body JSON]
          3commerce payout bank-accounts|approve-bank|instructions --tenant TENANT_ID [--id ID] --reason REASON
          3commerce xero mappings|sync --tenant TENANT_ID [--id DATE]
          3commerce version [--output table|json]

        Safety rules:
          - Mutations must provide explicit --tenant/--storefront where relevant.
          - High-risk commands require --reason.
          - The CLI calls Gateway/Admin APIs only; it never connects to service databases.
          - The session token is stored owner-only at ~/.3commerce/config.json.
        """);
}

internal sealed record CliOptions(
    string Output,
    string? Tenant,
    string? Storefront,
    string? GatewayUrl,
    string? Reason,
    string? Email,
    string? Password,
    string? Code,
    string? Id,
    string? Principal,
    string? Body)
{
    public static CliOptions Parse(IEnumerable<string> args)
    {
        var output = "table";
        string? tenant = null, storefront = null, gateway = null, reason = null;
        string? email = null, password = null, code = null, id = null, principal = null, body = null;
        var list = args.ToArray();
        for (var i = 0; i < list.Length; i++)
        {
            var value = i + 1 < list.Length ? list[i + 1] : null;
            switch (list[i])
            {
                case "--output" when value is not null: output = value; i++; break;
                case "--tenant" when value is not null: tenant = value; i++; break;
                case "--storefront" when value is not null: storefront = value; i++; break;
                case "--gateway" when value is not null: gateway = value; i++; break;
                case "--reason" when value is not null: reason = value; i++; break;
                case "--email" when value is not null: email = value; i++; break;
                case "--password" when value is not null: password = value; i++; break;
                case "--code" when value is not null: code = value; i++; break;
                case "--id" when value is not null: id = value; i++; break;
                case "--principal" when value is not null: principal = value; i++; break;
                case "--body" when value is not null: body = value; i++; break;
            }
        }

        return new CliOptions(output, tenant, storefront, gateway, reason, email, password, code, id, principal, body);
    }
}
