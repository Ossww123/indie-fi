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

        // ConnectionMultiplexer is designed to live for the process lifetime and
        // reconnect by itself. Replacing it whenever Redis is briefly disconnected
        // can dispose the object while another request is still using its database.
        if (_connection is not null)
        {
            return _connection;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null)
            {
                return _connection;
            }

            var configuration = ConfigurationOptions.Parse(
                _options.ConnectionString,
                ignoreUnknown: false);
            configuration.AbortOnConnectFail = false;
            configuration.BacklogPolicy = BacklogPolicy.FailFast;
            configuration.ConnectTimeout = checked((int)_options.ConnectTimeout.TotalMilliseconds);
            configuration.ConnectRetry = 1;
            configuration.ReconnectRetryPolicy = new LinearRetry(1_000);

            try
            {
                _connection = await ConnectionMultiplexer.ConnectAsync(configuration);
                return _connection;
            }
            catch (RedisConnectionException exception)
            {
                WaitingRoomTelemetry.RecordFailure("redis", "connect");
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
