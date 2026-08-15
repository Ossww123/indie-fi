using System.Globalization;
using IndieFi.Web.Data;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.KeyDistributions.Application;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.KeyRotation;

internal static class Program
{
    private const string ConnectionVariable = "ConnectionStrings__Database";
    private const string KeyFileVariable = "AccountProtectionKeyFile__Path";

    public static async Task<int> Main(string[] args)
    {
        RotationOptions options;
        try
        {
            options = RotationOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintUsage();
            return 1;
        }

        try
        {
            var provider = new FileKeyProvider(options.KeyFilePath);
            var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(options.ConnectionString)
                .Options;
            await using var db = new AppDbContext(dbOptions);

            if (options.Target == RotationTarget.GameKey)
            {
                return await RunGameKeyOperationAsync(db, provider, options);
            }

            return await RunAccountEmailOperationAsync(db, provider, options);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Key operation failed: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> RunAccountEmailOperationAsync(
        AppDbContext db,
        IKeyProvider provider,
        RotationOptions options)
    {
        var service = new AccountEmailKeyRotationService(db, provider);
        if (options.VerifyOnly)
        {
            return await VerifyAccountEmailsAsync(service, options.BatchSize);
        }

        if (options.RetiredLookupKeyId is not null)
        {
            return await RetireLookupKeyAsync(
                service,
                options.RetiredLookupKeyId,
                options.BatchSize);
        }

        return await RotateAccountEmailsAsync(service, options.BatchSize);
    }

    private static async Task<int> RunGameKeyOperationAsync(
        AppDbContext db,
        IKeyProvider provider,
        RotationOptions options)
    {
        var service = new GameKeyMigrationService(db, provider);
        if (options.VerifyOnly)
        {
            return await VerifyGameKeysAsync(service, options.BatchSize);
        }

        if (options.RetiredFingerprintKeyId is not null)
        {
            return await RetireFingerprintKeyAsync(
                service,
                options.RetiredFingerprintKeyId,
                options.BatchSize);
        }

        return await MigrateGameKeysAsync(service, options.BatchSize);
    }

    private static async Task<int> RotateAccountEmailsAsync(
        AccountEmailKeyRotationService service,
        int batchSize)
    {
        long cursor = 0;
        var scanned = 0;
        var reencrypted = 0;
        var lookupsAdded = 0;

        while (true)
        {
            var result = await service.RotateBatchAsync(cursor, batchSize);
            if (result.ScannedCount == 0)
            {
                break;
            }

            cursor = result.NextUserId;
            scanned += result.ScannedCount;
            reencrypted += result.ReencryptedCount;
            lookupsAdded += result.AddedLookupCount;
            Console.WriteLine($"Processed through user ID {cursor}.");
        }

        Console.WriteLine(
            $"Rotation complete. Scanned={scanned}, " +
            $"Reencrypted={reencrypted}, LookupsAdded={lookupsAdded}.");
        return 0;
    }

    private static async Task<int> VerifyAccountEmailsAsync(
        AccountEmailKeyRotationService service,
        int batchSize)
    {
        var scanned = await service.VerifyAllAsync(batchSize);
        Console.WriteLine($"Verification complete. Scanned={scanned}.");
        return 0;
    }

    private static async Task<int> MigrateGameKeysAsync(
        GameKeyMigrationService service,
        int batchSize)
    {
        long cursor = 0;
        var scanned = 0;
        var encrypted = 0;
        var reencrypted = 0;
        var fingerprintsAdded = 0;

        while (true)
        {
            var result = await service.MigrateBatchAsync(cursor, batchSize);
            if (result.ScannedCount == 0)
            {
                break;
            }

            cursor = result.NextGameKeyId;
            scanned += result.ScannedCount;
            encrypted += result.EncryptedCount;
            reencrypted += result.ReencryptedCount;
            fingerprintsAdded += result.AddedFingerprintCount;
            Console.WriteLine($"Processed through game key ID {cursor}.");
        }

        Console.WriteLine(
            $"Game key migration complete. Scanned={scanned}, "
            + $"Encrypted={encrypted}, Reencrypted={reencrypted}, "
            + $"FingerprintsAdded={fingerprintsAdded}.");
        return 0;
    }

    private static async Task<int> VerifyGameKeysAsync(
        GameKeyMigrationService service,
        int batchSize)
    {
        var scanned = await service.VerifyAllAsync(batchSize);
        Console.WriteLine($"Game key verification complete. Scanned={scanned}.");
        return 0;
    }

    private static async Task<int> RetireFingerprintKeyAsync(
        GameKeyMigrationService service,
        string keyId,
        int batchSize)
    {
        var deleted = await service.RetireFingerprintKeyAsync(keyId, batchSize);
        Console.WriteLine(
            $"Retired fingerprint cleanup complete. KeyId={keyId}, "
            + $"Deleted={deleted}.");
        return 0;
    }

    private static async Task<int> RetireLookupKeyAsync(
        AccountEmailKeyRotationService service,
        string keyId,
        int batchSize)
    {
        var deleted = await service.RetireLookupKeyAsync(keyId, batchSize);
        Console.WriteLine(
            $"Retired lookup cleanup complete. KeyId={keyId}, Deleted={deleted}.");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: dotnet run --project tools/IndieFi.KeyRotation -- " +
            "[--target account-email|game-key] " +
            "(--apply | --verify-only | --retire-lookup-key <key-id> | " +
            "--retire-fingerprint-key <key-id>) " +
            "[--batch-size <1-1000>] [--key-file <absolute-path>]");
        Console.Error.WriteLine(
            $"Set {ConnectionVariable}; {KeyFileVariable} supplies the default key file path.");
        Console.Error.WriteLine(
            "The default target is account-email for compatibility. " +
            "Retirement options must match their target.");
    }

    private sealed record RotationOptions(
        string ConnectionString,
        string KeyFilePath,
        int BatchSize,
        RotationTarget Target,
        bool VerifyOnly,
        string? RetiredLookupKeyId,
        string? RetiredFingerprintKeyId)
    {
        public static RotationOptions Parse(string[] args)
        {
            var connection = Environment.GetEnvironmentVariable(ConnectionVariable);
            var keyFile = Environment.GetEnvironmentVariable(KeyFileVariable);
            var batchSize = 100;
            var target = RotationTarget.AccountEmail;
            var apply = false;
            var verifyOnly = false;
            string? retiredLookupKeyId = null;
            string? retiredFingerprintKeyId = null;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--apply":
                        apply = true;
                        break;
                    case "--verify-only":
                        verifyOnly = true;
                        break;
                    case "--target":
                        target = ParseTarget(ReadValue(args, ref index));
                        break;
                    case "--retire-lookup-key":
                        retiredLookupKeyId = ReadValue(args, ref index);
                        break;
                    case "--retire-fingerprint-key":
                        retiredFingerprintKeyId = ReadValue(args, ref index);
                        break;
                    case "--batch-size":
                        batchSize = ParseBatchSize(ReadValue(args, ref index));
                        break;
                    case "--key-file":
                        keyFile = ReadValue(args, ref index);
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument: {args[index]}");
                }
            }

            var operationCount = (apply ? 1 : 0)
                + (verifyOnly ? 1 : 0)
                + (retiredLookupKeyId is null ? 0 : 1)
                + (retiredFingerprintKeyId is null ? 0 : 1);
            if (operationCount != 1)
            {
                throw new ArgumentException(
                    "Choose exactly one operation: --apply, --verify-only, or " +
                    "a target-specific retirement option.");
            }

            if (target == RotationTarget.AccountEmail
                && retiredFingerprintKeyId is not null)
            {
                throw new ArgumentException(
                    "--retire-fingerprint-key requires --target game-key.");
            }

            if (target == RotationTarget.GameKey
                && retiredLookupKeyId is not null)
            {
                throw new ArgumentException(
                    "--retire-lookup-key requires --target account-email.");
            }

            if (string.IsNullOrWhiteSpace(connection))
            {
                throw new ArgumentException(
                    $"Set the {ConnectionVariable} environment variable.");
            }

            if (string.IsNullOrWhiteSpace(keyFile)
                || !Path.IsPathFullyQualified(keyFile))
            {
                throw new ArgumentException(
                    "The account protection key file path must be absolute.");
            }

            return new RotationOptions(
                connection,
                keyFile,
                batchSize,
                target,
                verifyOnly,
                retiredLookupKeyId,
                retiredFingerprintKeyId);
        }

        private static RotationTarget ParseTarget(string value)
        {
            return value switch
            {
                "account-email" => RotationTarget.AccountEmail,
                "game-key" => RotationTarget.GameKey,
                _ => throw new ArgumentException(
                    "Target must be account-email or game-key."),
            };
        }

        private static string ReadValue(string[] args, ref int index)
        {
            index++;
            if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException("An option value is missing.");
            }

            return args[index];
        }

        private static int ParseBatchSize(string value)
        {
            if (!int.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var result)
                || result is < 1 or > AccountEmailKeyRotationService.MaximumBatchSize)
            {
                throw new ArgumentException(
                    $"Batch size must be between 1 and " +
                    $"{AccountEmailKeyRotationService.MaximumBatchSize}.");
            }

            return result;
        }
    }

    private enum RotationTarget
    {
        AccountEmail,
        GameKey,
    }
}
