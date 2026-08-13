using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace IndieFi.Web.Tests;

public sealed class WebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public WebAppFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Database", _connectionString);
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
