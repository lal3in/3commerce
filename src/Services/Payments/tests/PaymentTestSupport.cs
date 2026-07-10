using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ThreeCommerce.Payments.Domain;

namespace ThreeCommerce.Payments.Tests;

/// <summary>Shared doubles for the provider/mode/registry unit suites.</summary>
internal static class PaymentTestSupport
{
    public static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    public static IHostEnvironment Env(string environmentName) =>
        new FakeHostEnvironment { EnvironmentName = environmentName };

    public static PaymentAccountSnapshot Account(PaymentProviderMode mode, string provider = "stripe") =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, provider, mode, mode == PaymentProviderMode.Live ? "acct_live" : null);

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
