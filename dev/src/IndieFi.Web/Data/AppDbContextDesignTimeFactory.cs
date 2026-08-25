using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IndieFi.Web.Data;

public sealed class AppDbContextDesignTimeFactory
    : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string RuntimeConnectionEnvironmentVariable =
        "ConnectionStrings__Database";

    private const string DesignTimeConnectionString =
        "Host=127.0.0.1;Port=5432;Database=indiefi_design_time;" +
        "Username=indiefi_design_time;Password=not-used";

    public AppDbContext CreateDbContext(string[] args)
    {
        var runtimeConnectionString = Environment.GetEnvironmentVariable(
            RuntimeConnectionEnvironmentVariable);
        var connectionString = string.IsNullOrWhiteSpace(runtimeConnectionString)
            ? DesignTimeConnectionString
            : runtimeConnectionString;

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .EnableSensitiveDataLogging(false)
            .Options;

        return new AppDbContext(options);
    }
}
