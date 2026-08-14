using System.Globalization;
using IndieFi.Web.Data;
using IndieFi.Web.Infrastructure.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;
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
            var service = new AccountEmailKeyRotationService(db, provider);

            return options.VerifyOnly
                ? await VerifyAsync(service, options.BatchSize)
                : await RotateAsync(service, options.BatchSize);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Account email key operation failed: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> RotateAsync(
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

    private static async Task<int> VerifyAsync(
        AccountEmailKeyRotationService service,
        int batchSize)
    {
        long cursor = 0;
        var scanned = 0;

        while (true)
        {
            var result = await service.VerifyBatchAsync(cursor, batchSize);
            if (result.ScannedCount == 0)
            {
                break;
            }

            cursor = result.NextUserId;
            scanned += result.ScannedCount;
        }

        Console.WriteLine($"Verification complete. Scanned={scanned}.");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: dotnet run --project tools/IndieFi.KeyRotation -- " +
            "(--apply | --verify-only) [--batch-size <1-1000>] [--key-file <absolute-path>]");
        Console.Error.WriteLine(
            $"Set {ConnectionVariable}; {KeyFileVariable} supplies the default key file path.");
    }

    private sealed record RotationOptions(
        string ConnectionString,
        string KeyFilePath,
        int BatchSize,
        bool VerifyOnly)
    {
        public static RotationOptions Parse(string[] args)
        {
            var connection = Environment.GetEnvironmentVariable(ConnectionVariable);
            var keyFile = Environment.GetEnvironmentVariable(KeyFileVariable);
            var batchSize = 100;
            var apply = false;
            var verifyOnly = false;

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

            if (apply == verifyOnly)
            {
                throw new ArgumentException(
                    "Choose exactly one operation: --apply or --verify-only.");
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
                verifyOnly);
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
}
