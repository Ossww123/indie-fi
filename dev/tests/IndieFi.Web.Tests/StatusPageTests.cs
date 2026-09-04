using System.Net;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class StatusPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public StatusPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_UnknownBrowserPage_ShowsGenericNotFoundPage()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/this-page-does-not-exist",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        var decodedHtml = WebUtility.HtmlDecode(html);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("data-status-page=\"404\"", html);
        Assert.Contains("페이지를 찾을 수 없습니다.", decodedHtml);
        Assert.DoesNotContain("localhost", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_StatusPage_PreservesRequestedFailureCode()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Status/403",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("이 작업을 수행할 수 없습니다.", WebUtility.HtmlDecode(html));
    }

    [Fact]
    public async Task Get_UnknownApiPath_DoesNotReplaceResponseWithHtmlPage()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/this-endpoint-does-not-exist",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("data-status-page", body);
    }

    [Fact]
    public async Task Get_ErrorPage_ShowsSafeLocalizedMessage()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/Error",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("요청을 처리하지 못했습니다.", html);
        Assert.DoesNotContain("Development Mode", html);
        Assert.DoesNotContain("ASPNETCORE_ENVIRONMENT", html);
    }
}
