using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace IndieFi.Web.Modules.Games.Application;

public sealed record PublicGameDiscoveryCursorBoundary(
    DateTimeOffset CreatedAt,
    long GameId);

public enum PublicGameDiscoveryCursorDecodeStatus
{
    Success,
    Malformed,
    Expired,
    FilterMismatch,
}

public sealed record PublicGameDiscoveryCursorDecodeResult(
    PublicGameDiscoveryCursorDecodeStatus Status,
    PublicGameDiscoveryCursorBoundary? Boundary);

public sealed class PublicGameDiscoveryCursorCodec
{
    private const int CurrentVersion = 1;
    private const int FingerprintBytes = 32;
    private const int MaximumCursorLength = 2048;

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;

    public PublicGameDiscoveryCursorCodec(
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _protector = dataProtectionProvider.CreateProtector(
            "IndieFi.Web",
            "Games",
            "PublicGameDiscoveryCursor",
            "v1");
        _timeProvider = timeProvider;
    }

    public string Encode(
        PublicGameDiscoveryCursorBoundary boundary,
        PublicGameDiscoveryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boundary.GameId);

        var createdAtUtc = boundary.CreatedAt.ToUniversalTime();
        var expiresAt = _timeProvider.GetUtcNow() + Lifetime;
        var payload = new CursorPayload(
            CurrentVersion,
            expiresAt.ToUnixTimeSeconds(),
            createdAtUtc.UtcDateTime.Ticks,
            boundary.GameId,
            WebEncoders.Base64UrlEncode(CreateFilterFingerprint(filter)));
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        return WebEncoders.Base64UrlEncode(_protector.Protect(plaintext));
    }

    public PublicGameDiscoveryCursorDecodeResult Decode(
        string cursor,
        PublicGameDiscoveryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (string.IsNullOrWhiteSpace(cursor)
            || cursor.Length > MaximumCursorLength)
        {
            return Failure(PublicGameDiscoveryCursorDecodeStatus.Malformed);
        }

        try
        {
            var protectedPayload = WebEncoders.Base64UrlDecode(cursor);
            var plaintext = _protector.Unprotect(protectedPayload);
            var payload = JsonSerializer.Deserialize<CursorPayload>(plaintext);
            if (!TryValidatePayload(
                    payload,
                    out var createdAtUtc,
                    out var fingerprint,
                    out var expiresAt))
            {
                return Failure(PublicGameDiscoveryCursorDecodeStatus.Malformed);
            }

            if (expiresAt <= _timeProvider.GetUtcNow())
            {
                return Failure(PublicGameDiscoveryCursorDecodeStatus.Expired);
            }

            var expectedFingerprint = CreateFilterFingerprint(filter);
            if (!CryptographicOperations.FixedTimeEquals(
                    fingerprint,
                    expectedFingerprint))
            {
                return Failure(
                    PublicGameDiscoveryCursorDecodeStatus.FilterMismatch);
            }

            return new PublicGameDiscoveryCursorDecodeResult(
                PublicGameDiscoveryCursorDecodeStatus.Success,
                new PublicGameDiscoveryCursorBoundary(
                    createdAtUtc,
                    payload!.GameId));
        }
        catch (Exception exception) when (
            exception is CryptographicException
            or FormatException
            or JsonException
            or ArgumentOutOfRangeException)
        {
            return Failure(PublicGameDiscoveryCursorDecodeStatus.Malformed);
        }
    }

    private static bool TryValidatePayload(
        CursorPayload? payload,
        out DateTimeOffset createdAtUtc,
        out byte[] fingerprint,
        out DateTimeOffset expiresAt)
    {
        createdAtUtc = default;
        fingerprint = [];
        expiresAt = default;
        if (payload is null
            || payload.Version != CurrentVersion
            || payload.GameId <= 0
            || string.IsNullOrEmpty(payload.FilterFingerprint))
        {
            return false;
        }

        try
        {
            createdAtUtc = new DateTimeOffset(
                payload.CreatedAtUtcTicks,
                TimeSpan.Zero);
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(
                payload.ExpiresAtUnixSeconds);
            fingerprint = WebEncoders.Base64UrlDecode(
                payload.FilterFingerprint);
            return fingerprint.Length == FingerprintBytes;
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or FormatException)
        {
            return false;
        }
    }

    private static byte[] CreateFilterFingerprint(
        PublicGameDiscoveryFilter filter)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteNullableString(writer, "q", filter.Query);
            WriteNullableString(writer, "genre", filter.GenreCode);
            WriteSortedCodes(writer, "tags", filter.TagCodes);
            WriteSortedCodes(writer, "platforms", filter.PlatformCodes);
            WriteNullableUtcTicks(writer, "from", filter.CreatedFromUtc);
            WriteNullableUtcTicks(writer, "before", filter.CreatedBeforeUtc);
            writer.WriteEndObject();
        }

        return SHA256.HashData(buffer.WrittenSpan);
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteString(propertyName, value);
    }

    private static void WriteSortedCodes(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<string> codes)
    {
        writer.WriteStartArray(propertyName);
        foreach (var code in codes.Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(code);
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableUtcTicks(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset? value)
    {
        if (!value.HasValue)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteNumber(
            propertyName,
            value.Value.UtcDateTime.Ticks);
    }

    private static PublicGameDiscoveryCursorDecodeResult Failure(
        PublicGameDiscoveryCursorDecodeStatus status) =>
        new(status, null);

    private sealed record CursorPayload(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("e")] long ExpiresAtUnixSeconds,
        [property: JsonPropertyName("c")] long CreatedAtUtcTicks,
        [property: JsonPropertyName("i")] long GameId,
        [property: JsonPropertyName("f")] string FilterFingerprint);
}
