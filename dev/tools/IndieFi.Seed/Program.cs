using IndieFi.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IndieFi.Seed;

internal sealed class Program
{
    private const string ConnectionVariable = "ConnectionStrings__Database";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            WriteUsage();
            return 64;
        }

        try
        {
            var configuration = BuildConfiguration();
            var connection = configuration.GetConnectionString("Database")
                ?? configuration[ConnectionVariable];
            if (string.IsNullOrWhiteSpace(connection))
            {
                throw new ArgumentException(
                    $"Set {ConnectionVariable}; database credentials are not accepted as command-line arguments.");
            }

            var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connection)
                .Options;

            return args[0] switch
            {
                "prepare" => await PrepareCommand.RunAsync(
                    dbOptions,
                    configuration,
                    PrepareOptions.Parse(args[1..])),
                "audit" => await AuditCommand.RunAsync(
                    dbOptions,
                    AuditOptions.Parse(args[1..])),
                _ => throw new ArgumentException($"Unknown command: {args[0]}")
            };
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteUsage();
            return 64;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Seed command failed with {exception.GetType().Name}; no secret value was printed.");
            return 1;
        }
    }

    private static IConfiguration BuildConfiguration()
    {
        var configuration = new ConfigurationManager();
        configuration.AddUserSecrets<Program>(optional: true);
        configuration.AddEnvironmentVariables();
        return configuration;
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine(
            "Usage:\n" +
            "  IndieFi.Seed prepare --run-id <id> --output <manifest.json> --credentials <ignored.json> " +
            "--base-url <origin> --commit <sha> " +
            "[--owners N --users N --late-users N --retry-users N --games N --keys N --opens-in N]\n" +
            "  IndieFi.Seed audit --manifest <manifest.json> --output <audit.json> --phase <before|after>\n" +
            "Prepare also requires the configured protection keys.");
    }
}
