using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container =
        new RedisBuilder("redis:8.8.1-alpine").Build();
    private WaitingRoomRedisConnection? _connection;
    private WaitingRoomRedisKeys? _keys;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var redisOptions = Options.Create(new WaitingRoomRedisOptions
        {
            Enabled = true,
            ConnectionString = _container.GetConnectionString() + ",allowAdmin=true",
            KeyPrefix = "test:waiting-room:v1",
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });
        _connection = new WaitingRoomRedisConnection(redisOptions);
        _keys = new WaitingRoomRedisKeys(redisOptions);
        await _connection.GetAsync();
    }

    public RedisWaitingRoomStore NewStore(WaitingRoomPolicyOptions? policy = null) =>
        new(
            GetConnection(),
            GetKeys(),
            Options.Create(policy ?? new WaitingRoomPolicyOptions()));

    public string ConnectionString => _container.GetConnectionString();

    public WaitingRoomRedisKeys GetKeys() =>
        _keys ?? throw new InvalidOperationException("Redis fixture is not initialized.");

    public async Task<IDatabase> GetDatabaseAsync() =>
        (await GetConnection().GetAsync()).GetDatabase();

    public WaitingRoomRedisConnection GetConnection() =>
        _connection
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
