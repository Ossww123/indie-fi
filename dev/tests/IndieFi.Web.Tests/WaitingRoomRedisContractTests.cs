using IndieFi.Web.Modules.KeyDistributions.Domain;
using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class WaitingRoomRedisContractTests
{
    [Fact]
    public void Keys_ForSameDrop_UseOneRedisClusterHashTag()
    {
        var keys = NewKeys().All(42).Select(key => key.ToString()).ToArray();

        Assert.Equal(5, keys.Length);
        Assert.All(keys, key => Assert.Contains("{drop:42}", key));
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.DoesNotContain(keys, key => key.Contains("{drop:43}"));
    }

    [Fact]
    public void Keys_WithInvalidDropId_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewKeys().Metadata(0));
    }

    [Theory]
    [InlineData(WaitingRoomEntryState.PreWaiting, 1, 1000, null)]
    [InlineData(WaitingRoomEntryState.Waiting, 2, 2000, null)]
    [InlineData(WaitingRoomEntryState.Admitted, 3, 3000, 4000L)]
    [InlineData(WaitingRoomEntryState.Completed, 4, 5000, null)]
    [InlineData(WaitingRoomEntryState.Expired, 5, 6000, null)]
    [InlineData(WaitingRoomEntryState.Terminal, 6, 7000, null)]
    public void EntryCodec_RoundTripsEverySupportedState(
        WaitingRoomEntryState state,
        long order,
        long lastSeen,
        long? admissionExpires)
    {
        var expected = new WaitingRoomStoredEntry(
            state,
            order,
            lastSeen,
            admissionExpires);

        var actual = WaitingRoomRedisValueCodec.Deserialize(
            WaitingRoomRedisValueCodec.Serialize(expected));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2|1|1|1000|")]
    [InlineData("1|999|1|1000|")]
    [InlineData("1|3|1|1000|")]
    [InlineData("1|2|-1|1000|")]
    public void EntryCodec_RejectsUnknownOrInvalidValues(string value)
    {
        Assert.Throws<FormatException>(() =>
            WaitingRoomRedisValueCodec.Deserialize((RedisValue)value));
    }

    [Fact]
    public void PolicyOptions_DefaultsMatchDecidedWaitingRoomPolicy()
    {
        var options = new WaitingRoomPolicyOptions();

        Assert.True(WaitingRoomPolicyOptions.IsValid(options));
        Assert.Equal(TimeSpan.FromMinutes(30), options.OpensBefore);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ReconnectGrace);
        Assert.Equal(TimeSpan.FromMinutes(2), options.AdmissionLifetime);
        Assert.Equal(10, options.ConcurrentAdmissionLimit);
    }

    private static WaitingRoomRedisKeys NewKeys() =>
        new(Options.Create(new WaitingRoomRedisOptions
        {
            KeyPrefix = "test:waiting-room:v1",
        }));
}
