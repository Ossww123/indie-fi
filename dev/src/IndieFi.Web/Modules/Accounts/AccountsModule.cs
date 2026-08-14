using IndieFi.Web.Entities;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.AspNetCore.Identity;

namespace IndieFi.Web.Modules.Accounts;

public static class AccountsModule
{
    public static IServiceCollection AddAccountsModule(this IServiceCollection services)
    {
        services.AddScoped<AccountRegistrationService>();
        services.AddScoped<AccountAuthenticationService>();
        services.AddScoped<EmailConfirmationTokenService>();
        services.AddScoped<AuthenticationEmailOutboxService>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IKeyProvider, ConfigurationKeyProvider>();
        services.AddSingleton<EmailProtectionService>();
        services.AddSingleton<AuthenticationEmailMessageProtectionService>();

        // Used on its own, without the rest of ASP.NET Core Identity.
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        return services;
    }
}
