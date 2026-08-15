using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PublicKeyDropReaderTests
{
    private readonly PostgreSqlFixture _fixture;

    public PublicKeyDropReaderTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListForGameAsync_UsesDatabaseTimeAndAuthoritativeInventoryForEveryStatus()
    {
        long gameId;
        long scheduledDropId;
        long activeDropId;
        long soldOutDropId;
        long closedDropId;

        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var owner = TestAccountProtection.AddUser(
                arrangeDb,
                $"{Guid.NewGuid():N}@example.test");
            var recipient = TestAccountProtection.AddUser(
                arrangeDb,
                $"{Guid.NewGuid():N}@example.test");
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);

            var game = new Game
            {
                OwnerId = owner.Id,
                Title = $"Public drop game {Guid.NewGuid():N}",
            };
            arrangeDb.Games.Add(game);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameId = game.Id;

            var future = DateTimeOffset.UtcNow.AddHours(1);
            var scheduledDrop = NewDrop(game, owner, future, totalKeys: 1);
            var activeDrop = NewDrop(game, owner, future, totalKeys: 2);
            var soldOutDrop = NewDrop(game, owner, future, totalKeys: 1);
            var closedDrop = NewDrop(game, owner, future, totalKeys: 1);
            closedDrop.ClosedAt = DateTimeOffset.UtcNow;
            arrangeDb.KeyDrops.AddRange(
                scheduledDrop,
                activeDrop,
                soldOutDrop,
                closedDrop);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);

            TestAccountProtection.AddGameKey(
                arrangeDb,
                scheduledDrop,
                $"SCHEDULED-{Guid.NewGuid():N}");
            var activeGrantedKey = TestAccountProtection.AddGameKey(
                arrangeDb,
                activeDrop,
                $"ACTIVE-GRANTED-{Guid.NewGuid():N}");
            TestAccountProtection.AddGameKey(
                arrangeDb,
                activeDrop,
                $"ACTIVE-AVAILABLE-{Guid.NewGuid():N}");
            var soldOutKey = TestAccountProtection.AddGameKey(
                arrangeDb,
                soldOutDrop,
                $"SOLD-OUT-{Guid.NewGuid():N}");
            TestAccountProtection.AddGameKey(
                arrangeDb,
                closedDrop,
                $"CLOSED-{Guid.NewGuid():N}");
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);

            arrangeDb.KeyGrants.AddRange(
                new KeyGrant
                {
                    DropId = activeDrop.Id,
                    UserId = recipient.Id,
                    KeyId = activeGrantedKey.Id,
                },
                new KeyGrant
                {
                    DropId = soldOutDrop.Id,
                    UserId = recipient.Id,
                    KeyId = soldOutKey.Id,
                });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);

            // The exact boundary comes from PostgreSQL, not the test or web-server clock.
            await arrangeDb.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE key_drops SET opens_at = now() WHERE id = {activeDrop.Id}",
                TestContext.Current.CancellationToken);

            scheduledDropId = scheduledDrop.Id;
            activeDropId = activeDrop.Id;
            soldOutDropId = soldOutDrop.Id;
            closedDropId = closedDrop.Id;
        }

        await using var readDb = _fixture.NewDbContext();
        var reader = new PublicKeyDropReader(readDb);
        var drops = await reader.ListForGameAsync(
            gameId,
            TestContext.Current.CancellationToken);
        var byId = drops.ToDictionary(drop => drop.Id);

        Assert.Equal(KeyDropStatus.Scheduled, byId[scheduledDropId].Status);
        Assert.Equal((1, 1, "오픈 예정", false), Summary(byId[scheduledDropId]));
        Assert.True(byId[scheduledDropId].CanAddInventory);
        Assert.True(byId[scheduledDropId].CanCloseEarly);
        Assert.Equal(KeyDropStatus.Active, byId[activeDropId].Status);
        Assert.Equal((2, 1, "진행 중", true), Summary(byId[activeDropId]));
        Assert.True(byId[activeDropId].CanAddInventory);
        Assert.True(byId[activeDropId].CanCloseEarly);
        Assert.Equal(KeyDropStatus.SoldOut, byId[soldOutDropId].Status);
        Assert.Equal((1, 0, "소진", false), Summary(byId[soldOutDropId]));
        Assert.False(byId[soldOutDropId].CanAddInventory);
        Assert.False(byId[soldOutDropId].CanCloseEarly);
        Assert.Equal(KeyDropStatus.ClosedEarly, byId[closedDropId].Status);
        Assert.Equal((1, 1, "조기 마감", false), Summary(byId[closedDropId]));
        Assert.False(byId[closedDropId].CanAddInventory);
        Assert.False(byId[closedDropId].CanCloseEarly);

        var activeDetail = await reader.GetAsync(
            activeDropId,
            TestContext.Current.CancellationToken);
        Assert.NotNull(activeDetail);
        Assert.Equal(byId[activeDropId], activeDetail);
    }

    [Fact]
    public void PublicDetailContract_DoesNotExposeOwnerParticipantOrKeyMaterial()
    {
        var propertyNames = typeof(PublicKeyDropDetail)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("OwnerId", propertyNames);
        Assert.DoesNotContain("UserId", propertyNames);
        Assert.DoesNotContain("KeyValue", propertyNames);
        Assert.DoesNotContain("ClosedAt", propertyNames);
        Assert.DoesNotContain("StateVersion", propertyNames);
    }

    private static (int Total, int Remaining, string Status, bool CanClaim) Summary(
        PublicKeyDropDetail drop) =>
        (drop.TotalKeys, drop.RemainingKeys, drop.StatusText, drop.CanClaim);

    private static KeyDrop NewDrop(
        Game game,
        User owner,
        DateTimeOffset opensAt,
        int totalKeys) => new()
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = opensAt,
            TotalKeys = totalKeys,
        };
}
