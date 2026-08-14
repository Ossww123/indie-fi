using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using IndieFi.Web.Infrastructure.Cryptography;

namespace IndieFi.Web.Modules.Accounts.Application;

public sealed record RequestIpHash(string KeyId, byte[] Value);

public sealed class RequestIpProtectionService
{
    public const int HashSize = 32;

    private static readonly byte[] PurposePrefix =
        "IndieFi:request-ip-rate-limit:v1:"u8.ToArray();

    private readonly IKeyProvider _keyProvider;

    public RequestIpProtectionService(IKeyProvider keyProvider)
    {
        _keyProvider = keyProvider;
    }

    public RequestIpHash CreateCurrentHash(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return CreateHash(
            Normalize(address),
            _keyProvider.GetCurrent(KeyPurpose.AccountRequestIpLookup));
    }

    public IReadOnlyList<RequestIpHash> CreateReadableHashes(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var normalized = Normalize(address);
        return _keyProvider.GetReadable(KeyPurpose.AccountRequestIpLookup)
            .Select(key => CreateHash(normalized, key))
            .ToArray();
    }

    private static RequestIpHash CreateHash(IPAddress address, KeyMaterial key)
    {
        var addressBytes = address.GetAddressBytes();
        var payload = new byte[PurposePrefix.Length + 1 + addressBytes.Length];
        PurposePrefix.CopyTo(payload, 0);
        payload[PurposePrefix.Length] = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => 4,
            AddressFamily.InterNetworkV6 => 6,
            _ => throw new ArgumentException("Only IPv4 and IPv6 addresses are supported."),
        };
        addressBytes.CopyTo(payload, PurposePrefix.Length + 1);

        try
        {
            return new RequestIpHash(
                key.Id,
                HMACSHA256.HashData(key.Secret.Span, payload));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;
}
