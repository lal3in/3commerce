using Microsoft.Extensions.Hosting;
using ThreeCommerce.Payments.Domain;
using ThreeCommerce.Payments.Infrastructure.Providers;

namespace ThreeCommerce.Payments.Tests;

/// <summary>
/// The env×account fail-closed matrix (ADR-0039). A Production host refuses a Test account and never
/// the mock path; a Sandbox host refuses a Live account; LocalMock always resolves LocalMock
/// regardless of the account's declared mode. Plus the host-default resolution the old singleton had.
/// </summary>
public class PaymentModeResolverTests
{
    private static PaymentModeResolver Resolver(string hostMode, string env = "Production") =>
        new(PaymentTestSupport.Config(("Payments:Mode", hostMode)), PaymentTestSupport.Env(env));

    // --- 6 host×account combinations (fail-closed matrix) ---

    [Fact]
    public void LocalMock_host_with_Test_account_resolves_LocalMock() =>
        Assert.Equal(PaymentMode.LocalMock, Resolver("LocalMock").Resolve(PaymentTestSupport.Account(PaymentProviderMode.Test)));

    [Fact]
    public void LocalMock_host_with_Live_account_still_resolves_LocalMock() =>
        Assert.Equal(PaymentMode.LocalMock, Resolver("LocalMock").Resolve(PaymentTestSupport.Account(PaymentProviderMode.Live)));

    [Fact]
    public void Sandbox_host_with_Test_account_resolves_Sandbox() =>
        Assert.Equal(PaymentMode.Sandbox, Resolver("Sandbox").Resolve(PaymentTestSupport.Account(PaymentProviderMode.Test)));

    [Fact]
    public void Sandbox_host_with_Live_account_is_refused() =>
        Assert.Throws<PaymentModeException>(() => Resolver("Sandbox").Resolve(PaymentTestSupport.Account(PaymentProviderMode.Live)));

    [Fact]
    public void Production_host_with_Live_account_resolves_Production() =>
        Assert.Equal(PaymentMode.Production, Resolver("Production").Resolve(PaymentTestSupport.Account(PaymentProviderMode.Live)));

    [Fact]
    public void Production_host_with_Test_account_is_refused() =>
        Assert.Throws<PaymentModeException>(() => Resolver("Production").Resolve(PaymentTestSupport.Account(PaymentProviderMode.Test)));

    // --- 3 host-default resolutions (successor to the startup singleton) ---

    [Fact]
    public void Host_mode_defaults_to_LocalMock_in_Development()
    {
        var resolver = new PaymentModeResolver(PaymentTestSupport.Config(), PaymentTestSupport.Env(Environments.Development));
        Assert.Equal(PaymentMode.LocalMock, resolver.ResolveHostMode());
    }

    [Fact]
    public void Host_mode_defaults_to_Production_outside_Development()
    {
        var resolver = new PaymentModeResolver(PaymentTestSupport.Config(), PaymentTestSupport.Env(Environments.Production));
        Assert.Equal(PaymentMode.Production, resolver.ResolveHostMode());
    }

    [Fact]
    public void Default_account_for_host_never_throws_and_matches_the_ceiling()
    {
        // LocalMock default → mock path (Test-flavoured synthetic account, resolves without throwing).
        var dev = new PaymentModeResolver(PaymentTestSupport.Config(), PaymentTestSupport.Env(Environments.Development));
        Assert.Equal(PaymentMode.LocalMock, dev.Resolve(dev.DefaultAccountForHost()));

        // Production default → Live synthetic account, resolves to Production without throwing.
        var prod = new PaymentModeResolver(PaymentTestSupport.Config(), PaymentTestSupport.Env(Environments.Production));
        Assert.Equal(PaymentMode.Production, prod.Resolve(prod.DefaultAccountForHost()));
    }
}
