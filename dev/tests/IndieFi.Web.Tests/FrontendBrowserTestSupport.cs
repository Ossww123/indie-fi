using Microsoft.Playwright;

namespace IndieFi.Web.Tests;

internal static class FrontendBrowserTestSupport
{
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
