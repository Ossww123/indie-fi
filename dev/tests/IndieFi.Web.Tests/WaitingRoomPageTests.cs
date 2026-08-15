using System.Collections.Concurrent;
using System.Net;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.KeyDistributions.Application;
using IndieFi.Web.Modules.KeyDistributions.Domain;
using IndieFi.Web.Modules.KeyDistributions.Infrastructure;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class WaitingRoomPageTests
{
    private readonly PostgreSqlFixture _fixture;

    public WaitingRoomPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Join_PostRedirectGet_RestoresSameWaitingState()
    {
        var (email, password) = await PageTestSupport
            .CreateUserWithKnownPasswordAsync(_fixture);
        var dropId = await CreateActiveDropAsync();
        var store = new InMemoryWaitingRoomStore();

        await using var factory = new WebAppFactory(
            _fixture.ConnectionString,
            waitingRoomStore: store);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        using var detailResponse = await client.GetAsync(
            $"/KeyDrops/Detail/{dropId}",
            TestContext.Current.CancellationToken);
        var detailHtml = await detailResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.Contains("가상 대기실", detailHtml);
        Assert.Contains("대기실 진입은 게임 키 예약이나 지급을 보장하지 않습니다.", detailHtml);
        Assert.Contains("대기실 입장", detailHtml);

        using var joinForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(detailHtml),
        });
        using var joinResponse = await client.PostAsync(
            $"/KeyDrops/Detail/{dropId}?handler=Join",
            joinForm,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, joinResponse.StatusCode);
        Assert.Contains("waiting=joined", joinResponse.Headers.Location!.OriginalString);

        using var restoredResponse = await client.GetAsync(
            joinResponse.Headers.Location,
            TestContext.Current.CancellationToken);
        var restoredHtml = WebUtility.HtmlDecode(
            await restoredResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, restoredResponse.StatusCode);
        Assert.Contains("대기실에 들어왔습니다.", restoredHtml);
        Assert.Contains("data-waiting-room-state", restoredHtml);
        Assert.Contains("대기 중", restoredHtml);
        Assert.Contains("data-waiting-room-position", restoredHtml);
        Assert.Contains("현재 1번 · 앞에 0명", restoredHtml);
        Assert.Single(store.Entries);
    }

    [Fact]
    public async Task Join_WithoutAntiforgeryToken_IsRejectedBeforeStoreWrite()
    {
        var (email, password) = await PageTestSupport
            .CreateUserWithKnownPasswordAsync(_fixture);
        var dropId = await CreateActiveDropAsync();
        var store = new InMemoryWaitingRoomStore();

        await using var factory = new WebAppFactory(
            _fixture.ConnectionString,
            waitingRoomStore: store);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        using var response = await client.PostAsync(
            $"/KeyDrops/Detail/{dropId}?handler=Join",
            new FormUrlEncodedContent([]),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task Join_WhenSlotIsAvailable_ShowsAbsoluteAdmissionDeadline()
    {
        var (email, password) = await PageTestSupport
            .CreateUserWithKnownPasswordAsync(_fixture);
        var dropId = await CreateActiveDropAsync();
        var store = new InMemoryWaitingRoomStore
        {
            AdmitOnAdvance = true,
        };

        await using var factory = new WebAppFactory(
            _fixture.ConnectionString,
            waitingRoomStore: store);
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        using var detailResponse = await client.GetAsync(
            $"/KeyDrops/Detail/{dropId}",
            TestContext.Current.CancellationToken);
        var detailHtml = await detailResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var joinForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(detailHtml),
        });

        using var joinResponse = await client.PostAsync(
            $"/KeyDrops/Detail/{dropId}?handler=Join",
            joinForm,
            TestContext.Current.CancellationToken);
        using var restoredResponse = await client.GetAsync(
            joinResponse.Headers.Location,
            TestContext.Current.CancellationToken);
        var restoredHtml = await restoredResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, restoredResponse.StatusCode);
        Assert.Contains("입장 가능", restoredHtml);
        Assert.Contains("data-admission-expires-at", restoredHtml);
        Assert.Contains("재접속해도 입장 가능 시간은 연장되지 않으며", restoredHtml);
        Assert.Contains("이 상태만으로 키가 예약되지는 않습니다", restoredHtml);
        Assert.Single(store.Entries);
        Assert.Equal(WaitingRoomEntryState.Admitted, store.Entries.Single().Value.State);
    }

    [Fact]
    public async Task Get_WhenRedisIsUnavailable_ShowsFixedFailureWithoutLeakingDetails()
    {
        var (email, password) = await PageTestSupport
            .CreateUserWithKnownPasswordAsync(_fixture);
        var dropId = await CreateActiveDropAsync();

        await using var factory = new WebAppFactory(
            _fixture.ConnectionString,
            waitingRoomStore: new UnavailableWaitingRoomStore());
        using var client = factory.CreateClient();
        await PageTestSupport.LoginAsync(client, email, password);
        using var response = await client.GetAsync(
            $"/KeyDrops/Detail/{dropId}",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("현재 대기실에 연결할 수 없습니다.", html);
        Assert.DoesNotContain("Redis", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localhost", html, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> CreateActiveDropAsync()
    {
        await using var db = _fixture.NewDbContext();
        var owner = TestAccountProtection.AddUser(
            db,
            $"{Guid.NewGuid():N}@example.test");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var game = await ReviewTestData.CreateGameAsync(db, owner.Id);
        var drop = new KeyDrop
        {
            GameId = game.Id,
            OwnerId = owner.Id,
            OpensAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            TotalKeys = 1,
        };
        db.KeyDrops.Add(drop);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        TestAccountProtection.AddGameKey(
            db,
            drop,
            $"WAITING-PAGE-{Guid.NewGuid():N}");
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return drop.Id;
    }

    private sealed class InMemoryWaitingRoomStore : IWaitingRoomStore
    {
        public ConcurrentDictionary<(long DropId, long UserId), WaitingRoomStoredEntry>
            Entries
        { get; } = new();

        public bool AdmitOnAdvance { get; init; }

        public Task<WaitingRoomJoinResult> JoinAsync(
            WaitingRoomJoinCommand command,
            CancellationToken cancellationToken = default)
        {
            var state = command.Mode == WaitingRoomJoinMode.PreOpen
                ? WaitingRoomEntryState.PreWaiting
                : WaitingRoomEntryState.Waiting;
            var created = new WaitingRoomStoredEntry(state, 1, 1000);
            var actual = Entries.GetOrAdd((command.DropId, command.UserId), created);
            return Task.FromResult(new WaitingRoomJoinResult(
                ReferenceEquals(created, actual)
                    ? WaitingRoomJoinOutcome.Created
                    : WaitingRoomJoinOutcome.Existing,
                actual));
        }

        public Task<WaitingRoomStoredEntry?> GetEntryAsync(
            long dropId,
            long userId,
            CancellationToken cancellationToken = default)
        {
            Entries.TryGetValue((dropId, userId), out var entry);
            return Task.FromResult(entry);
        }

        public Task<WaitingRoomSnapshot?> GetSnapshotAsync(
            long dropId,
            long userId,
            CancellationToken cancellationToken = default)
        {
            Entries.TryGetValue((dropId, userId), out var entry);
            return Task.FromResult(entry is null
                ? null
                : new WaitingRoomSnapshot(
                    entry,
                    Position: entry.State == WaitingRoomEntryState.Waiting
                        ? checked((int)entry.Order)
                        : null,
                    AheadCount: entry.State == WaitingRoomEntryState.Waiting
                        ? checked((int)entry.Order - 1)
                        : null,
                    WaitingCount: Entries.Count(item =>
                        item.Key.DropId == dropId
                        && item.Value.State == WaitingRoomEntryState.Waiting),
                    PreWaitingCount: 0,
                    AdmittedCount: Entries.Count(item =>
                        item.Key.DropId == dropId
                        && item.Value.State == WaitingRoomEntryState.Admitted),
                    OrderFinalized: true));
        }

        public Task<WaitingRoomTransitionResult> TransitionAsync(
            WaitingRoomTransitionCommand command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WaitingRoomOrderFinalizationResult> FinalizeOrderAsync(
            long dropId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WaitingRoomOrderFinalizationResult(
                WaitingRoomOrderFinalizationOutcome.AlreadyFinalized,
                Entries.Count));

        public Task<WaitingRoomAdmissionAdvanceResult> AdvanceAdmissionsAsync(
            long dropId,
            CancellationToken cancellationToken = default)
        {
            var promoted = new List<long>();
            if (AdmitOnAdvance)
            {
                foreach (var item in Entries.Where(item =>
                             item.Key.DropId == dropId
                             && item.Value.State == WaitingRoomEntryState.Waiting))
                {
                    Entries[item.Key] = item.Value with
                    {
                        State = WaitingRoomEntryState.Admitted,
                        AdmissionExpiresUnixTimeMilliseconds = DateTimeOffset.UtcNow
                            .AddMinutes(2)
                            .ToUnixTimeMilliseconds(),
                    };
                    promoted.Add(item.Key.UserId);
                }
            }

            return Task.FromResult(new WaitingRoomAdmissionAdvanceResult(
                WaitingRoomAdmissionAdvanceOutcome.Advanced,
                promoted,
                [],
                Entries.Count(item =>
                    item.Key.DropId == dropId
                    && item.Value.State == WaitingRoomEntryState.Admitted)));
        }

        public Task<WaitingRoomAdmissionValidationResult> ValidateAdmissionAsync(
            long dropId,
            long userId,
            CancellationToken cancellationToken = default)
        {
            Entries.TryGetValue((dropId, userId), out var entry);
            return Task.FromResult(new WaitingRoomAdmissionValidationResult(
                entry?.State == WaitingRoomEntryState.Admitted
                    ? WaitingRoomAdmissionValidationOutcome.Valid
                    : entry is null
                        ? WaitingRoomAdmissionValidationOutcome.Missing
                        : WaitingRoomAdmissionValidationOutcome.NotAdmitted,
                entry));
        }

        public Task<WaitingRoomCompletionResult> CompleteAsync(
            long dropId,
            long userId,
            CancellationToken cancellationToken = default)
        {
            if (!Entries.TryGetValue((dropId, userId), out var entry))
            {
                return Task.FromResult(new WaitingRoomCompletionResult(
                    WaitingRoomCompletionOutcome.Missing));
            }

            var outcome = entry.State == WaitingRoomEntryState.Completed
                ? WaitingRoomCompletionOutcome.AlreadyCompleted
                : WaitingRoomCompletionOutcome.Completed;
            var completed = entry with
            {
                State = WaitingRoomEntryState.Completed,
                AdmissionExpiresUnixTimeMilliseconds = null,
            };
            Entries[(dropId, userId)] = completed;
            return Task.FromResult(new WaitingRoomCompletionResult(outcome, completed));
        }
    }

    private sealed class UnavailableWaitingRoomStore : IWaitingRoomStore
    {
        public Task<WaitingRoomJoinResult> JoinAsync(
            WaitingRoomJoinCommand command,
            CancellationToken cancellationToken = default) =>
            throw new WaitingRoomUnavailableException("test unavailable");

        public Task<WaitingRoomStoredEntry?> GetEntryAsync(
            long dropId,
            long userId,
            CancellationToken cancellationToken = default) =>
            throw new WaitingRoomUnavailableException("test unavailable");

        public Task<WaitingRoomSnapshot?> GetSnapshotAsync(
            long dropId,
            long userId,
            CancellationToken cancellationToken = default) =>
            throw new WaitingRoomUnavailableException("test unavailable");

        public Task<WaitingRoomTransitionResult> TransitionAsync(
            WaitingRoomTransitionCommand command,
            CancellationToken cancellationToken = default) =>
            throw new WaitingRoomUnavailableException("test unavailable");

        public Task<WaitingRoomOrderFinalizationResult> FinalizeOrderAsync(
            long dropId,
            CancellationToken cancellationToken = default) =>
            throw new WaitingRoomUnavailableException("test unavailable");

        public Task<WaitingRoomAdmissionAdvanceResult> AdvanceAdmissionsAsync(
            long dropId,
            CancellationToken cancellationToken = default) =>
            throw new WaitingRoomUnavailableException("test unavailable");

        public Task<WaitingRoomAdmissionValidationResult> ValidateAdmissionAsync(
            long dropId,
            long userId,
            CancellationToken cancellationToken = default) =>
            throw new WaitingRoomUnavailableException("test unavailable");

        public Task<WaitingRoomCompletionResult> CompleteAsync(
            long dropId,
            long userId,
            CancellationToken cancellationToken = default) =>
            throw new WaitingRoomUnavailableException("test unavailable");
    }
}
