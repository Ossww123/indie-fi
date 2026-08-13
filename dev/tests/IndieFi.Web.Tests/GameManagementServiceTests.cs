using IndieFi.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class GameManagementServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public GameManagementServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateAsync_RegistersGameForOwnerAndNormalizesBlankDescription()
    {
        long ownerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            ownerId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
        }

        CreateGameResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new GameManagementService(serviceDb)
                .CreateAsync(ownerId, "New Game", "   ");
        }

        await using var verificationDb = _fixture.NewDbContext();
        var game = await verificationDb.Games.SingleAsync(
            item => item.Id == result.GameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(CreateGameOutcome.Created, result.Outcome);
        Assert.Equal("New Game", result.Title);
        Assert.Equal(ownerId, game.OwnerId);
        Assert.Equal("New Game", game.Title);
        Assert.Null(game.Description);
    }

    [Fact]
    public async Task CreateAsync_WhenTitleIsOnlyWhitespace_WritesNothing()
    {
        long ownerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            ownerId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
        }

        CreateGameResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new GameManagementService(serviceDb)
                .CreateAsync(ownerId, "   ", null);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var gameCount = await verificationDb.Games.CountAsync(
            item => item.OwnerId == ownerId,
            TestContext.Current.CancellationToken);

        Assert.Equal(CreateGameOutcome.Invalid, result.Outcome);
        Assert.Equal(
            [GameManagementService.TitleField],
            result.Violations.Select(violation => violation.Field));
        Assert.Null(result.GameId);
        Assert.Equal(0, gameCount);
    }

    [Fact]
    public async Task CreateAsync_WhenTitleAndDescriptionAreBothTooLong_ReportsBothAtOnce()
    {
        long ownerId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            ownerId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
        }

        CreateGameResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new GameManagementService(serviceDb).CreateAsync(
                ownerId,
                new string('T', 201),
                new string('D', 2001));
        }

        await using var verificationDb = _fixture.NewDbContext();
        var gameCount = await verificationDb.Games.CountAsync(
            item => item.OwnerId == ownerId,
            TestContext.Current.CancellationToken);

        Assert.Equal(CreateGameOutcome.Invalid, result.Outcome);
        Assert.Equal(
            [GameManagementService.TitleField, GameManagementService.DescriptionField],
            result.Violations.Select(violation => violation.Field));
        Assert.Equal(0, gameCount);
    }

    [Fact]
    public async Task UpdateAsync_WhenCallerOwnsGame_UpdatesGame()
    {
        long ownerId;
        long gameId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            ownerId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            gameId = (await ReviewTestData.CreateGameAsync(arrangeDb, ownerId)).Id;
        }

        UpdateGameResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new GameManagementService(serviceDb)
                .UpdateAsync(gameId, ownerId, "Updated Game", "Updated description");
        }

        await using var verificationDb = _fixture.NewDbContext();
        var game = await verificationDb.Games.SingleAsync(
            item => item.Id == gameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateGameOutcome.Updated, result.Outcome);
        Assert.Equal("Updated Game", game.Title);
        Assert.Equal("Updated description", game.Description);
    }

    [Fact]
    public async Task UpdateAsync_WhenCallerDoesNotOwnGame_ReturnsNotFoundAndWritesNothing()
    {
        long ownerId;
        long otherUserId;
        long gameId;
        string originalTitle;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            ownerId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            otherUserId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var arrangedGame = await ReviewTestData.CreateGameAsync(arrangeDb, ownerId);
            gameId = arrangedGame.Id;
            originalTitle = arrangedGame.Title;
        }

        UpdateGameResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new GameManagementService(serviceDb)
                .UpdateAsync(gameId, otherUserId, "Attacker Title", "Attacker description");
        }

        await using var verificationDb = _fixture.NewDbContext();
        var verifiedGame = await verificationDb.Games.SingleAsync(
            item => item.Id == gameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateGameOutcome.NotFound, result.Outcome);
        Assert.Equal(originalTitle, verifiedGame.Title);
        Assert.Null(verifiedGame.Description);
    }

    [Fact]
    public async Task UpdateAsync_WhenTitleIsOnlyWhitespace_KeepsExistingGame()
    {
        long ownerId;
        long gameId;
        string originalTitle;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            ownerId = (await ReviewTestData.CreateUserWithCreditAsync(arrangeDb, balance: 0)).Id;
            var arrangedGame = await ReviewTestData.CreateGameAsync(arrangeDb, ownerId);
            gameId = arrangedGame.Id;
            originalTitle = arrangedGame.Title;
        }

        UpdateGameResult result;
        await using (var serviceDb = _fixture.NewDbContext())
        {
            result = await new GameManagementService(serviceDb)
                .UpdateAsync(gameId, ownerId, "   ", null);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var verifiedGame = await verificationDb.Games.SingleAsync(
            item => item.Id == gameId,
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateGameOutcome.Invalid, result.Outcome);
        Assert.Equal(
            [GameManagementService.TitleField],
            result.Violations.Select(violation => violation.Field));
        Assert.Equal(originalTitle, verifiedGame.Title);
    }
}
