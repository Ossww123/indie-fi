using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class FrontendBrowserSmokeTests : PageTest
{
    [Fact]
    public async Task ProductionStyles_RenderInteractiveControlsWithTheTouchTargetBaseline()
    {
        await Page.SetContentAsync("""
            <!doctype html>
            <html lang="ko">
            <body>
                <button type="button" class="button-primary">계속하기</button>
            </body>
            </html>
            """);
        await FrontendBrowserTestSupport.LoadProductionStylesAsync(Page);

        var button = Page.GetByRole(AriaRole.Button, new() { Name = "계속하기" });
        var box = await button.BoundingBoxAsync();

        Assert.NotNull(box);
        Assert.True(box.Height >= 44, $"Expected a 44px touch target, got {box.Height}px.");
        Assert.Equal("rgb(98, 71, 170)", await button.EvaluateAsync<string>(
            "element => getComputedStyle(element).backgroundColor"));
    }
}
