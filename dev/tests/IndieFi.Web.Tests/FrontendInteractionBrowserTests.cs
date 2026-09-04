using Microsoft.Playwright;
using Microsoft.Playwright.Xunit.v3;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "FrontendBrowser")]
public sealed class FrontendInteractionBrowserTests : PageTest
{
    private readonly PostgreSqlFixture _fixture;

    public FrontendInteractionBrowserTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MobileNavigation_OpensAndEscapeClosesItWithFocusRestored()
    {
        await LoadPageAsync("/BrowserSupport", viewportWidth: 320);
        await FrontendBrowserTestSupport.LoadProductionScriptAsync(
            Page,
            "layout",
            "navigation.js");

        var toggle = Page.Locator("[data-navigation-toggle]");
        var menu = Page.Locator("[data-navigation-menu]");

        await toggle.ClickAsync();
        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
        await Assertions.Expect(menu).ToBeVisibleAsync();
        await Assertions.Expect(Page.Locator("[data-navigation-label]")).ToHaveTextAsync("메뉴 닫기");

        await Page.Keyboard.PressAsync("Escape");

        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
        await Assertions.Expect(menu).ToBeHiddenAsync();
        await Assertions.Expect(toggle).ToBeFocusedAsync();
    }

    [Fact]
    public async Task SkipLink_MovesKeyboardFocusToTheMainContent()
    {
        await LoadPageAsync("/BrowserSupport", viewportWidth: 1440);
        await FrontendBrowserTestSupport.LoadProductionScriptAsync(
            Page,
            "layout",
            "navigation.js");

        await Page.Keyboard.PressAsync("Tab");

        var skipLink = Page.GetByRole(AriaRole.Link, new() { Name = "본문 바로가기" });
        await Assertions.Expect(skipLink).ToBeFocusedAsync();
        await skipLink.PressAsync("Enter");
        await Assertions.Expect(Page.Locator("#main-content")).ToBeFocusedAsync();
    }

    [Fact]
    public async Task ConfirmationDialog_CancelStopsAndConfirmResubmitsTheForm()
    {
        await LoadPageAsync("/BrowserSupport", viewportWidth: 768);
        await Page.Locator("#main-content").EvaluateAsync("""
            main => {
              main.insertAdjacentHTML('beforeend', `
                <form data-browser-confirmation-fixture
                      data-confirm-title="게임 삭제"
                      data-confirm-message="삭제한 게임은 복구할 수 없습니다."
                      data-confirm-label="삭제 확인">
                  <button type="submit" class="button-danger">게임 삭제</button>
                </form>`);
            }
            """);
        await FrontendBrowserTestSupport.LoadProductionScriptAsync(
            Page,
            "layout",
            "confirmation.js");
        await Page.EvaluateAsync("""
            () => {
              window.confirmedSubmitCount = 0;
              document.addEventListener('submit', event => {
                  if (!event.target.matches('[data-browser-confirmation-fixture]')) {
                    return;
                  }
                  if (!event.defaultPrevented) {
                    window.confirmedSubmitCount += 1;
                  }
                  event.preventDefault();
              });
            }
            """);

        var trigger = Page.GetByRole(AriaRole.Button, new() { Name = "게임 삭제" });
        var dialog = Page.Locator("[data-confirmation-dialog]");

        await trigger.ClickAsync();
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        await Assertions.Expect(Page.Locator("[data-confirmation-title]")).ToHaveTextAsync("게임 삭제");
        await Assertions.Expect(Page.Locator("[data-confirmation-message]")).ToHaveTextAsync(
            "삭제한 게임은 복구할 수 없습니다.");
        await Page.GetByRole(AriaRole.Button, new() { Name = "돌아가기" }).ClickAsync();

        await Assertions.Expect(dialog).ToBeHiddenAsync();
        Assert.Equal(0, await ConfirmedSubmitCountAsync());
        await Assertions.Expect(trigger).ToBeFocusedAsync();

        await trigger.ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "삭제 확인" }).ClickAsync();

        await Assertions.Expect(dialog).ToBeHiddenAsync();
        await Page.WaitForFunctionAsync("() => window.confirmedSubmitCount === 1");
        Assert.Equal(1, await ConfirmedSubmitCountAsync());
    }

    [Fact]
    public async Task ServerValidationSummary_ReceivesFocusAfterThePageLoads()
    {
        await LoadPageAsync("/Login", viewportWidth: 768);
        await Page.Locator(".auth-form").EvaluateAsync("""
            form => {
              form.insertAdjacentHTML('afterbegin', `
                <div class="validation-summary-errors" role="alert">
                  <ul><li>입력값을 다시 확인해 주세요.</li></ul>
                </div>`);
            }
            """);

        await FrontendBrowserTestSupport.LoadProductionScriptAsync(
            Page,
            "layout",
            "form-state.js");

        var summary = Page.Locator(".validation-summary-errors");
        await Assertions.Expect(summary).ToHaveAttributeAsync("tabindex", "-1");
        await Assertions.Expect(summary).ToBeFocusedAsync();
    }

    private async Task LoadPageAsync(string route, int viewportWidth)
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
    }

    private Task<int> ConfirmedSubmitCountAsync() =>
        Page.EvaluateAsync<int>("window.confirmedSubmitCount");
}
