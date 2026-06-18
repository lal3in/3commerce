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

static int RequireTenantOrWritePlaceholder(string endpoint, CliOptions options)
{
    if (string.IsNullOrWhiteSpace(options.Tenant))
    {
        Console.Error.WriteLine("RBAC commands require explicit --tenant.");
        return 2;
    }

    return WriteOutput(new { status = "not-implemented", endpoint, tenant = options.Tenant }, options);
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
    string? GatewayUrl)
{
    public static CliOptions Parse(IEnumerable<string> args)
    {
        var output = "table";
        string? tenant = null;
        string? storefront = null;
        string? gateway = null;
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
            }
        }

        return new CliOptions(output, tenant, storefront, gateway);
    }
}
