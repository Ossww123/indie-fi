using System.Net;
using System.Reflection;
using IndieFi.Web.Modules.KeyDistributions.Application;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class WaitingRoomHubTests
{
    private readonly PostgreSqlFixture _fixture;

    public WaitingRoomHubTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Negotiate_RequiresAuthenticatedApplicationSession()
    {
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var anonymousClient = factory.CreateClient();

        using var anonymous = await NegotiateAsync(anonymousClient);

        var (email, password) = await PageTestSupport
            .CreateUserWithKnownPasswordAsync(_fixture);
        using var authenticatedClient = factory.CreateClient();
        await PageTestSupport.LoginAsync(authenticatedClient, email, password);
        using var authenticated = await NegotiateAsync(authenticatedClient);
        var payload = await authenticated.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Contains("connectionId", payload);
    }

    [Fact]
    public void Hub_RequiresAuthorizationAndBuildsServerOwnedDropGroups()
    {
        var authorize = typeof(WaitingRoomHub)
            .GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal("waiting-room:drop:42", WaitingRoomHubGroups.Drop(42));
        Assert.Throws<ArgumentOutOfRangeException>(() => WaitingRoomHubGroups.Drop(0));
    }

    private static Task<HttpResponseMessage> NegotiateAsync(HttpClient client) =>
        client.PostAsync(
            "/hubs/waiting-room/negotiate?negotiateVersion=1",
            new StringContent(string.Empty),
            TestContext.Current.CancellationToken);
}
