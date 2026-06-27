using System.Text.Json;

var command = args.FirstOrDefault();
if (command is null or "--help" or "-h")
{
    PrintHelp();
    return 0;
}

var options = CliOptions.Parse(args.Skip(1));
return command switch
{
    "auth" => HandleAuth(args.Skip(1).ToArray(), options),
    "context" => HandleContext(args.Skip(1).ToArray(), options),
    "rbac" => HandleRbac(args.Skip(1).ToArray(), options),
    "entity" => HandleEntity(args.Skip(1).ToArray(), options),
    "supplier" => HandleSupplier(args.Skip(1).ToArray(), options),
    "storefront" => HandleStorefront(args.Skip(1).ToArray(), options),
    "catalog" => HandleCatalog(args.Skip(1).ToArray(), options),
    "pricing" => HandlePricing(args.Skip(1).ToArray(), options),
    "payment" => HandlePayment(args.Skip(1).ToArray(), options),
    "payout" => HandlePayout(args.Skip(1).ToArray(), options),
    "xero" => HandleXero(args.Skip(1).ToArray(), options),
    "version" => WriteOutput(new { name = "3commerce.Cli", version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev" }, options),
    _ => Unknown(command),
};

static int HandleAuth(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    return sub switch
    {
        "login" => WriteOutput(new { status = "not-implemented", message = "Human admin login will call the Gateway auth flow in mt1_6 follow-up." }, options),
        "login-service" => WriteOutput(new { status = "not-implemented", message = "Service-account client credentials will call the Gateway auth flow in mt1_6 follow-up." }, options),
        _ => Unknown($"auth {sub}"),
    };
}

static int HandleRbac(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    return sub switch
    {
        "permissions" => RequireTenantOrWritePlaceholder("GET /api/identity/admin/rbac/permissions", options),
        "roles" => RequireTenantOrWritePlaceholder("GET /api/identity/admin/rbac/roles?tenantId=<tenant>", options),
        "effective" => RequireTenantOrWritePlaceholder("GET /api/identity/admin/rbac/principals/<principal>/effective-permissions?tenantId=<tenant>", options),
        _ => Unknown($"rbac {sub}"),
    };
}

static int HandleEntity(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    return sub switch
    {
        "list" => TenantScopedPlaceholder("GET /api/entity/entities?tenantId=<tenant>", options),
        "create" => TenantScopedPlaceholder("POST /api/entity/entities", options),
        "archive" => TenantScopedPlaceholder("DELETE /api/entity/entities/<id>", options),
        "customer-links" => TenantScopedPlaceholder("GET /api/entity/entities/<id>/customer-links?tenantId=<tenant>", options),
        "link-customer" => TenantScopedPlaceholder("POST /api/entity/entities/<id>/customer-links", options),
        "unlink-customer" => TenantScopedPlaceholder("POST /api/entity/entities/customer-links/<linkId>/unlink?tenantId=<tenant>", options),
        _ => Unknown($"entity {sub}"),
    };
}

static int HandleSupplier(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    return sub switch
    {
        "onboard" => TenantScopedPlaceholder("POST /api/entity/entities/<id>/suppliers", options),
        "readiness" => TenantScopedPlaceholder("GET /api/entity/entities/<id>/suppliers/readiness", options),
        "activate" => TenantScopedPlaceholder("POST /api/entity/entities/<id>/suppliers/activate", options),
        "requests" => TenantScopedPlaceholder("GET /api/entity/entities/suppliers/change-requests?tenantId=<tenant>", options),
        "approve" => TenantScopedPlaceholder("POST /api/entity/entities/suppliers/change-requests/<id>/approve?tenantId=<tenant>", options),
        "reject" => TenantScopedPlaceholder("POST /api/entity/entities/suppliers/change-requests/<id>/reject?tenantId=<tenant>", options),
        _ => Unknown($"supplier {sub}"),
    };
}

static int HandleStorefront(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    return sub switch
    {
        "list" => TenantScopedPlaceholder("GET /api/catalog/admin/storefronts?tenantId=<tenant>", options),
        "create" => TenantScopedPlaceholder("POST /api/catalog/admin/storefronts", options),
        "domain" => StorefrontScopedPlaceholder("POST /api/catalog/admin/storefronts/<storefront>/domains", options),
        "readiness" => StorefrontScopedPlaceholder("GET /api/catalog/admin/storefronts/<storefront>/readiness", options),
        "activate" => StorefrontScopedPlaceholder("POST /api/catalog/admin/storefronts/<storefront>/activate", options),
        "pause" => StorefrontScopedPlaceholder("POST /api/catalog/admin/storefronts/<storefront>/pause", options),
        _ => Unknown($"storefront {sub}"),
    };
}

static int HandleCatalog(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    return sub switch
    {
        "products" => TenantScopedPlaceholder("GET /api/catalog/admin/products", options),
        "assign" => StorefrontScopedPlaceholder("POST /api/catalog/admin/storefronts/<storefront>/products", options),
        "publish" => StorefrontScopedPlaceholder("POST /api/catalog/admin/storefronts/<storefront>/products/<product>/publish", options),
        "unpublish" => StorefrontScopedPlaceholder("POST /api/catalog/admin/storefronts/<storefront>/products/<product>/unpublish", options),
        _ => Unknown($"catalog {sub}"),
    };
}

static int HandlePricing(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    return sub switch
    {
        "promotions" => StorefrontScopedPlaceholder("GET/POST /api/ordering/admin/promotions (planned Admin API)", options),
        "preview" => StorefrontScopedPlaceholder("POST /api/ordering/admin/pricing/preview (planned Admin API)", options),
        _ => Unknown($"pricing {sub}"),
    };
}

static int HandlePayment(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    return sub switch
    {
        "accounts" => TenantScopedPlaceholder("GET/POST /api/payments/admin/payment-accounts", options),
        "activate" => TenantScopedPlaceholder("POST /api/payments/admin/payment-accounts/<id>/activate", options),
        _ => Unknown($"payment {sub}"),
    };
}

static int HandlePayout(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    return sub switch
    {
        "bank-accounts" => TenantScopedPlaceholder("GET/POST /api/payments/admin/supplier-payouts/bank-accounts", options),
        "approve-bank" => RequireReason(TenantScopedPlaceholder("POST /api/payments/admin/supplier-payouts/bank-accounts/<id>/approve", options), options),
        "instructions" => TenantScopedPlaceholder("GET/POST /api/payments/admin/supplier-payouts/instructions", options),
        "policies" => TenantScopedPlaceholder("GET/POST /api/payments/admin/supplier-payouts/policies (not implemented)", options),
        _ => Unknown($"payout {sub}"),
    };
}

static int HandleXero(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    return sub switch
    {
        "mappings" => TenantScopedPlaceholder("GET/POST/PUT/DELETE /api/payments/admin/xero/mappings", options),
        "sync" => TenantScopedPlaceholder("POST /api/payments/admin/xero/sync/<date>", options),
        _ => Unknown($"xero {sub}"),
    };
}

static int RequireTenantOrWritePlaceholder(string endpoint, CliOptions options)
{
    if (string.IsNullOrWhiteSpace(options.Tenant))
    {
        Console.Error.WriteLine("RBAC commands require explicit --tenant.");
        return 2;
    }

    return WriteOutput(new { status = "not-implemented", endpoint, tenant = options.Tenant }, options);
}

static int TenantScopedPlaceholder(string endpoint, CliOptions options)
{
    if (string.IsNullOrWhiteSpace(options.Tenant))
    {
        Console.Error.WriteLine("This command requires explicit --tenant.");
        return 2;
    }

    return WriteOutput(new { status = "not-implemented", endpoint, tenant = options.Tenant }, options);
}

static int StorefrontScopedPlaceholder(string endpoint, CliOptions options)
{
    if (string.IsNullOrWhiteSpace(options.Tenant) || string.IsNullOrWhiteSpace(options.Storefront))
    {
        Console.Error.WriteLine("This command requires explicit --tenant and --storefront.");
        return 2;
    }

    return WriteOutput(new { status = "not-implemented", endpoint, tenant = options.Tenant, storefront = options.Storefront }, options);
}

static int RequireReason(int result, CliOptions options)
{
    if (result != 0)
    {
        return result;
    }

    if (string.IsNullOrWhiteSpace(options.Reason))
    {
        Console.Error.WriteLine("This high-risk command requires --reason.");
        return 2;
    }

    return 0;
}

static int HandleContext(string[] args, CliOptions options)
{
    var sub = args.FirstOrDefault();
    return sub switch
    {
        "show" => WriteOutput(new
        {
            gateway = options.GatewayUrl ?? Environment.GetEnvironmentVariable("THREECOMMERCE_GATEWAY_URL") ?? "http://localhost:8080",
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
        3commerce CLI

        Usage:
          3commerce auth login [--gateway URL]
          3commerce auth login-service --client-id ID --client-secret SECRET [--gateway URL]
          3commerce context show [--tenant TENANT_ID] [--storefront STOREFRONT_ID]
          3commerce rbac permissions --tenant TENANT_ID
          3commerce rbac roles --tenant TENANT_ID
          3commerce rbac effective --tenant TENANT_ID --principal PRINCIPAL_ID
          3commerce entity list|create|archive|customer-links|link-customer|unlink-customer --tenant TENANT_ID
          3commerce supplier onboard|readiness|activate|requests|approve|reject --tenant TENANT_ID
          3commerce storefront list|create|domain|readiness|activate|pause --tenant TENANT_ID [--storefront STOREFRONT_ID]
          3commerce catalog products|assign|publish|unpublish --tenant TENANT_ID [--storefront STOREFRONT_ID]
          3commerce pricing promotions|preview --tenant TENANT_ID --storefront STOREFRONT_ID
          3commerce payment accounts|activate --tenant TENANT_ID
          3commerce payout bank-accounts|approve-bank|instructions|policies --tenant TENANT_ID --reason REASON
          3commerce xero mappings|sync --tenant TENANT_ID
          3commerce version [--output table|json]

        Safety rules:
          - Mutations must provide explicit --tenant/--storefront where relevant.
          - Destructive/high-risk commands will require --reason and --yes or interactive confirmation.
          - The CLI calls Gateway/Admin APIs only; it never connects to service databases.
        """);
}

internal sealed record CliOptions(
    string Output,
    string? Tenant,
    string? Storefront,
    string? GatewayUrl,
    string? Reason)
{
    public static CliOptions Parse(IEnumerable<string> args)
    {
        var output = "table";
        string? tenant = null;
        string? storefront = null;
        string? gateway = null;
        string? reason = null;
        var list = args.ToArray();
        for (var i = 0; i < list.Length; i++)
        {
            var value = i + 1 < list.Length ? list[i + 1] : null;
            switch (list[i])
            {
                case "--output" when value is not null:
                    output = value;
                    i++;
                    break;
                case "--tenant" when value is not null:
                    tenant = value;
                    i++;
                    break;
                case "--storefront" when value is not null:
                    storefront = value;
                    i++;
                    break;
                case "--gateway" when value is not null:
                    gateway = value;
                    i++;
                    break;
                case "--reason" when value is not null:
                    reason = value;
                    i++;
                    break;
            }
        }

        return new CliOptions(output, tenant, storefront, gateway, reason);
    }
}
