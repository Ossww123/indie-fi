using IndieFi.Web.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class AppDbContextDesignTimeFactoryTests
{
    private const string DatabaseEnvironmentVariable =
        "ConnectionStrings__Database";

    [Fact]
    public void CreateDbContext_DoesNotRequireRuntimeApplicationConfiguration()
    {
        var originalValue = Environment.GetEnvironmentVariable(
            DatabaseEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(DatabaseEnvironmentVariable, null);
            using var db = new AppDbContextDesignTimeFactory().CreateDbContext([]);

            var connection = db.Database.GetDbConnection();

            Assert.Contains(
                "127.0.0.1",
                connection.DataSource,
                StringComparison.Ordinal);
            Assert.Equal("indiefi_design_time", connection.Database);
            Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DatabaseEnvironmentVariable,
                originalValue);
        }
    }

    [Fact]
    public void CreateDbContext_UsesOwnerConnectionProvidedByMigrationProcess()
    {
        const string runtimeConnection =
            "Host=127.0.0.1;Port=5432;Database=indiefi;" +
            "Username=indiefi;Password=test-owner-password";
        var originalValue = Environment.GetEnvironmentVariable(
            DatabaseEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                DatabaseEnvironmentVariable,
                runtimeConnection);
            using var db = new AppDbContextDesignTimeFactory().CreateDbContext([]);

            var connection = db.Database.GetDbConnection();

            Assert.Equal("indiefi", connection.Database);
            Assert.Equal("indiefi", connection.GetType()
                .GetProperty("UserName")?
                .GetValue(connection));
            Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DatabaseEnvironmentVariable,
                originalValue);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariableCollection
{
    public const string Name = "Environment-variable tests";
}
