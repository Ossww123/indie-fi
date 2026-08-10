using IndieFi.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private const string ApplicationRolePassword = "indiefi_app_test_password";
    private const string SetApplicationRolePasswordSql =
        "ALTER ROLE indiefi_app PASSWORD '" + ApplicationRolePassword + "';";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("indiefi_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var ownerOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;

        await using var ownerDb = new AppDbContext(ownerOptions);
        await ownerDb.Database.MigrateAsync();
        await ownerDb.Database.ExecuteSqlRawAsync(SetApplicationRolePasswordSql);
    }

    // Page tests boot the whole app, so they need the address of this container
    // rather than a DbContext. The fixture owns the container, so it answers for it.
    public string ConnectionString => CreateApplicationConnectionString();

    public AppDbContext NewDbContext()
    {
        return NewDbContext([]);
    }

    public AppDbContext NewDbContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(CreateApplicationConnectionString())
            .AddInterceptors(interceptors)
            .Options;

        return new AppDbContext(options);
    }

    private string CreateApplicationConnectionString()
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Username = "indiefi_app",
            Password = ApplicationRolePassword,
        };

        return builder.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL";
}
