using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace IndieFi.Web.Tests;

public sealed class WebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly bool _configureGoogle;

    public WebAppFactory(string connectionString, bool configureGoogle = true)
    {
        _connectionString = connectionString;
        _configureGoogle = configureGoogle;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Database", _connectionString);
        if (_configureGoogle)
        {
            builder.UseSetting(
                "Authentication:Google:ClientId",
                "google-test-client-id");
            builder.UseSetting(
                "Authentication:Google:ClientSecret",
                "google-test-client-secret");
        }

        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(
                TestAccountProtection.ConfigurationValues()));
    }

    public new HttpClient CreateClient()
    {
        // Following redirects would hide the destination that page tests need to inspect.
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }
}
