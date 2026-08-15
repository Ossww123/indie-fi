using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace IndieFi.Web.Tests;

public sealed class WebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly bool _configureGoogle;
    private readonly string _accountProtectionKeyFilePath;

    public WebAppFactory(string connectionString, bool configureGoogle = true)
    {
        _connectionString = connectionString;
        _configureGoogle = configureGoogle;
        _accountProtectionKeyFilePath = TestAccountProtection.CreateKeyFile();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Database", _connectionString);
        builder.UseSetting(
            "DataProtectionKeyManagement:CertificatePath",
            TestDataProtection.CertificatePath);
        builder.UseSetting(
            "DataProtectionKeyManagement:PrivateKeyPath",
            TestDataProtection.PrivateKeyPath);
        builder.UseSetting("DataProtectionKeyManagement:KeyLifetimeDays", "90");
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
        {
            var values = TestAccountProtection.ConfigurationValues();
            values["AccountProtectionKeyFile:Path"] =
                _accountProtectionKeyFilePath;
            configuration.AddInMemoryCollection(values);
        });
    }

    public new HttpClient CreateClient()
    {
        // Following redirects would hide the destination that page tests need to inspect.
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            File.Delete(_accountProtectionKeyFilePath);
        }
    }
}
