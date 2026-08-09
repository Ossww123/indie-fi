using System.Reflection;

namespace IndieFi.Web;

/// <summary>
/// Which commit the running copy was built from, so that the answer to "what is on the
/// server right now" is visible on the page instead of requiring someone to log in.
/// </summary>
public static class BuildInfo
{
    /// <summary>
    /// The short commit hash, or "unknown" when the build carries no commit at all.
    /// </summary>
    public static string Revision { get; } = ReadRevision();

    // The SDK appends "+<SourceRevisionId>" to the informational version, and reads that
    // id out of the git checkout on its own - a build run by hand gets one too, not just
    // the ones the deploy workflow makes. "unknown" is therefore rare: it takes a build
    // with no git information at all, such as one made from an exported copy of the source.
    //
    // A local build with uncommitted edits reports its last commit, which is not exactly
    // what is running. On the server that cannot happen: the workflow only ever builds a
    // commit it just checked out.
    private static string ReadRevision()
    {
        var informationalVersion = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var separator = informationalVersion?.IndexOf('+') ?? -1;
        if (separator < 0)
        {
            return "unknown";
        }

        var revision = informationalVersion![(separator + 1)..];
        return revision.Length > 7 ? revision[..7] : revision;
    }
}
