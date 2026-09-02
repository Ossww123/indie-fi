using IndieFi.Web.Application.Time;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class KoreanStandardTimeTests
{
    [Fact]
    public void FromLocal_InterpretsUnzonedWallClockAsUtcPlusNine()
    {
        var local = new DateTime(2026, 9, 2, 2, 50, 0, DateTimeKind.Unspecified);

        var result = KoreanStandardTime.FromLocal(local);

        Assert.Equal(TimeSpan.FromHours(9), result.Offset);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 1, 17, 50, 0, TimeSpan.Zero),
            result.ToUniversalTime());
    }

    [Fact]
    public void ToLocal_ShowsUtcInstantAsUtcPlusNine()
    {
        var utc = new DateTimeOffset(2026, 9, 1, 17, 50, 0, TimeSpan.Zero);

        var result = KoreanStandardTime.ToLocal(utc);

        Assert.Equal(TimeSpan.FromHours(9), result.Offset);
        Assert.Equal(2, result.Hour);
        Assert.Equal(2, result.Day);
    }
}
