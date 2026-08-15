using System.Net;
using IndieFi.Web.Infrastructure.Authentication;
using IndieFi.Web.Modules.Accounts.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class DataProtectionKeyPersistenceTests
{
    private readonly PostgreSqlFixture _fixture;

    public DataProtectionKeyPersistenceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProductionInstances_SharingDatabaseAndCertificate_ReadSameCookie()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        long userId;
        await using (var db = _fixture.NewDbContext())
        {
            userId = (await TestAccountProtection.FindUserAsync(
                db,
                email,
                TestContext.Current.CancellationToken)).Id;
        }

        using var baseFactoryA = new WebAppFactory(_fixture.ConnectionString);
        using var factoryA = baseFactoryA.WithWebHostBuilder(builder =>
            builder.UseEnvironment(Environments.Production));
        using var clientA = CreateHttpsClient(factoryA);
        var cookie = await LoginAndReadApplicationCookieAsync(
            factoryA,
            clientA,
            email,
            password);

        await using (var db = _fixture.NewOwnerDbContext())
        {
            var storedKey = await db.DataProtectionKeys
                .OrderByDescending(key => key.Id)
                .FirstAsync(TestContext.Current.CancellationToken);
            Assert.Contains("EncryptedKey", storedKey.Xml, StringComparison.Ordinal);
            Assert.DoesNotContain("masterKey", storedKey.Xml, StringComparison.OrdinalIgnoreCase);
        }

        using var baseFactoryB = new WebAppFactory(_fixture.ConnectionString);
        using var factoryB = baseFactoryB.WithWebHostBuilder(builder =>
            builder.UseEnvironment(Environments.Production));
        using var clientB = CreateHttpsClient(factoryB, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", cookie);
        using var response = await clientB.SendAsync(
            request,
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(UserDisplayIdentifier.Format(userId), html);
    }

    [Fact]
    public void ProductionStartup_WithoutCertificatePaths_FailsBeforeServing()
    {
        using var baseFactory = new WebAppFactory(_fixture.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Production);
            builder.UseSetting("DataProtectionKeyManagement:CertificatePath", null);
            builder.UseSetting("DataProtectionKeyManagement:PrivateKeyPath", null);
        });

        var exception = Assert.ThrowsAny<Exception>(() => _ = factory.Services);
        Assert.Contains(
            "Data Protection certificate",
            FlattenMessages(exception),
            StringComparison.Ordinal);
    }

    private static HttpClient CreateHttpsClient(
        WebApplicationFactory<Program> factory,
        bool handleCookies = true)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = handleCookies,
        });
    }

    private static async Task<string> LoginAndReadApplicationCookieAsync(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        string email,
        string password)
    {
        using var pageResponse = await client.GetAsync(
            "/Login",
            TestContext.Current.CancellationToken);
        var html = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(html),
        });
        using var response = await client.PostAsync(
            "/Login",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookieName = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme)
            .Cookie.Name;
        var setCookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{cookieName}=", StringComparison.Ordinal));
        return setCookie.Split(';', 2)[0];
    }

    private static string FlattenMessages(Exception exception)
    {
        return string.Join(
            " ",
            EnumerateExceptions(exception).Select(item => item.Message));
    }

    private static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
