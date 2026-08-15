using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IndieFi.Web.Modules.KeyDistributions.Infrastructure;

public sealed class WaitingRoomRedisConnection : IAsyncDisposable
{
    private readonly WaitingRoomRedisOptions _options;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IConnectionMultiplexer? _connection;

    public WaitingRoomRedisConnection(
        IOptions<WaitingRoomRedisOptions> options)
    {
        _options = options.Value;
    }

    public bool Enabled => _options.Enabled;

    public async Task<IConnectionMultiplexer> GetAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            throw new WaitingRoomUnavailableException(
                "The virtual waiting-room Redis connection is disabled.");
        }

        if (_connection is { IsConnected: true })
        {
            return _connection;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsConnected: true })
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            var configuration = ConfigurationOptions.Parse(
                _options.ConnectionString,
                ignoreUnknown: false);
            configuration.AbortOnConnectFail = false;
            configuration.ConnectTimeout = checked((int)_options.ConnectTimeout.TotalMilliseconds);
            configuration.ConnectRetry = 1;

            try
            {
                _connection = await ConnectionMultiplexer.ConnectAsync(configuration);
                return _connection;
            }
            catch (RedisConnectionException exception)
            {
                throw new WaitingRoomUnavailableException(
                    "The virtual waiting-room Redis connection is unavailable.",
                    exception);
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _connectionLock.Dispose();
    }
}

public sealed class WaitingRoomUnavailableException : Exception
{
    public WaitingRoomUnavailableException(string message)
        : base(message)
    {
    }

    public WaitingRoomUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
