using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FrontendResponsiveBrowserTests : PageTest
{
    private readonly PostgreSqlFixture _fixture;

    public FrontendResponsiveBrowserTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<string, string, int> RepresentativeViewports => new()
    {
        { "/", ".home-hero", 320 },
        { "/", ".home-hero", 768 },
        { "/", ".home-hero", 1440 },
        { "/Login", ".auth-card", 320 },
        { "/Login", ".auth-card", 768 },
        { "/Login", ".auth-card", 1440 },
        { "/BrowserSupport", "#supported-browsers-heading", 320 },
        { "/BrowserSupport", "#supported-browsers-heading", 768 },
        { "/BrowserSupport", "#supported-browsers-heading", 1440 },
    };

    [Theory]
    [MemberData(nameof(RepresentativeViewports))]
    public async Task AnonymousRepresentativePages_KeepCoreContentInsideTheViewport(
        string route,
        string coreSelector,
        int viewportWidth)
    {
        await Page.SetViewportSizeAsync(viewportWidth, 1000);

        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            route,
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        await FrontendBrowserTestSupport.LoadRenderedPageAsync(Page, html);

        await Assertions.Expect(Page.Locator(coreSelector)).ToBeVisibleAsync();
        await FrontendBrowserTestSupport.AssertNoHorizontalOverflowAsync(
            Page,
            route,
            viewportWidth);

        var mainBox = await Page.Locator("#main-content").BoundingBoxAsync();
        Assert.NotNull(mainBox);
        Assert.True(mainBox.X >= -1, $"{route} main content started outside the viewport.");
        Assert.True(
            mainBox.X + mainBox.Width <= viewportWidth + 1,
            $"{route} main content ended outside the {viewportWidth}px viewport.");

        var mobileToggle = Page.Locator("[data-navigation-toggle]");
        var navigation = Page.Locator("[data-navigation-menu]");
        if (viewportWidth < 640)
        {
            await Assertions.Expect(mobileToggle).ToBeVisibleAsync();
            await Assertions.Expect(navigation).ToBeHiddenAsync();
        }
        else
        {
            await Assertions.Expect(mobileToggle).ToBeHiddenAsync();
            await Assertions.Expect(navigation).ToBeVisibleAsync();
        }
    }
}
