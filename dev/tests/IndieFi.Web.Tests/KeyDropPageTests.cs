using System.Net;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class KeyDropPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public KeyDropPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    // This first page test proves that the HTTP foundation crosses routing, Razor Pages, and the real database.
    [Fact]
    public async Task Get_DropDetailPage_ShowsGameTitleAndRemainingKeys()
    {
        var (dropId, gameTitle) = await CreateOpenDropAsync();

        // One request needs no shared host lifetime, so this test owns and disposes its factory.
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/KeyDrops/Detail/{dropId}",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(gameTitle, html);
        Assert.Contains("총 3개 중 3개 남음", html);
    }

    private async Task<(long DropId, string GameTitle)> CreateOpenDropAsync()
    {
        await using var db = _fixture.NewDbContext();
        var cancellationToken = TestContext.Current.CancellationToken;

        var owner = new User
        {
            Email = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = "test-password-hash"
        };

        db.Users.Add(owner);
        await db.SaveChangesAsync(cancellationToken);

        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Test Game {Guid.NewGuid():N}"
        };

        db.Games.Add(game);
        await db.SaveChangesAsync(cancellationToken);

        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.AddHours(-1),
            TotalKeys = 3
        };

        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(cancellationToken);

        for (var index = 0; index < 3; index++)
        {
            db.SteamKeys.Add(new SteamKey
            {
                DropId = drop.Id,
                KeyValue = $"TEST-{Guid.NewGuid():N}"
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        return (drop.Id, game.Title);
    }
}
