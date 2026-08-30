using System.Security.Cryptography;
using System.Text;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Games.Domain;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IndieFi.Seed;

internal static class PrepareCommand
{
    public static async Task<int> RunAsync(
        DbContextOptions<AppDbContext> dbOptions,
        IConfiguration configuration,
        PrepareOptions options)
    {
        var credentials = await JsonFile.ReadAsync<LoadTestCredentials>(options.CredentialsPath);
        var password = RequiredSecret(credentials);
        if (File.Exists(options.OutputPath))
        {
            throw new InvalidOperationException($"Refusing to overwrite manifest: {options.OutputPath}");
        }

        var marker = $"operational-validation:{options.RunId}";
        var runTag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.RunId)))
            .ToLowerInvariant()[..12];
        var pools = Pools(runTag, options);
        var accountConfirmedAt = DateTimeOffset.UtcNow;

        await using var db = new AppDbContext(dbOptions);
        var keyProvider = new ConfigurationKeyProvider(configuration);
        var emailProtection = new EmailProtectionService(keyProvider);
        var gameKeyProtection = new GameKeyProtectionService(keyProvider);
        var passwordHasher = new PasswordHasher<User>();
        var owners = new List<User>(options.OwnerCount);
        var allUsers = new List<User>(pools.Sum(pool => pool.Count));

        foreach (var pool in pools)
        {
            for (var number = 1; number <= pool.Count; number++)
            {
                var email = $"{pool.EmailPrefix}{number:D5}@example.test";
                if (!AccountEmailAddressPolicy.TryCreate(email, out var address, out _))
                {
                    throw new InvalidOperationException("A generated load-test email is invalid.");
                }

                var protectedEmail = emailProtection.Protect(address!);
                var user = new User
                {
                    EmailCiphertext = protectedEmail.Ciphertext,
                    EmailNonce = protectedEmail.Nonce,
                    EmailAuthenticationTag = protectedEmail.AuthenticationTag,
                    EmailEncryptionKeyId = protectedEmail.KeyId,
                    EmailConfirmedAt = accountConfirmedAt,
                };
                user.PasswordHash = passwordHasher.HashPassword(user, password);
                db.Users.Add(user);
                allUsers.Add(user);
                foreach (var lookup in emailProtection.CreateWritableLookupValues(address!))
                {
                    db.UserEmailLookups.Add(new UserEmailLookup
                    {
                        User = user,
                        KeyId = lookup.KeyId,
                        Value = lookup.Value,
                    });
                }
                if (pool.Name == "owners")
                {
                    owners.Add(user);
                }
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({options.RunId}, 0))");
        if (await db.UserProfiles.AnyAsync(profile => profile.Biography == marker)
            || await db.Games.AnyAsync(game => game.Description != null && game.Description.StartsWith(marker)))
        {
            throw new InvalidOperationException("The run id already exists; choose a new run id instead of deleting it.");
        }
        await db.SaveChangesAsync();

        foreach (var user in allUsers)
        {
            db.UserProfiles.Add(new UserProfile { UserId = user.Id, Biography = marker });
        }
        await db.SaveChangesAsync();

        var activeGenres = GameClassificationCatalog.Genres.Where(item => item.IsActive).ToArray();
        var activeTags = GameClassificationCatalog.Tags.Where(item => item.IsActive).ToArray();
        var activePlatforms = GameClassificationCatalog.Platforms.Where(item => item.IsActive).ToArray();
        var games = new List<Game>(options.GameCount);
        for (var index = 0; index < options.GameCount; index++)
        {
            var searchToken = (index % 4) switch
            {
                0 => "Aurora",
                1 => "Harbor",
                2 => "Pixel",
                _ => "Strategy",
            };
            var game = new Game
            {
                OwnerId = owners[index % owners.Count].Id,
                GenreId = activeGenres[index % activeGenres.Length].Id,
                Title = $"OPV {runTag} {searchToken} {index + 1:D5}",
                Description = $"{marker}\nDeterministic catalog row {index + 1:D5} for browse and search load.",
            };
            games.Add(game);
            db.Games.Add(game);
        }
        await db.SaveChangesAsync();

        for (var index = 0; index < games.Count; index++)
        {
            db.GameTagAssignments.Add(new GameTagAssignment
            {
                GameId = games[index].Id,
                TagId = activeTags[index % activeTags.Length].Id,
            });
            db.GamePlatformAssignments.Add(new GamePlatformAssignment
            {
                GameId = games[index].Id,
                PlatformId = activePlatforms[index % activePlatforms.Length].Id,
            });
        }

        var opensAt = DateTimeOffset.UtcNow.AddSeconds(options.OpensInSeconds);
        var drop = new KeyDrop
        {
            GameId = games[0].Id,
            OwnerId = games[0].OwnerId,
            OpensAt = opensAt,
            TotalKeys = options.KeyCount,
        };
        db.KeyDrops.Add(drop);
        for (var index = 0; index < options.KeyCount; index++)
        {
            var keyValue = GameKeyPolicy.ParseBatch($"OPV-{Guid.NewGuid():N}").Keys.Single();
            var protectedKey = gameKeyProtection.Protect(keyValue);
            var gameKey = new GameKey
            {
                Drop = drop,
                Ciphertext = protectedKey.Ciphertext,
                Nonce = protectedKey.Nonce,
                AuthenticationTag = protectedKey.AuthenticationTag,
                EncryptionKeyId = protectedKey.EncryptionKeyId,
            };
            db.GameKeys.Add(gameKey);
            foreach (var fingerprint in gameKeyProtection.CreateWritableFingerprints(keyValue))
            {
                db.GameKeyFingerprints.Add(new StoredGameKeyFingerprint
                {
                    GameKey = gameKey,
                    KeyId = fingerprint.KeyId,
                    Value = fingerprint.Value,
                });
            }
        }
        await db.SaveChangesAsync();

        var manifest = new SeedManifest(
            1,
            options.RunId,
            DateTimeOffset.UtcNow,
            options.Commit,
            options.BaseUrl,
            marker,
            new SeedAccountManifest(
                pools.Sum(pool => pool.Count), pools[0], pools[1], pools[2], pools[3]),
            new SeedCatalogManifest(
                options.GameCount,
                games.Min(game => game.Id),
                games.Max(game => game.Id),
                games.Select(game => game.Id).Order().ToArray()),
            new SeedDropManifest(drop.Id, drop.GameId, drop.OpensAt, options.KeyCount));

        try
        {
            await JsonFile.WriteNewAsync(options.OutputPath, manifest);
            await transaction.CommitAsync();
        }
        catch
        {
            if (File.Exists(options.OutputPath))
            {
                File.Delete(options.OutputPath);
            }
            throw;
        }

        Console.WriteLine($"Load-test seed prepared for run {options.RunId}.");
        Console.WriteLine($"Manifest: {options.OutputPath}");
        Console.WriteLine($"Accounts: {manifest.Accounts.Total}; games: {options.GameCount}; keys: {options.KeyCount}");
        Console.WriteLine($"Drop ID: {drop.Id}; opens at UTC: {drop.OpensAt:O}");
        return 0;
    }

    private static SeedAccountPool[] Pools(string tag, PrepareOptions options) =>
    [
        new("owners", options.OwnerCount, $"opv-{tag}-owner-"),
        new("claimers", options.UserCount, $"opv-{tag}-claimer-"),
        new("latecomers", options.LateUserCount, $"opv-{tag}-late-"),
        new("retries", options.RetryUserCount, $"opv-{tag}-retry-"),
    ];

    private static string RequiredSecret(LoadTestCredentials credentials)
    {
        if (credentials.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(credentials.Password)
            || credentials.Password.Length < 12)
        {
            throw new ArgumentException(
                "The ignored credentials JSON must have schemaVersion 1 and a password of at least 12 characters.");
        }
        return credentials.Password;
    }

}
