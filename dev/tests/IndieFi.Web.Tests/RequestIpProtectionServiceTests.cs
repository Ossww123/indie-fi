using System.Net;
using IndieFi.Web.Modules.Accounts.Application;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class RequestIpProtectionServiceTests
{
    [Fact]
    public void CreateCurrentHash_NormalizesIpv4MappedAddressWithoutStoringRawAddress()
    {
        var service = TestAccountProtection.CreateRequestIpProtectionService();
        var ipv4 = IPAddress.Parse("203.0.113.30");
        var mapped = ipv4.MapToIPv6();

        var first = service.CreateCurrentHash(ipv4);
        var second = service.CreateCurrentHash(mapped);

        Assert.Equal(first.KeyId, second.KeyId);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(RequestIpProtectionService.HashSize, first.Value.Length);
        Assert.NotEqual(ipv4.GetAddressBytes(), first.Value);
    }

    [Fact]
    public void CreateCurrentHash_DifferentAddressesProduceDifferentValues()
    {
        var service = TestAccountProtection.CreateRequestIpProtectionService();

        var first = service.CreateCurrentHash(IPAddress.Parse("203.0.113.31"));
        var second = service.CreateCurrentHash(IPAddress.Parse("203.0.113.32"));

        Assert.NotEqual(first.Value, second.Value);
    }
}
