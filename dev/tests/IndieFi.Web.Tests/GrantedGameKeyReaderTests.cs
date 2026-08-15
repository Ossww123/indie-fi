using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using System.Security.Cryptography;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GrantedGameKeyReaderTests
{
    private readonly PostgreSqlFixture _fixture;

    public GrantedGameKeyReaderTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetForUserAsync_WhenEncryptedKeyWasGranted_ReturnsOnlyToRecipient()
    {
        const string Secret = "ENCRYPTED-CLAIMED-GAME-KEY";
        long dropId;
        long recipientId;
        long otherUserId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var (drop, recipient, otherUser) = await CreateDropAndUsersAsync(arrangeDb);
            dropId = drop.Id;
            recipientId = recipient.Id;
            otherUserId = otherUser.Id;
            var value = ParseKey(Secret);
            var protection = TestAccountProtection.CreateGameKeyProtectionService();
            var protectedKey = protection.Protect(value);
            var gameKey = new GameKey
            {
                DropId = drop.Id,
                Ciphertext = protectedKey.Ciphertext,
                Nonce = protectedKey.Nonce,
                AuthenticationTag = protectedKey.AuthenticationTag,
                EncryptionKeyId = protectedKey.EncryptionKeyId,
            };
            arrangeDb.GameKeys.Add(gameKey);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            arrangeDb.KeyGrants.Add(new KeyGrant
            {
                DropId = drop.Id,
                UserId = recipient.Id,
                KeyId = gameKey.Id,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        string? recipientResult;
        string? otherUserResult;
        await using (var readDb = _fixture.NewDbContext())
        {
            var reader = NewReader(readDb);
            recipientResult = await reader.GetForUserAsync(
                dropId,
                recipientId,
                TestContext.Current.CancellationToken);
            otherUserResult = await reader.GetForUserAsync(
                dropId,
                otherUserId,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(Secret, recipientResult);
        Assert.Null(otherUserResult);
    }

    [Fact]
    public async Task GetForUserAsync_WhenCiphertextIsTampered_FailsWithoutExposingKey()
    {
        const string Secret = "TAMPERED-ENCRYPTED-GAME-KEY";
        long dropId;
        long recipientId;
        long gameKeyId;
        await using (var arrangeDb = _fixture.NewOwnerDbContext())
        {
            var (drop, recipient, _) = await CreateDropAndUsersAsync(arrangeDb);
            dropId = drop.Id;
            recipientId = recipient.Id;
            var gameKey = TestAccountProtection.AddGameKey(
                arrangeDb,
                drop,
                Secret);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameKeyId = gameKey.Id;
            arrangeDb.KeyGrants.Add(new KeyGrant
            {
                DropId = drop.Id,
                UserId = recipient.Id,
                KeyId = gameKey.Id,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);

            var tamperedTag = gameKey.AuthenticationTag.ToArray();
            tamperedTag[0] ^= 0xff;
            gameKey.AuthenticationTag = tamperedTag;
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readDb = _fixture.NewDbContext();
        var exception = await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() =>
            NewReader(readDb).GetForUserAsync(
                dropId,
                recipientId,
                TestContext.Current.CancellationToken));

        Assert.True(gameKeyId > 0);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
    }

    private static GrantedGameKeyReader NewReader(AppDbContext db) => new(
        db,
        TestAccountProtection.CreateGameKeyProtectionService());

    private static GameKeyValue ParseKey(string value)
    {
        var parsed = GameKeyPolicy.ParseBatch(value);
        Assert.True(parsed.IsValid);
        return Assert.Single(parsed.Keys);
    }

    private static async Task<(KeyDrop Drop, User Recipient, User OtherUser)>
        CreateDropAndUsersAsync(AppDbContext db)
    {
        var owner = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        var recipient = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        var otherUser = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var game = new Game
        {
            OwnerId = owner.Id,
            Title = $"Game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.AddHours(-1),
            TotalKeys = 1,
        };
        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (drop, recipient, otherUser);
    }
}
