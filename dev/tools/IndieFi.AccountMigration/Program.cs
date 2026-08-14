using IndieFi.Web.Data;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IndieFi.AccountMigration;

internal sealed class AccountMigrationProgram
{
    private const string MigrationConnectionName = "MigrationDatabase";
    private const long MigrationLockId = 4_921_501_503;

    public static async Task<int> Main(string[] args)
    {
        MigrationOptions options;
        try
        {
            options = MigrationOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        var configuration = new ConfigurationManager();
        configuration.AddUserSecrets<AccountMigrationProgram>(optional: true);
        configuration.AddEnvironmentVariables();

        var connectionString = configuration.GetConnectionString(
            MigrationConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(
                "A migration-owner connection string is required in " +
                "ConnectionStrings__MigrationDatabase.");
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using var db = new AppDbContext(dbOptions);
            await db.Database.OpenConnectionAsync(cancellation.Token);

            var lockAcquired = await db.Database
                .SqlQuery<bool>(
                    $"SELECT pg_try_advisory_lock({MigrationLockId}) AS \"Value\"")
                .SingleAsync(cancellation.Token);
            if (!lockAcquired)
            {
                Console.Error.WriteLine(
                    "Another email protection migration is already running.");
                return 1;
            }

            try
            {
                var pendingMigrations = await db.Database.GetPendingMigrationsAsync(
                    cancellation.Token);
                if (pendingMigrations.Any())
                {
                    Console.Error.WriteLine(
                        "The database schema is not current. Apply EF migrations first.");
                    return 1;
                }

                var keyProvider = new ConfigurationKeyProvider(configuration);
                var emailProtection = new EmailProtectionService(keyProvider);
                var migration = new EmailProtectionMigration(db, emailProtection);

                if (!options.VerifyOnly)
                {
                    var backfill = await migration.BackfillAsync(
                        options.BatchSize,
                        cancellation.Token);
                    PrintBackfill(backfill);
                }

                var verification = await migration.VerifyAsync(
                    options.BatchSize,
                    cancellation.Token);
                PrintVerification(verification);

                return verification.IsSuccessful ? 0 : 2;
            }
            finally
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_unlock({MigrationLockId})",
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(
                "The migration was cancelled. Completed batches are safe to keep; " +
                "rerun it to continue.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"The migration failed ({exception.GetType().Name}). " +
                "Sensitive exception details were suppressed.");
            return 1;
        }
    }

    private static void PrintBackfill(EmailProtectionBackfillResult result)
    {
        Console.WriteLine("Email protection backfill finished.");
        Console.WriteLine($"  Users scanned:        {result.ScannedUserCount}");
        Console.WriteLine($"  Users protected:      {result.ProtectedUserCount}");
        Console.WriteLine($"  Lookups added:        {result.AddedLookupCount}");
        Console.WriteLine($"  Users needing review: {result.ProblemUserCount}");
    }

    private static void PrintVerification(EmailProtectionVerificationResult result)
    {
        Console.WriteLine("Email protection verification finished.");
        Console.WriteLine($"  Users scanned:              {result.ScannedUserCount}");
        Console.WriteLine($"  Invalid plaintext emails:   {result.InvalidPlaintextEmailCount}");
        Console.WriteLine($"  Normalization mismatches:   {result.NormalizedEmailMismatchCount}");
        Console.WriteLine($"  Incomplete protection:      {result.IncompleteProtectionCount}");
        Console.WriteLine($"  Unreadable ciphertexts:     {result.UnreadableProtectionCount}");
        Console.WriteLine($"  Decrypted value mismatches: {result.DecryptedEmailMismatchCount}");
        Console.WriteLine($"  Missing lookups:            {result.MissingLookupCount}");
        Console.WriteLine($"  Lookup mismatches:          {result.LookupMismatchCount}");
        Console.WriteLine($"  Duplicate lookup groups:    {result.DuplicateLookupGroupCount}");
        Console.WriteLine(result.IsSuccessful ? "  Result: PASS" : "  Result: FAIL");
    }
}

internal sealed record MigrationOptions(int BatchSize, bool VerifyOnly)
{
    public static MigrationOptions Parse(string[] args)
    {
        var batchSize = 100;
        var verifyOnly = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--verify-only":
                    verifyOnly = true;
                    break;
                case "--batch-size":
                    if (index + 1 >= args.Length
                        || !int.TryParse(args[++index], out batchSize)
                        || batchSize is < 1 or > 10_000)
                    {
                        throw new ArgumentException(
                            "--batch-size must be an integer from 1 through 10000.");
                    }

                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }

        return new MigrationOptions(batchSize, verifyOnly);
    }
}
