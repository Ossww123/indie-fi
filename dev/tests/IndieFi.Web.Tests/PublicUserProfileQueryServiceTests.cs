using IndieFi.Web.Entities;
using IndieFi.Web.Data;
using IndieFi.Web.Modules.Credits.Domain;
using IndieFi.Web.Modules.Profiles.Application;
using IndieFi.Web.Modules.Profiles.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PublicUserProfileQueryServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public PublicUserProfileQueryServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public async Task Get_WhenUserIsNotPubliclyAddressable_ReturnsNull(long userId)
    {
        await using var db = _fixture.NewDbContext();

        var profile = await new PublicUserProfileQueryService(db).GetAsync(
            userId,
            TestContext.Current.CancellationToken);

        Assert.Null(profile);
    }

    [Fact]
    public async Task Get_WhenProfileRowDoesNotExist_UsesFallbackAndEmptyValues()
    {
        long userId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            userId = (await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0)).Id;
        }

        await using var db = _fixture.NewDbContext();
        var profile = await new PublicUserProfileQueryService(db).GetAsync(
            userId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(profile);
        Assert.Equal(userId, profile.UserId);
        Assert.Equal($"user#{userId}", profile.DisplayName);
        Assert.Empty(profile.Biography);
        Assert.Equal(0, profile.CompletedReviewCount);
        Assert.Empty(profile.Games);
    }

    [Fact]
    public async Task Get_ReturnsNicknameBiographyOrderedGamesAndCompletedReviewCount()
    {
        long userId;
        long firstGameId;
        long secondGameId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var user = await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 0);
            userId = user.Id;
            var profileChangedAt = DateTimeOffset.UtcNow.AddDays(-8);
            arrangeDb.UserProfiles.Add(new UserProfile
            {
                UserId = userId,
                Nickname = "PublicPlayer",
                NormalizedNickname = "PUBLICPLAYER",
                Biography = "공개 소개",
                NicknameChangedAt = profileChangedAt,
                CreatedAt = profileChangedAt,
                UpdatedAt = profileChangedAt,
            });
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);

            var firstGame = await ReviewTestData.CreateGameAsync(arrangeDb, userId);
            firstGame.Description = "첫 게임";
            firstGameId = firstGame.Id;
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            var secondGame = await ReviewTestData.CreateGameAsync(arrangeDb, userId);
            secondGame.Description = "둘째 게임";
            secondGameId = secondGame.Id;
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);

            await CreateCompletedFeedbackAsync(
                arrangeDb,
                userId,
                new string('가', 30));
            await CreateCompletedFeedbackAsync(
                arrangeDb,
                userId,
                new string('나', 30));
        }

        await using var db = _fixture.NewDbContext();
        var profile = await new PublicUserProfileQueryService(db).GetAsync(
            userId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(profile);
        Assert.Equal("PublicPlayer", profile.DisplayName);
        Assert.Equal("공개 소개", profile.Biography);
        Assert.Equal(2, profile.CompletedReviewCount);
        Assert.Equal([secondGameId, firstGameId], profile.Games.Select(game => game.Id));
        Assert.Equal(["둘째 게임", "첫 게임"], profile.Games.Select(game => game.Description));
    }

    private static async Task CreateCompletedFeedbackAsync(
        AppDbContext db,
        long reviewerId,
        string body)
    {
        var requester = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 1);
        var game = await ReviewTestData.CreateGameAsync(db, requester.Id);
        var request = await ReviewTestData.CreateQueuedRequestAsync(
            db,
            requester.Id,
            game.Id);
        var completedAt = DateTimeOffset.UtcNow;
        request.Status = ReviewRequestStatus.Completed;
        request.CompletedAt = completedAt;
        var assignment = new ReviewAssignment
        {
            RequestId = request.Id,
            GameId = game.Id,
            ReviewerId = reviewerId,
            ClaimOperationId = Guid.NewGuid(),
            Status = ReviewAssignmentStatus.Completed,
            LeasedUntil = completedAt.AddHours(1),
            CompletedAt = completedAt,
        };
        db.ReviewAssignments.Add(assignment);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var earnEntry = new CreditLedgerEntry
        {
            UserId = reviewerId,
            Kind = CreditEntryKind.Earn,
            Delta = 1,
        };
        db.CreditLedgerEntries.Add(earnEntry);
        db.FeedbackSubmissions.Add(new FeedbackSubmission
        {
            AssignmentId = assignment.Id,
            ReviewerId = reviewerId,
            EarnEntry = earnEntry,
            Body = body,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
