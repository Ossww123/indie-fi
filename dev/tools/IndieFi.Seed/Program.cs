using System.Globalization;
using System.Text.Json;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Seed;

internal static class Program
{
    private const string DefaultConnection =
        "Host=localhost;Port=5432;Database=indiefi;Username=indiefi;Password=indiefi_local_dev";
    private const string Password = "LoadTest123!";
    private const string BaseUrl = "http://localhost:5258";
    private const string GameTitle = "IndieFi Load Test Game";

    public static async Task<int> Main(string[] args)
    {
        SeedOptions options;

        try
        {
            options = SeedOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(options.Connection)
            .Options;

        await using var db = new AppDbContext(dbOptions);
        var passwordHasher = new PasswordHasher<User>();

        // Latecomers get their own accounts: reusing an account that already holds
        // a key would return AlreadyClaimed, which the page renders exactly like a
        // fresh grant and would inflate the success count.
        var emails = Enumerable
            .Range(1, options.UserCount + options.LateUserCount)
            .Select(LoadTestEmail)
            .ToArray();

        var existingEmails = await db.Users
            .Where(user => emails.Contains(user.Email))
            .Select(user => user.Email)
            .ToHashSetAsync();

        foreach (var email in emails.Where(email => !existingEmails.Contains(email)))
        {
            var user = new User { Email = email };
            user.PasswordHash = passwordHasher.HashPassword(user, Password);
            db.Users.Add(user);
        }

        await db.SaveChangesAsync();

        var owner = await db.Users.SingleAsync(user => user.Email == emails[0]);
        var game = await db.Games
            .Where(candidate =>
                candidate.OwnerId == owner.Id &&
                candidate.Title == GameTitle)
            .OrderBy(candidate => candidate.Id)
            .FirstOrDefaultAsync();

        if (game is null)
        {
            game = new Game
            {
                OwnerId = owner.Id,
                Title = GameTitle,
                Description = "A stable game used only for local key drop load measurements."
            };

            db.Games.Add(game);
            await db.SaveChangesAsync();
        }

        var opensAt = DateTimeOffset.UtcNow.AddSeconds(options.OpensInSeconds);
        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = opensAt,
            TotalKeys = options.KeyCount
        };

        db.KeyDrops.Add(drop);

        for (var index = 1; index <= options.KeyCount; index++)
        {
            db.SteamKeys.Add(new SteamKey
            {
                Drop = drop,
                KeyValue = $"LOADTEST-{Guid.NewGuid():N}"
            });
        }

        await db.SaveChangesAsync();

        var configuration = new
        {
            dropId = drop.Id,
            opensAt = drop.OpensAt.UtcDateTime,
            claimerCount = options.UserCount,
            latecomerCount = options.LateUserCount,
            password = Password,
            baseUrl = BaseUrl
        };

        var loadTestDirectory = Path.Combine(
            Directory.GetCurrentDirectory(),
            "dev",
            "loadtest");
        var resultsDirectory = Path.Combine(loadTestDirectory, "results");
        var configurationPath = Path.Combine(loadTestDirectory, "drop.json");

        Directory.CreateDirectory(resultsDirectory);

        await File.WriteAllTextAsync(
            configurationPath,
            JsonSerializer.Serialize(
                configuration,
                new JsonSerializerOptions { WriteIndented = true }));

        var secondsRemaining = Math.Max(
            0,
            (drop.OpensAt - DateTimeOffset.UtcNow).TotalSeconds);

        Console.WriteLine("Load test data is ready.");
        Console.WriteLine($"Drop ID:        {drop.Id}");
        Console.WriteLine($"Opens at (UTC): {drop.OpensAt:O}");
        Console.WriteLine($"Opens in:       {secondsRemaining:F0} seconds");
        Console.WriteLine($"Keys:           {options.KeyCount}");
        Console.WriteLine($"Claimers:       {options.UserCount} (accounts 1..{options.UserCount})");
        Console.WriteLine(
            $"Latecomers:     {options.LateUserCount} " +
            $"(accounts {options.UserCount + 1}..{options.UserCount + options.LateUserCount})");
        Console.WriteLine($"Password:       {Password}");
        Console.WriteLine($"Base URL:       {BaseUrl}");
        Console.WriteLine($"Configuration:  {configurationPath}");

        return 0;
    }

    private static string LoadTestEmail(int number)
    {
        return $"loadtest-{number:D4}@example.test";
    }

    private sealed record SeedOptions(
        int UserCount,
        int LateUserCount,
        int KeyCount,
        int OpensInSeconds,
        string Connection)
    {
        public static SeedOptions Parse(string[] args)
        {
            var userCount = 500;
            var lateUserCount = 200;
            var keyCount = 100;
            var opensInSeconds = 90;
            var connection = DefaultConnection;

            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {args[index]}.");
                }

                var name = args[index];
                var value = args[index + 1];

                switch (name)
                {
                    case "--users":
                        userCount = ParsePositiveInteger(name, value);
                        break;
                    case "--late-users":
                        lateUserCount = ParsePositiveInteger(name, value);
                        break;
                    case "--keys":
                        keyCount = ParsePositiveInteger(name, value);
                        break;
                    case "--opens-in":
                        opensInSeconds = ParseNonNegativeInteger(name, value);
                        break;
                    case "--connection":
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            throw new ArgumentException("--connection cannot be empty.");
                        }

                        connection = value;
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: {name}");
                }
            }

            return new SeedOptions(
                userCount,
                lateUserCount,
                keyCount,
                opensInSeconds,
                connection);
        }

        private static int ParsePositiveInteger(string name, string value)
        {
            if (!int.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var result) ||
                result <= 0)
            {
                throw new ArgumentException($"{name} must be a positive integer.");
            }

            return result;
        }

        private static int ParseNonNegativeInteger(string name, string value)
        {
            if (!int.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var result) ||
                result < 0)
            {
                throw new ArgumentException($"{name} must be a non-negative integer.");
            }

            return result;
        }
    }
}
