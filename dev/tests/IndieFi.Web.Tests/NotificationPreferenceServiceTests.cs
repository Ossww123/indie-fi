using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationPreferenceServiceTests
{
    private readonly PostgreSqlFixture _fixture;

    public NotificationPreferenceServiceTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_WithoutOverrides_ReturnsEveryPolicyDefault()
    {
        var userId = await CreateUserAsync();
        await using var db = _fixture.NewDbContext();
        var service = CreateService(db);

        var settings = await service.GetAsync(
            userId,
            TestContext.Current.CancellationToken);

        Assert.Equal(Enum.GetValues<NotificationKind>().Length, settings.Count);
        foreach (var setting in settings)
        {
            var defaults = NotificationPolicy.GetDefaults(setting.Kind);
            Assert.Equal(defaults.WebEnabled, setting.WebEnabled);
            Assert.Equal(defaults.EmailEnabled, setting.EmailEnabled);
            Assert.Equal(defaults.IsMandatory, setting.IsMandatory);
            Assert.False(setting.IsOverridden);
        }
    }

    [Fact]
    public async Task Save_StoresCompleteOverridesAndForcesMandatoryChannelsOn()
    {
        var userId = await CreateUserAsync();
        var now = new DateTimeOffset(2026, 8, 16, 9, 30, 0, TimeSpan.Zero);
        await using var db = _fixture.NewDbContext();
        var service = CreateService(db, now);
        var selections = Enum.GetValues<NotificationKind>()
            .Select(kind => new NotificationPreferenceSelection(
                kind,
                WebEnabled: false,
                EmailEnabled: false))
            .ToArray();

        var outcome = await service.SaveAsync(
            userId,
            selections,
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationPreferenceSaveOutcome.Saved, outcome);
        await using var verificationDb = _fixture.NewDbContext();
        var stored = await verificationDb.NotificationPreferences
            .Where(item => item.UserId == userId)
            .OrderBy(item => item.Kind)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Enum.GetValues<NotificationKind>().Length, stored.Count);
        Assert.All(stored, item => Assert.Equal(now, item.UpdatedAt));
        var mandatory = Assert.Single(
            stored,
            item => item.Kind == NotificationKind.AccountStatusChanged);
        Assert.True(mandatory.WebEnabled);
        Assert.True(mandatory.EmailEnabled);
        Assert.All(
            stored.Where(item => item.Kind != NotificationKind.AccountStatusChanged),
            item =>
            {
                Assert.False(item.WebEnabled);
                Assert.False(item.EmailEnabled);
            });
    }

    [Fact]
    public async Task Save_MissingOrDuplicateKind_DoesNotPartiallyChangeSettings()
    {
        var userId = await CreateUserAsync();
        await using var db = _fixture.NewDbContext();
        var service = CreateService(db);
        var invalid = Enum.GetValues<NotificationKind>()
            .Select(kind => new NotificationPreferenceSelection(kind, false, false))
            .SkipLast(1)
            .Append(new NotificationPreferenceSelection(
                NotificationKind.ReviewAssigned,
                true,
                true))
            .ToArray();

        var outcome = await service.SaveAsync(
            userId,
            invalid,
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationPreferenceSaveOutcome.InvalidSelection, outcome);
        Assert.False(await db.NotificationPreferences.AnyAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_SuspendedAccount_DoesNotChangeSettings()
    {
        var userId = await CreateUserAsync();
        await using (var setupDb = _fixture.NewDbContext())
        {
            var transitions = new AccountStatusTransitionService(
                setupDb,
                new FixedTimeProvider(DateTimeOffset.UtcNow));
            var transition = await transitions.SuspendIndefinitelyAsync(
                userId,
                TestContext.Current.CancellationToken);
            Assert.Equal(AccountStatusTransitionOutcome.Changed, transition.Outcome);
        }

        await using var db = _fixture.NewDbContext();
        var service = CreateService(db);
        var selections = Enum.GetValues<NotificationKind>()
            .Select(kind => new NotificationPreferenceSelection(kind, false, false))
            .ToArray();

        var outcome = await service.SaveAsync(
            userId,
            selections,
            TestContext.Current.CancellationToken);

        Assert.Equal(NotificationPreferenceSaveOutcome.AccountUnavailable, outcome);
        Assert.False(await db.NotificationPreferences.AnyAsync(
            item => item.UserId == userId,
            TestContext.Current.CancellationToken));
    }

    private NotificationPreferenceService CreateService(
        IndieFi.Web.Data.AppDbContext db,
        DateTimeOffset? now = null) => new(
        db,
        new FixedTimeProvider(now ?? DateTimeOffset.UtcNow));

    private async Task<long> CreateUserAsync()
    {
        await using var db = _fixture.NewDbContext();
        var user = await ReviewTestData.CreateUserWithCreditAsync(db, balance: 0);
        return user.Id;
    }

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
