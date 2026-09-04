using System.Globalization;
using System.Text.RegularExpressions;

namespace IndieFi.Seed;

internal sealed record PrepareOptions(
    string RunId,
    string OutputPath,
    string CredentialsPath,
    string BaseUrl,
    string Commit,
    int OwnerCount,
    int UserCount,
    int LateUserCount,
    int RetryUserCount,
    int GameCount,
    int KeyCount,
    int OpensInSeconds)
{
    private static readonly Regex RunIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant);

    public static PrepareOptions Parse(string[] args)
    {
        string? runId = null;
        string? outputPath = null;
        string? credentialsPath = null;
        string? baseUrl = null;
        string? commit = null;
        var owners = 2;
        var users = 20;
        var lateUsers = 5;
        var retryUsers = 2;
        var games = 20;
        var keys = 12;
        var opensIn = 300;

        ParsePairs(args, (name, value) =>
        {
            switch (name)
            {
                case "--run-id": runId = value; break;
                case "--output": outputPath = value; break;
                case "--credentials": credentialsPath = value; break;
                case "--base-url": baseUrl = value; break;
                case "--commit": commit = value; break;
                case "--owners": owners = Positive(name, value); break;
                case "--users": users = Positive(name, value); break;
                case "--late-users": lateUsers = NonNegative(name, value); break;
                case "--retry-users": retryUsers = NonNegative(name, value); break;
                case "--games": games = Positive(name, value); break;
                case "--keys": keys = Positive(name, value); break;
                case "--opens-in": opensIn = NonNegative(name, value); break;
                default: throw new ArgumentException($"Unknown argument: {name}");
            }
        });

        if (runId is null || !RunIdPattern.IsMatch(runId))
        {
            throw new ArgumentException("--run-id has an invalid format.");
        }
        outputPath = RequiredPath(outputPath, "--output");
        credentialsPath = RequiredPath(credentialsPath, "--credentials");
        baseUrl = RequiredBaseUrl(baseUrl);
        commit = RequiredCommit(commit);
        if (owners + users + lateUsers + retryUsers > 5_000)
        {
            throw new ArgumentException("The total account count cannot exceed 5,000.");
        }
        if (games > 10_000 || keys > 5_000)
        {
            throw new ArgumentException("--games cannot exceed 10,000 and --keys cannot exceed 5,000.");
        }

        return new(runId, outputPath, credentialsPath, baseUrl, commit, owners, users, lateUsers, retryUsers, games, keys, opensIn);
    }

    internal static void ParsePairs(string[] args, Action<string, string> apply)
    {
        if (args.Length % 2 != 0)
        {
            throw new ArgumentException($"Missing value for {args[^1]}.");
        }
        for (var index = 0; index < args.Length; index += 2)
        {
            apply(args[index], args[index + 1]);
        }
    }

    internal static string RequiredPath(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{name} must be a JSON file path.");
        }
        return Path.GetFullPath(value);
    }

    private static string RequiredCommit(string? value)
    {
        if (value is null || value.Length != 40 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("--commit must be a 40-character commit SHA.");
        }
        return value.ToLowerInvariant();
    }

    private static string RequiredBaseUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps
                && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("--base-url must be an HTTPS Origin, or HTTP loopback for local tests.");
        }
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static int Positive(string name, string value)
    {
        var result = NonNegative(name, value);
        return result > 0
            ? result
            : throw new ArgumentException($"{name} must be positive.");
    }

    private static int NonNegative(string name, string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            || result < 0)
        {
            throw new ArgumentException($"{name} must be a non-negative integer.");
        }
        return result;
    }
}

internal sealed record AuditOptions(string ManifestPath, string OutputPath, string Phase)
{
    public static AuditOptions Parse(string[] args)
    {
        string? manifest = null;
        string? output = null;
        string? phase = null;
        PrepareOptions.ParsePairs(args, (name, value) =>
        {
            switch (name)
            {
                case "--manifest": manifest = value; break;
                case "--output": output = value; break;
                case "--phase": phase = value; break;
                default: throw new ArgumentException($"Unknown argument: {name}");
            }
        });

        var manifestPath = PrepareOptions.RequiredPath(manifest, "--manifest");
        var outputPath = PrepareOptions.RequiredPath(output, "--output");
        if (phase is not ("before" or "after"))
        {
            throw new ArgumentException("--phase must be before or after.");
        }
        return new(manifestPath, outputPath, phase);
    }
}
