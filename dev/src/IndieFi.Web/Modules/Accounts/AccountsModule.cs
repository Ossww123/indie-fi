using IndieFi.Web.Entities;
using IndieFi.Web.Services;
using Microsoft.AspNetCore.Identity;

namespace IndieFi.Web.Modules.Accounts;

public static class AccountsModule
{
    public static IServiceCollection AddAccountsModule(this IServiceCollection services)
    {
        services.AddScoped<AccountRegistrationService>();
        services.AddScoped<AccountAuthenticationService>();

        // Used on its own, without the rest of ASP.NET Core Identity.
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        return services;
    }
}
