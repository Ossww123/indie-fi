using IndieFi.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.PostgreSql;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18")
        .WithDatabase("indiefi_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    // Page tests boot the whole app, so they need the address of this container
    // rather than a DbContext. The fixture owns the container, so it answers for it.
    public string ConnectionString => _container.GetConnectionString();

    public AppDbContext NewDbContext()
    {
        return NewDbContext([]);
    }

    public AppDbContext NewDbContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .AddInterceptors(interceptors)
            .Options;

        return new AppDbContext(options);
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
