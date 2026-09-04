using System.Security.Claims;
using System.Text.Json;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ExternalAuthenticationConfigurationTests
{
    private readonly PostgreSqlFixture _fixture;

    public ExternalAuthenticationConfigurationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void Google_UsesProtectedTemporaryCookieWithoutSavingProviderTokens()
    {
        using var factory = new WebAppFactory(_fixture.ConnectionString);
        var cookieOptions = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var temporaryCookie = cookieOptions.Get(
            ExternalAuthenticationDefaults.TemporaryScheme);
        var google = factory.Services
            .GetRequiredService<IOptionsMonitor<GoogleOptions>>()
            .Get(ExternalLoginPolicy.GoogleProvider);
        var authentication = factory.Services
            .GetRequiredService<IOptions<AuthenticationOptions>>()
            .Value;

        Assert.Equal(
            CookieAuthenticationDefaults.AuthenticationScheme,
            authentication.DefaultScheme);
        Assert.Equal(
            ExternalAuthenticationDefaults.TemporaryCookieName,
            temporaryCookie.Cookie.Name);
        Assert.True(temporaryCookie.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, temporaryCookie.Cookie.SameSite);
        Assert.Equal(
            ExternalAuthenticationDefaults.TemporaryLifetime,
            temporaryCookie.Cookie.MaxAge);
        Assert.Equal(
            ExternalAuthenticationDefaults.TemporaryLifetime,
            temporaryCookie.ExpireTimeSpan);
        Assert.False(temporaryCookie.SlidingExpiration);
        Assert.NotNull(temporaryCookie.TicketDataFormat);

        Assert.Equal(
            ExternalAuthenticationDefaults.TemporaryScheme,
            google.SignInScheme);
        Assert.Equal("/signin-google", google.CallbackPath.Value);
        Assert.Equal(
            ExternalAuthenticationDefaults.TemporaryLifetime,
            google.RemoteAuthenticationTimeout);
        Assert.False(google.SaveTokens);
        Assert.True(google.UsePkce);
        Assert.Contains("email", google.Scope);
    }

    [Fact]
    public void Google_StoresOnlySubjectVerifiedEmailAndVerificationState()
    {
        using var factory = new WebAppFactory(_fixture.ConnectionString);
        var google = factory.Services
            .GetRequiredService<IOptionsMonitor<GoogleOptions>>()
            .Get(ExternalLoginPolicy.GoogleProvider);
        using var userInfo = JsonDocument.Parse(
            """
            {
              "id": "legacy-google-id",
              "sub": "immutable-google-subject",
              "email": "verified@example.test",
              "email_verified": true,
              "name": "Must Not Be Stored"
            }
            """);
        var identity = new ClaimsIdentity();

        foreach (var action in google.ClaimActions)
        {
            action.Run(userInfo.RootElement, identity, ExternalLoginPolicy.GoogleProvider);
        }

        var claims = identity.Claims.ToList();
        Assert.Equal(3, claims.Count);
        Assert.Contains(
            claims,
            claim => claim.Type == ClaimTypes.NameIdentifier
                && claim.Value == "immutable-google-subject");
        Assert.Contains(
            claims,
            claim => claim.Type == ClaimTypes.Email
                && claim.Value == "verified@example.test");
        Assert.Contains(
            claims,
            claim => claim.Type == ExternalAuthenticationDefaults.GoogleEmailVerifiedClaim
                && string.Equals(
                    claim.Value,
                    bool.TrueString,
                    StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(claims, claim => claim.Value == "legacy-google-id");
        Assert.DoesNotContain(claims, claim => claim.Type == ClaimTypes.Name);
    }

    [Fact]
    public void EnabledGoogle_RequiresBothCredentials()
    {
        var result = GoogleAuthenticationSettings.Validate(
            new GoogleAuthenticationSettings { Enabled = true });

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("ClientId", StringComparison.Ordinal));
        Assert.Contains(
            result.Failures,
            failure => failure.Contains("ClientSecret", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledGoogle_AllowsMissingCredentialsButRejectsPartialCredentials()
    {
        var disabled = GoogleAuthenticationSettings.Validate(
            new GoogleAuthenticationSettings());
        var partial = GoogleAuthenticationSettings.Validate(
            new GoogleAuthenticationSettings { ClientId = "configured-client" });

        Assert.True(disabled.Succeeded);
        Assert.True(partial.Failed);
        Assert.Contains(
            partial.Failures,
            failure => failure.Contains("ClientSecret", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionStartup_WithGoogleEnabledButWithoutCredentials_FailsValidation()
    {
        using var baseFactory = new WebAppFactory(_fixture.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Authentication:Google:Enabled"] = "true",
                        ["Authentication:Google:ClientId"] = null,
                        ["Authentication:Google:ClientSecret"] = null,
                    }));
        });

        var exception = Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        var validationException = FindException<OptionsValidationException>(exception);

        Assert.NotNull(validationException);
        Assert.Contains(
            validationException.Failures,
            failure => failure.Contains(
                "Authentication:Google",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionStartup_WithGoogleDisabledAndWithoutCredentials_Succeeds()
    {
        using var baseFactory = new WebAppFactory(
            _fixture.ConnectionString,
            configureGoogle: false);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Authentication:Google:Enabled"] = "false",
                        ["Authentication:Google:ClientId"] = null,
                        ["Authentication:Google:ClientSecret"] = null,
                    }));
        });

        var settings = factory.Services
            .GetRequiredService<IOptions<GoogleAuthenticationSettings>>()
            .Value;

        Assert.False(settings.IsAvailable);
    }

    [Fact]
    public async Task DisabledGoogle_WithStoredCredentials_DoesNotRegisterProvider()
    {
        using var baseFactory = new WebAppFactory(
            _fixture.ConnectionString,
            configureGoogle: false);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Authentication:Google:ClientId"] = "stored-client",
                        ["Authentication:Google:ClientSecret"] = "stored-secret",
                    })));

        var schemes = factory.Services
            .GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Null(await schemes.GetSchemeAsync(ExternalLoginPolicy.GoogleProvider));
    }

    private static TException? FindException<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException expected)
            {
                return expected;
            }
        }

        return null;
    }
}
