using System.Net;
using System.Net.Sockets;

namespace IndieFi.Web.Modules.Games.Domain;

public enum GamePlayLinkAddressError
{
    None,
    Required,
    TooLong,
    InvalidFormat,
    UnsupportedScheme,
    UserInfoNotAllowed,
    NonPublicHost,
}

public enum GamePlayLinkDisplayNameError
{
    None,
    Required,
    TooLong,
}

public sealed record GamePlayLinkAddress(string Value);

public static class GamePlayLinkPolicy
{
    public const int MaxLinksPerGame = 5;
    public const int DisplayNameMaxLength = 100;
    public const int UrlMaxLength = 2048;

    public static bool TryCreateAddress(
        string? input,
        out GamePlayLinkAddress? address,
        out GamePlayLinkAddressError error)
    {
        address = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = GamePlayLinkAddressError.Required;
            return false;
        }

        var value = input.Trim();
        if (value.Length > UrlMaxLength)
        {
            error = GamePlayLinkAddressError.TooLong;
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            error = GamePlayLinkAddressError.InvalidFormat;
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = GamePlayLinkAddressError.UnsupportedScheme;
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = GamePlayLinkAddressError.InvalidFormat;
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = GamePlayLinkAddressError.UserInfoNotAllowed;
            return false;
        }

        if (!HasPublicHost(uri))
        {
            error = GamePlayLinkAddressError.NonPublicHost;
            return false;
        }

        address = new GamePlayLinkAddress(uri.AbsoluteUri);
        error = GamePlayLinkAddressError.None;
        return true;
    }

    public static bool TryNormalizeDisplayName(
        string? input,
        out string? displayName,
        out GamePlayLinkDisplayNameError error)
    {
        displayName = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = GamePlayLinkDisplayNameError.Required;
            return false;
        }

        var value = input.Trim();
        if (value.Length > DisplayNameMaxLength)
        {
            error = GamePlayLinkDisplayNameError.TooLong;
            return false;
        }

        displayName = value;
        error = GamePlayLinkDisplayNameError.None;
        return true;
    }

    private static bool HasPublicHost(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var addressText = host.Trim('[', ']');
        if (IPAddress.TryParse(addressText, out var ipAddress))
        {
            return IsPublicIpAddress(ipAddress);
        }

        return uri.HostNameType == UriHostNameType.Dns
            && host.Contains('.', StringComparison.Ordinal);
    }

    private static bool IsPublicIpAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicIpAddress(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] != 0
                && bytes[0] != 10
                && bytes[0] != 127
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
                && !(bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 198 && bytes[1] is 18 or 19)
                && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                && bytes[0] < 224;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.IPv6None)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return false;
        }

        var ipv6Bytes = address.GetAddressBytes();
        var isUniqueLocal = (ipv6Bytes[0] & 0xfe) == 0xfc;
        var isDocumentationRange =
            ipv6Bytes[0] == 0x20
            && ipv6Bytes[1] == 0x01
            && ipv6Bytes[2] == 0x0d
            && ipv6Bytes[3] == 0xb8;
        return !isUniqueLocal && !isDocumentationRange;
    }
}
