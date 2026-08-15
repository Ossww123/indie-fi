using System.Net;
using IndieFi.Web.Data;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Feedback.Application;
using IndieFi.Web.Modules.Games.Application;
using IndieFi.Web.Modules.KeyDistributions.Application;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AccountMutationBlockingTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 13, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public AccountMutationBlockingTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SuspendedAccount_CannotIssueOrConsumeEmailConfirmationToken()
    {
        var userId = await CreateUserAsync(emailConfirmed: false);
        EmailConfirmationTokenIssueResult issued;
        await using (var issueDb = _fixture.NewDbContext())
        {
            issued = await NewEmailConfirmationTokenService(issueDb).IssueAsync(
                userId,
                TestContext.Current.CancellationToken);
        }

        await SuspendDirectlyAsync(userId);

        await using (var blockedIssueDb = _fixture.NewDbContext())
        {
            var blocked = await NewEmailConfirmationTokenService(blockedIssueDb)
                .IssueAsync(userId, TestContext.Current.CancellationToken);
            Assert.Equal(
                EmailConfirmationTokenIssueOutcome.AccountUnavailable,
                blocked.Outcome);
        }

        await using (var consumeDb = _fixture.NewDbContext())
        {
            var consumed = await NewEmailConfirmationTokenService(consumeDb)
                .ConsumeAsync(issued.Token, TestContext.Current.CancellationToken);
            Assert.Equal(EmailConfirmationTokenConsumeOutcome.Invalid, consumed.Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            item => item.Id == userId,
            TestContext.Current.CancellationToken);
        var token = await verificationDb.EmailConfirmationTokens.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);
        Assert.Null(user.EmailConfirmedAt);
        Assert.Null(token.UsedAt);
        Assert.Null(token.RevokedAt);
    }

    [Fact]
    public async Task SuspendedAccounts_ReceiveGenericRecoveryResponsesWithoutQueuedMail()
    {
        var confirmationEmail = $"blocked-confirm-{Guid.NewGuid():N}@example.test";
        var resetEmail = $"blocked-reset-{Guid.NewGuid():N}@example.test";
        var confirmationUserId = await CreateUserAsync(
            emailConfirmed: false,
            email: confirmationEmail);
        var resetUserId = await CreateUserAsync(
            emailConfirmed: true,
            email: resetEmail);
        await SuspendDirectlyAsync(confirmationUserId);
        await SuspendDirectlyAsync(resetUserId);

        EmailConfirmationResendResult confirmationResult;
        await using (var confirmationDb = _fixture.NewDbContext())
        {
            confirmationResult = await NewConfirmationResendService(confirmationDb)
                .RequestAsync(
                    confirmationEmail,
                    IPAddress.Parse("198.51.100.210"),
                    TestContext.Current.CancellationToken);
        }

        PasswordResetRequestResult resetResult;
        await using (var resetDb = _fixture.NewDbContext())
        {
            resetResult = await NewPasswordResetRequestService(resetDb).RequestAsync(
                resetEmail,
                IPAddress.Parse("198.51.100.211"),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            EmailConfirmationResendOutcome.RequestAccepted,
            confirmationResult.Outcome);
        Assert.Equal(PasswordResetRequestOutcome.RequestAccepted, resetResult.Outcome);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.EmailConfirmationTokens.AnyAsync(
            item => item.UserId == confirmationUserId,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.PasswordResetTokens.AnyAsync(
            item => item.UserId == resetUserId,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.AuthenticationEmailOutboxMessages.AnyAsync(
            item => item.UserId == confirmationUserId || item.UserId == resetUserId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SuspendedAccount_CannotChangeOrResetPassword()
    {
        const string currentPassword = "Current-Password-123!";
        const string newPassword = "Replacement-Password-456!";
        var userId = await CreateUserAsync(
            emailConfirmed: true,
            password: currentPassword,
            sessionVersion: 9);
        PasswordResetTokenIssueResult issued;
        await using (var issueDb = _fixture.NewDbContext())
        {
            issued = await NewPasswordResetTokenService(issueDb).IssueAsync(
                userId,
                TestContext.Current.CancellationToken);
        }

        await SuspendDirectlyAsync(userId);

        await using (var blockedIssueDb = _fixture.NewDbContext())
        {
            var blockedIssue = await NewPasswordResetTokenService(blockedIssueDb)
                .IssueAsync(userId, TestContext.Current.CancellationToken);
            Assert.Equal(
                PasswordResetTokenIssueOutcome.AccountUnavailable,
                blockedIssue.Outcome);
        }

        await using (var changeDb = _fixture.NewDbContext())
        {
            var change = await new AccountPasswordChangeService(
                    changeDb,
                    new PasswordHasher<User>())
                .ChangeAsync(
                    userId,
                    currentPassword,
                    newPassword,
                    TestContext.Current.CancellationToken);
            Assert.Equal(AccountPasswordChangeOutcome.AccountUnavailable, change.Outcome);
        }

        await using (var completionDb = _fixture.NewDbContext())
        {
            var completion = await new PasswordResetCompletionService(
                    completionDb,
                    new PasswordHasher<User>(),
                    new FixedTimeProvider(Now))
                .CompleteAsync(
                    issued.Token,
                    newPassword,
                    TestContext.Current.CancellationToken);
            Assert.Equal(PasswordResetCompletionOutcome.InvalidToken, completion.Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        var user = await verificationDb.Users.SingleAsync(
            item => item.Id == userId,
            TestContext.Current.CancellationToken);
        var token = await verificationDb.PasswordResetTokens.SingleAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken);
        Assert.Equal(9, user.SessionVersion);
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            new PasswordHasher<User>().VerifyHashedPassword(
                user,
                user.PasswordHash!,
                currentPassword));
        Assert.Equal(
            PasswordVerificationResult.Failed,
            new PasswordHasher<User>().VerifyHashedPassword(
                user,
                user.PasswordHash!,
                newPassword));
        Assert.Null(token.UsedAt);
        Assert.Null(token.RevokedAt);
    }

    [Fact]
    public async Task SuspendedAccount_CannotChangeGamesOrKeyDistributions()
    {
        var userId = await CreateUserAsync(emailConfirmed: true);
        long gameId;
        long dropId;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var game = new Game
            {
                OwnerId = userId,
                Title = "Original title",
            };
            arrangeDb.Games.Add(game);
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            var drop = new KeyDrop
            {
                GameId = game.Id,
                OwnerId = userId,
                OpensAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                TotalKeys = 1,
            };
            arrangeDb.KeyDrops.Add(drop);
            TestAccountProtection.AddGameKey(
                arrangeDb,
                drop,
                $"BLOCKED-{Guid.NewGuid():N}");
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            gameId = game.Id;
            dropId = drop.Id;
        }

        await SuspendDirectlyAsync(userId);

        await using (var gameDb = _fixture.NewDbContext())
        {
            var games = new GameManagementService(gameDb);
            var create = await games.CreateAsync(userId, "Blocked game", null);
            var update = await games.UpdateAsync(
                gameId,
                userId,
                "Changed title",
                null);
            Assert.Equal(CreateGameOutcome.AccountUnavailable, create.Outcome);
            Assert.Equal(UpdateGameOutcome.AccountUnavailable, update.Outcome);
        }

        await using (var scheduleDb = _fixture.NewDbContext())
        {
            var schedule = await new KeyDropSchedulingService(
                scheduleDb,
                new GameKeyRegistrationWriter(
                    scheduleDb,
                    TestAccountProtection.CreateGameKeyProtectionService())).ScheduleAsync(
                gameId,
                userId,
                DateTimeOffset.UtcNow.AddHours(1),
                "BLOCKED-NEW-KEY");
            Assert.Equal(ScheduleKeyDropOutcome.AccountUnavailable, schedule.Outcome);
        }

        await using (var claimDb = _fixture.NewDbContext())
        {
            var claim = await new KeyClaimService(
                    claimDb,
                    new KeyDropMutationLock(claimDb),
                    NullLogger<KeyClaimService>.Instance,
                    AllowKeyClaimAdmissionGate.Instance)
                .ClaimAsync(
                    dropId,
                    userId,
                    TestContext.Current.CancellationToken);
            Assert.Equal(ClaimOutcome.AccountUnavailable, claim.Outcome);
        }

        await using (var inventoryDb = _fixture.NewDbContext())
        {
            var inventory = await new KeyDropInventoryService(
                    inventoryDb,
                    new GameKeyRegistrationWriter(
                        inventoryDb,
                        TestAccountProtection.CreateGameKeyProtectionService()),
                    new KeyDropMutationLock(inventoryDb))
                .AddAsync(
                    dropId,
                    userId,
                    "BLOCKED-INVENTORY-KEY",
                    TestContext.Current.CancellationToken);
            Assert.Equal(
                AddKeyInventoryOutcome.AccountUnavailable,
                inventory.Outcome);
        }

        await using (var closeDb = _fixture.NewDbContext())
        {
            var close = await new KeyDropCloseService(
                    closeDb,
                    new KeyDropMutationLock(closeDb))
                .CloseAsync(
                    dropId,
                    userId,
                    TestContext.Current.CancellationToken);
            Assert.Equal(
                CloseKeyDropOutcome.AccountUnavailable,
                close.Outcome);
        }

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(1, await verificationDb.Games.CountAsync(
            item => item.OwnerId == userId,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            "Original title",
            await verificationDb.Games
                .Where(item => item.Id == gameId)
                .Select(item => item.Title)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await verificationDb.KeyDrops.CountAsync(
            item => item.OwnerId == userId,
            TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.KeyGrants.AnyAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SuspendedAccounts_CannotChangeReviewState()
    {
        var assignment = await ReviewTestData.CreateLiveAssignmentAsync(_fixture);
        await SuspendDirectlyAsync(assignment.ReviewerId);

        FeedbackSubmissionResult submission;
        ReviewClaimResult claim;
        ReviewLeaseSweepResult sweep;
        await using (var reviewerDb = _fixture.NewDbContext())
        {
            submission = await new FeedbackSubmissionService(
                    reviewerDb,
                    NullLogger<FeedbackSubmissionService>.Instance)
                .SubmitAsync(
                    assignment.AssignmentId,
                    assignment.ReviewerId,
                    "This feedback must not be stored for a suspended reviewer.");
            claim = await new ReviewClaimService(
                    reviewerDb,
                    NullLogger<ReviewClaimService>.Instance)
                .ClaimNextAsync(assignment.ReviewerId);
            sweep = await new ReviewLeaseSweepService(
                    reviewerDb,
                    NullLogger<ReviewLeaseSweepService>.Instance)
                .SweepAsync(assignment.ReviewerId);
        }

        long requesterId;
        long requestId;
        long gameId;
        int balanceBefore;
        await using (var arrangeDb = _fixture.NewDbContext())
        {
            var requester = await ReviewTestData.CreateUserWithCreditAsync(
                arrangeDb,
                balance: 2);
            var game = await ReviewTestData.CreateGameAsync(arrangeDb, requester.Id);
            var request = await ReviewTestData.CreateQueuedRequestAsync(
                arrangeDb,
                requester.Id,
                game.Id);
            requester.AccountStatus = AccountStatus.IndefinitelySuspended;
            requester.SuspendedUntil = null;
            await arrangeDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            requesterId = requester.Id;
            requestId = request.Id;
            gameId = game.Id;
            balanceBefore = await arrangeDb.Users
                .Where(item => item.Id == requester.Id)
                .Select(item => item.CreditBalance)
                .SingleAsync(TestContext.Current.CancellationToken);
        }

        ReviewRequestResult requested;
        ReviewRequestResult cancelled;
        await using (var requesterDb = _fixture.NewDbContext())
        {
            var requests = new ReviewRequestService(
                requesterDb,
                NullLogger<ReviewRequestService>.Instance);
            requested = await requests.RequestAsync(gameId, requesterId);
            cancelled = await requests.CancelAsync(requestId, requesterId);
        }

        Assert.Equal(FeedbackSubmissionOutcome.AccountUnavailable, submission.Outcome);
        Assert.Equal(ReviewClaimOutcome.AccountUnavailable, claim.Outcome);
        Assert.Equal(ReviewLeaseSweepOutcome.AccountUnavailable, sweep.Outcome);
        Assert.Equal(ReviewRequestOutcome.AccountUnavailable, requested.Outcome);
        Assert.Equal(ReviewRequestOutcome.AccountUnavailable, cancelled.Outcome);

        await using var verificationDb = _fixture.NewDbContext();
        Assert.Equal(
            ReviewAssignmentStatus.Active,
            await verificationDb.ReviewAssignments
                .Where(item => item.Id == assignment.AssignmentId)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.False(await verificationDb.FeedbackSubmissions.AnyAsync(
            item => item.AssignmentId == assignment.AssignmentId,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            ReviewRequestStatus.Queued,
            await verificationDb.ReviewRequests
                .Where(item => item.Id == requestId)
                .Select(item => item.Status)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await verificationDb.ReviewRequests.CountAsync(
            item => item.RequesterId == requesterId,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            balanceBefore,
            await verificationDb.Users
                .Where(item => item.Id == requesterId)
                .Select(item => item.CreditBalance)
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SuspendedExistingSession_CannotPostGameCreationRequest()
    {
        var email = $"blocked-game-page-{Guid.NewGuid():N}@example.test";
        const string password = "Known-Password-123!";
        var userId = await CreateUserAsync(
            emailConfirmed: true,
            email: email,
            password: password,
            sessionVersion: 15);
        await using var factory = new WebAppFactory(_fixture.ConnectionString);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        using var pageResponse = await client.GetAsync(
            "/Games/New",
            TestContext.Current.CancellationToken);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        await SuspendDirectlyAsync(userId);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Title"] = "Must not be created",
            ["Description"] = "The session became suspended before this POST.",
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(pageHtml),
        });
        using var response = await client.PostAsync(
            "/Games/New",
            form,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Login", response.Headers.Location?.AbsolutePath);
        await using var verificationDb = _fixture.NewDbContext();
        Assert.False(await verificationDb.Games.AnyAsync(
            item => item.OwnerId == userId,
            TestContext.Current.CancellationToken));
    }

    private async Task<long> CreateUserAsync(
        bool emailConfirmed,
        string? email = null,
        string password = "Known-Password-123!",
        long sessionVersion = 1)
    {
        await using var db = _fixture.NewDbContext();
        var user = TestAccountProtection.AddUser(
            db,
            email ?? $"blocked-mutation-{Guid.NewGuid():N}@example.test",
            passwordHash: null);
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
        user.EmailConfirmedAt = emailConfirmed ? Now.AddDays(-1) : null;
        user.SessionVersion = sessionVersion;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private async Task SuspendDirectlyAsync(long userId)
    {
        await using var db = _fixture.NewDbContext();
        var user = await db.Users.SingleAsync(
            item => item.Id == userId,
            TestContext.Current.CancellationToken);
        user.AccountStatus = AccountStatus.IndefinitelySuspended;
        user.SuspendedUntil = null;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static EmailConfirmationTokenService NewEmailConfirmationTokenService(
        AppDbContext db) =>
        new(db, new FixedTimeProvider(Now));

    private static PasswordResetTokenService NewPasswordResetTokenService(
        AppDbContext db) =>
        new(db, new FixedTimeProvider(Now));

    private static EmailConfirmationResendService NewConfirmationResendService(
        AppDbContext db) =>
        new(
            db,
            TestAccountProtection.CreateService(),
            TestAccountProtection.CreateRequestIpProtectionService(),
            NewEmailConfirmationTokenService(db),
            NewOutboxService(db),
            new FixedTimeProvider(Now));

    private static PasswordResetRequestService NewPasswordResetRequestService(
        AppDbContext db) =>
        new(
            db,
            TestAccountProtection.CreateService(),
            TestAccountProtection.CreateRequestIpProtectionService(),
            NewPasswordResetTokenService(db),
            NewOutboxService(db),
            new FixedTimeProvider(Now));

    private static AuthenticationEmailOutboxService NewOutboxService(AppDbContext db) =>
        new(
            db,
            TestAccountProtection.CreateMessageProtectionService(),
            new FixedTimeProvider(Now));

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
