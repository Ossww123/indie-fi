using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ReverseProxySecurityTests
{
    private readonly PostgreSqlFixture _fixture;

    public ReverseProxySecurityTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Production_KnownProxyHttpsRequest_IsServedWithHsts()
    {
        using var baseFactory = new WebAppFactory(_fixture.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.UseEnvironment(Environments.Production));
        using var client = CreateHttpClient(factory);
        using var request = CreateForwardedRequest("https");

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "max-age=15552000",
            response.Headers.GetValues("Strict-Transport-Security").Single(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_KnownProxyHttpRequest_RedirectsPermanentlyToHttps()
    {
        using var baseFactory = new WebAppFactory(_fixture.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.UseEnvironment(Environments.Production));
        using var client = CreateHttpClient(factory);
        using var request = CreateForwardedRequest("http");

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal(
            "https://indiefi.example.test/",
            response.Headers.Location?.AbsoluteUri);
    }

    [Fact]
    public async Task Production_AsymmetricForwardedProto_IsNotTrusted()
    {
        using var baseFactory = new WebAppFactory(_fixture.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.UseEnvironment(Environments.Production));
        using var client = CreateHttpClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal(
            "https://indiefi.example.test/",
            response.Headers.Location?.AbsoluteUri);
    }

    private static HttpClient CreateHttpClient(
        WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("http://indiefi.example.test"),
        });
    }

    private static HttpRequestMessage CreateForwardedRequest(string scheme)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-For", "203.0.113.10");
        request.Headers.Add("X-Forwarded-Proto", scheme);
        return request;
    }
}
