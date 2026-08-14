using IndieFi.Web.Entities;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace IndieFi.Web.Modules.Accounts;

public static class AccountsModule
{
    public static IServiceCollection AddAccountsModule(this IServiceCollection services)
    {
        services.AddScoped<AccountRegistrationService>();
        services.AddScoped<AccountAuthenticationService>();
        services.AddScoped<EmailConfirmationTokenService>();
        services.AddScoped<AuthenticationEmailOutboxService>();
        services.AddScoped<AuthenticationEmailOutboxProcessor>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IKeyProvider, ConfigurationKeyProvider>();
        services.AddSingleton<EmailProtectionService>();
        services.AddSingleton<AuthenticationEmailMessageProtectionService>();
        services.AddOptions<AuthenticationEmailDeliveryOptions>()
            .BindConfiguration(AuthenticationEmailDeliveryOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<AuthenticationEmailDeliveryOptions>,
            AuthenticationEmailDeliveryOptionsValidator>();
        services.AddSingleton<AuthenticationEmailMessageFactory>();
        services.AddSingleton<
            IAuthenticationEmailSender,
            SmtpAuthenticationEmailSender>();

        services.AddHostedService(provider =>
        {
            var options = provider
                .GetRequiredService<IOptions<AuthenticationEmailDeliveryOptions>>()
                .Value;
            return new AuthenticationEmailOutboxWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<AuthenticationEmailOutboxWorker>>(),
                options.Enabled,
                options.WorkerInterval,
                options.BatchSize);
        });

        // Used on its own, without the rest of ASP.NET Core Identity.
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        return services;
    }
}
