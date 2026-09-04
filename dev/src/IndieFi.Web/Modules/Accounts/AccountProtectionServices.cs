using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Infrastructure;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Accounts;

public static class AccountProtectionServices
{
    public static IServiceCollection AddAccountProtectionServices(
        this IServiceCollection services)
    {
        services.AddOptions<AccountProtectionKeyFileOptions>()
            .BindConfiguration(AccountProtectionKeyFileOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<AccountProtectionKeyFileOptions>,
            AccountProtectionKeyFileOptionsValidator>();
        services.AddSingleton<IKeyProvider>(provider =>
        {
            var environment = provider.GetRequiredService<IHostEnvironment>();
            if (environment.IsProduction())
            {
                var options = provider
                    .GetRequiredService<IOptions<AccountProtectionKeyFileOptions>>()
                    .Value;
                return new FileKeyProvider(options.Path!);
            }

            return new ConfigurationKeyProvider(
                provider.GetRequiredService<IConfiguration>());
        });
        services.AddSingleton<EmailProtectionService>();
        services.AddSingleton<RequestIpProtectionService>();
        services.AddSingleton<AuthenticationEmailMessageProtectionService>();
        return services;
    }
}
