using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IndieFi.Web.Modules.KeyDistributions.Application;

namespace IndieFi.Web.Tests;

public sealed class WebAppFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly bool _configureGoogle;
    private readonly string _accountProtectionKeyFilePath;
    private readonly IWaitingRoomStore? _waitingRoomStore;

    public WebAppFactory(
        string connectionString,
        bool configureGoogle = true,
        IWaitingRoomStore? waitingRoomStore = null)
    {
        _connectionString = connectionString;
        _configureGoogle = configureGoogle;
        _waitingRoomStore = waitingRoomStore;
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
        builder.UseSetting("WaitingRoom:Redis:Enabled", "false");
        builder.UseSetting("Media:ObjectStorage:Enabled", "false");
        if (_configureGoogle)
        {
            builder.UseSetting("Authentication:Google:Enabled", "true");
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
        builder.ConfigureServices(services =>
        {
            if (_waitingRoomStore is null)
            {
                services.RemoveAll<IKeyClaimAdmissionGate>();
                services.AddSingleton<IKeyClaimAdmissionGate>(
                    AllowKeyClaimAdmissionGate.Instance);
                return;
            }

            services.RemoveAll<IWaitingRoomStore>();
            services.AddSingleton(_waitingRoomStore);
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
