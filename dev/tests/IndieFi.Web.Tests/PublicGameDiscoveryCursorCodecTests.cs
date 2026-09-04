using IndieFi.Web.Modules.Games.Application;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class PublicGameDiscoveryCursorCodecTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Encode_ThenDecode_RestoresBoundaryWithoutExposingFilter()
    {
        var codec = CreateCodec(out _);
        var filter = CreateFilter(query: "은하수 RPG");
        var boundary = new PublicGameDiscoveryCursorBoundary(
            new DateTimeOffset(2026, 8, 16, 1, 2, 3, TimeSpan.FromHours(9)),
            1042);

        var cursor = codec.Encode(boundary, filter);
        var result = codec.Decode(cursor, filter);

        Assert.Matches("^[A-Za-z0-9_-]+$", cursor);
        Assert.DoesNotContain("은하수", cursor, StringComparison.Ordinal);
        Assert.Equal(PublicGameDiscoveryCursorDecodeStatus.Success, result.Status);
        Assert.Equal(boundary.CreatedAt.ToUniversalTime(), result.Boundary!.CreatedAt);
        Assert.Equal(boundary.GameId, result.Boundary.GameId);
    }

    [Fact]
    public void Encode_SameBoundaryTwice_ProducesOpaqueDifferentValues()
    {
        var codec = CreateCodec(out _);
        var filter = CreateFilter();
        var boundary = new PublicGameDiscoveryCursorBoundary(Now, 17);

        var first = codec.Encode(boundary, filter);
        var second = codec.Encode(boundary, filter);

        Assert.NotEqual(first, second);
        Assert.Equal(
            PublicGameDiscoveryCursorDecodeStatus.Success,
            codec.Decode(first, filter).Status);
        Assert.Equal(
            PublicGameDiscoveryCursorDecodeStatus.Success,
            codec.Decode(second, filter).Status);
    }

    [Fact]
    public void Decode_WhenCursorWasChanged_ReturnsMalformed()
    {
        var codec = CreateCodec(out _);
        var filter = CreateFilter();
        var cursor = codec.Encode(
            new PublicGameDiscoveryCursorBoundary(Now, 17),
            filter);
        var changed = cursor[..^1] + (cursor[^1] == 'A' ? "B" : "A");

        var result = codec.Decode(changed, filter);

        Assert.Equal(PublicGameDiscoveryCursorDecodeStatus.Malformed, result.Status);
        Assert.Null(result.Boundary);
    }

    [Fact]
    public void Decode_WhenFilterChanged_ReturnsFilterMismatch()
    {
        var codec = CreateCodec(out _);
        var cursor = codec.Encode(
            new PublicGameDiscoveryCursorBoundary(Now, 17),
            CreateFilter(query: "galaxy"));

        var result = codec.Decode(cursor, CreateFilter(query: "planet"));

        Assert.Equal(
            PublicGameDiscoveryCursorDecodeStatus.FilterMismatch,
            result.Status);
        Assert.Null(result.Boundary);
    }

    [Fact]
    public void Decode_WhenSetFilterOrderChanged_AcceptsEquivalentFilter()
    {
        var codec = CreateCodec(out _);
        var original = CreateFilter(
            tags: ["co-op", "pixel-art"],
            platforms: ["windows", "linux"]);
        var reordered = CreateFilter(
            tags: ["pixel-art", "co-op"],
            platforms: ["linux", "windows"]);
        var cursor = codec.Encode(
            new PublicGameDiscoveryCursorBoundary(Now, 17),
            original);

        var result = codec.Decode(cursor, reordered);

        Assert.Equal(PublicGameDiscoveryCursorDecodeStatus.Success, result.Status);
    }

    [Fact]
    public void Decode_AfterLifetime_ReturnsExpired()
    {
        var codec = CreateCodec(out var timeProvider);
        var filter = CreateFilter();
        var cursor = codec.Encode(
            new PublicGameDiscoveryCursorBoundary(Now, 17),
            filter);
        timeProvider.UtcNow = Now + PublicGameDiscoveryCursorCodec.Lifetime;

        var result = codec.Decode(cursor, filter);

        Assert.Equal(PublicGameDiscoveryCursorDecodeStatus.Expired, result.Status);
        Assert.Null(result.Boundary);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not+base64")]
    public void Decode_InvalidEnvelope_ReturnsMalformed(string cursor)
    {
        var codec = CreateCodec(out _);

        var result = codec.Decode(cursor, CreateFilter());

        Assert.Equal(PublicGameDiscoveryCursorDecodeStatus.Malformed, result.Status);
    }

    private static PublicGameDiscoveryCursorCodec CreateCodec(
        out MutableTimeProvider timeProvider)
    {
        timeProvider = new MutableTimeProvider(Now);
        return new PublicGameDiscoveryCursorCodec(
            new EphemeralDataProtectionProvider(),
            timeProvider);
    }

    private static PublicGameDiscoveryFilter CreateFilter(
        string? query = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? platforms = null) =>
        new(
            query,
            query?.Split(' ') ?? [],
            "action",
            tags ?? ["co-op"],
            platforms ?? ["windows"],
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
