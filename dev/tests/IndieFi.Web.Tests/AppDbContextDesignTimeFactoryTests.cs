using IndieFi.Web.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class AppDbContextDesignTimeFactoryTests
{
    [Fact]
    public void CreateDbContext_DoesNotRequireRuntimeApplicationConfiguration()
    {
        using var db = new AppDbContextDesignTimeFactory().CreateDbContext([]);

        var connection = db.Database.GetDbConnection();

        Assert.Contains(
            "127.0.0.1",
            connection.DataSource,
            StringComparison.Ordinal);
        Assert.Equal("indiefi_design_time", connection.Database);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }
}
