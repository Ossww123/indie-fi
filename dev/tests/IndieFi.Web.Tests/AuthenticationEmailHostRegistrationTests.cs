using IndieFi.Web.Modules.Accounts;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class AuthenticationEmailHostRegistrationTests
{
    [Fact]
    public void AccountsModule_DoesNotRunAuthenticationEmailDeliveryInWeb()
    {
        var services = new ServiceCollection();

        services.AddAccountsModule();

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AuthenticationEmailDelivery_RunsOutboxWorkerInWorkerHost()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuthenticationEmail:Enabled"] = "false",
            })
            .Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();

        services.AddAuthenticationEmailDelivery();

        using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());
        Assert.IsType<AuthenticationEmailOutboxWorker>(hostedService);
    }
}
