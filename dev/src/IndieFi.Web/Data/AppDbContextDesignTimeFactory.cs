using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IndieFi.Web.Data;

public sealed class AppDbContextDesignTimeFactory
    : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string DesignTimeConnectionString =
        "Host=127.0.0.1;Port=5432;Database=indiefi_design_time;" +
        "Username=indiefi_design_time;Password=not-used";

    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(DesignTimeConnectionString)
            .EnableSensitiveDataLogging(false)
            .Options;

        return new AppDbContext(options);
    }
}
