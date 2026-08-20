using Microsoft.Playwright;
using System.Text.RegularExpressions;
using Xunit;

namespace IndieFi.Web.Tests;

internal static class FrontendBrowserTestSupport
{
    private static readonly Regex StylesheetLinkPattern = new(
        "<link\\s+rel=\"stylesheet\"[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExternalScriptPattern = new(
        "<script\\s+[^>]*src=\"[^\"]+\"[^>]*>\\s*</script>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string AssetPath(params string[] segments)
    {
        var repositoryRoot = FindRepositoryRoot();
        return Path.Combine(
            [repositoryRoot, "dev", "src", "IndieFi.Web", .. segments]);
    }

    public static async Task LoadProductionStylesAsync(IPage page)
    {
        await page.AddStyleTagAsync(new PageAddStyleTagOptions
        {
            Path = AssetPath("wwwroot", "css", "dist", "site.css"),
        });

        // Adding CSS after the fixture exists starts the real production color
        // transitions. Let those settle before asserting computed styles.
        await page.WaitForTimeoutAsync(200);
    }

    public static async Task LoadRenderedPageAsync(IPage page, string html)
    {
        var css = await File.ReadAllTextAsync(
            AssetPath("wwwroot", "css", "dist", "site.css"));
        var browserHtml = StylesheetLinkPattern.Replace(
            html,
            _ => $"<style>{css}</style>",
            count: 1);
        browserHtml = ExternalScriptPattern.Replace(browserHtml, string.Empty);

        await page.SetContentAsync(browserHtml, new PageSetContentOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
        });
        await page.WaitForTimeoutAsync(50);
    }

    public static async Task LoadProductionScriptAsync(
        IPage page,
        params string[] segments)
    {
        await page.AddScriptTagAsync(new PageAddScriptTagOptions
        {
            Path = AssetPath(["wwwroot", "js", "dist", .. segments]),
            Type = "module",
        });
        await page.WaitForTimeoutAsync(50);
    }

    public static async Task AssertNoHorizontalOverflowAsync(
        IPage page,
        string route,
        int viewportWidth)
    {
        var scrollWidth = await page.EvaluateAsync<int>(
            "Math.max(document.documentElement.scrollWidth, document.body.scrollWidth)");

        Assert.True(
            scrollWidth <= viewportWidth + 1,
            $"{route} overflowed at {viewportWidth}px: scroll width was {scrollWidth}px.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "dev", "IndieFi.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root for frontend browser assets.");
    }
}
