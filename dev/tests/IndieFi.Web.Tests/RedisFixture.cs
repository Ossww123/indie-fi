using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container =
        new RedisBuilder("redis:8.8.1-alpine")
            .WithCommand("--appendonly", "yes", "--appendfsync", "everysec")
            .Build();
    private WaitingRoomRedisConnection? _connection;
    private WaitingRoomRedisKeys? _keys;
    private string? _connectionString;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await ConnectAsync();
    }

    public async Task StopAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        await _container.StopAsync();
    }

    public async Task StartAsync()
    {
        await _container.StartAsync();
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        _connectionString = _container.GetConnectionString();
        var redisOptions = Options.Create(CreateOptions());
        _connection = new WaitingRoomRedisConnection(redisOptions);
        _keys = new WaitingRoomRedisKeys(redisOptions);
        await _connection.GetAsync();
    }

    public WaitingRoomRedisOptions CreateOptions() => new()
    {
        Enabled = true,
        ConnectionString = GetCachedConnectionString() + ",allowAdmin=true",
        KeyPrefix = "test:waiting-room:v1",
        ConnectTimeout = TimeSpan.FromSeconds(5),
    };

    public RedisWaitingRoomStore NewStore(WaitingRoomPolicyOptions? policy = null) =>
        new(
            GetConnection(),
            GetKeys(),
            Options.Create(policy ?? new WaitingRoomPolicyOptions()));

    public string ConnectionString => GetCachedConnectionString();

    public WaitingRoomRedisKeys GetKeys() =>
        _keys ?? throw new InvalidOperationException("Redis fixture is not initialized.");

    public async Task<IDatabase> GetDatabaseAsync() =>
        (await GetConnection().GetAsync()).GetDatabase();

    public WaitingRoomRedisConnection GetConnection() =>
        _connection
        ?? throw new InvalidOperationException("Redis fixture is not initialized.");

    private string GetCachedConnectionString() =>
        _connectionString
        ?? throw new InvalidOperationException("Redis fixture is not initialized.");

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
