using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using IndieFi.Web.Entities;
using IndieFi.Web.Modules.Media;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(MediaHttpCollection.Name)]
public sealed class MediaHttpIntegrationTests
{
    private readonly PostgreSqlFixture _postgresql;
    private readonly RedisFixture _redis;
    private readonly MinioFixture _minio;

    public MediaHttpIntegrationTests(
        PostgreSqlFixture postgresql,
        RedisFixture redis,
        MinioFixture minio)
    {
        _postgresql = postgresql;
        _redis = redis;
        _minio = minio;
    }

    [Fact]
    public async Task OwnerFlow_ReissuesCompletesReadsAndRemovesAcrossRealBoundaries()
    {
        var owner = await CreateAccountWithGameAsync();
        var other = await CreateAccountWithGameAsync();
        var prefix = Prefix();
        await using var baseFactory = new WebAppFactory(_postgresql.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            ConfigureMediaHost(builder, prefix));
        using var ownerClient = CreateClient(factory);
        using var otherClient = CreateClient(factory);
        await PageTestSupport.LoginAsync(ownerClient, owner.Email, owner.Password);
        await PageTestSupport.LoginAsync(otherClient, other.Email, other.Password);
        var antiforgery = await GetAntiforgeryAsync(ownerClient);
        byte[] payload = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

        using var reservationResponse = await SendJsonAsync(
            ownerClient,
            HttpMethod.Post,
            "/api/media/reservations",
            new MediaReservationRequest(
                MediaTargetKind.Game.ToString(),
                owner.GameId,
                MediaAttachmentKind.GameScreenshot.ToString(),
                "image/png",
                payload.LongLength),
            antiforgery);
        Assert.Equal(HttpStatusCode.Created, reservationResponse.StatusCode);
        AssertNoStore(reservationResponse);
        var reservation = await ReadRequiredAsync<MediaReservationResponse>(
            reservationResponse);
        Assert.Equal(MediaStatus.UploadPending.ToString(), reservation.Status);

        var firstAttempt = await IssueUploadAttemptAsync(
            ownerClient,
            reservation.MediaId,
            antiforgery);
        var secondAttempt = await IssueUploadAttemptAsync(
            ownerClient,
            reservation.MediaId,
            antiforgery);
        Assert.NotEqual(firstAttempt.UploadAttemptId, secondAttempt.UploadAttemptId);
        Assert.NotEqual(firstAttempt.UploadUrl, secondAttempt.UploadUrl);

        using var objectClient = new HttpClient();
        using (var firstUpload = await PutAsync(
            objectClient,
            firstAttempt.UploadUrl,
            payload,
            firstAttempt.RequiredContentType))
        {
            Assert.Equal(HttpStatusCode.OK, firstUpload.StatusCode);
        }

        using (var supersededCompletion = await CompleteAsync(
            ownerClient,
            reservation.MediaId,
            firstAttempt.UploadAttemptId,
            antiforgery))
        {
            Assert.Equal(HttpStatusCode.Conflict, supersededCompletion.StatusCode);
            Assert.Equal(
                "upload_attempt_superseded",
                (await ReadRequiredAsync<MediaApiError>(supersededCompletion)).Code);
        }

        using (var secondUpload = await PutAsync(
            objectClient,
            secondAttempt.UploadUrl,
            payload,
            secondAttempt.RequiredContentType))
        {
            Assert.Equal(HttpStatusCode.OK, secondUpload.StatusCode);
        }

        MediaProcessingAcceptedResponse completed;
        using (var completion = await CompleteAsync(
            ownerClient,
            reservation.MediaId,
            secondAttempt.UploadAttemptId,
            antiforgery))
        {
            Assert.Equal(HttpStatusCode.Accepted, completion.StatusCode);
            completed = await ReadRequiredAsync<MediaProcessingAcceptedResponse>(
                completion);
        }

        using (var repeatedCompletion = await CompleteAsync(
            ownerClient,
            reservation.MediaId,
            secondAttempt.UploadAttemptId,
            antiforgery))
        {
            Assert.Equal(HttpStatusCode.Accepted, repeatedCompletion.StatusCode);
            Assert.Equal(
                completed,
                await ReadRequiredAsync<MediaProcessingAcceptedResponse>(
                    repeatedCompletion));
        }

        using (var statusResponse = await ownerClient.GetAsync(
            $"/api/media/{reservation.MediaId}",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            AssertNoStore(statusResponse);
            var status = await ReadRequiredAsync<MediaStatusResponse>(statusResponse);
            Assert.Equal(MediaStatus.ProcessingPending.ToString(), status.Status);
            Assert.False(status.IsCurrent);
            Assert.Null(status.PublishedAt);
        }

        using (var foreignStatus = await otherClient.GetAsync(
            $"/api/media/{reservation.MediaId}",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignStatus.StatusCode);
        }

        MediaDeletionAcceptedResponse removed;
        using (var removal = await DeleteAsync(
            ownerClient,
            reservation.MediaId,
            antiforgery))
        {
            Assert.Equal(HttpStatusCode.Accepted, removal.StatusCode);
            removed = await ReadRequiredAsync<MediaDeletionAcceptedResponse>(removal);
        }

        using (var repeatedRemoval = await DeleteAsync(
            ownerClient,
            reservation.MediaId,
            antiforgery))
        {
            Assert.Equal(HttpStatusCode.Accepted, repeatedRemoval.StatusCode);
            Assert.Equal(
                removed,
                await ReadRequiredAsync<MediaDeletionAcceptedResponse>(repeatedRemoval));
        }

        using (var removedStatusResponse = await ownerClient.GetAsync(
            $"/api/media/{reservation.MediaId}",
            TestContext.Current.CancellationToken))
        {
            var removedStatus = await ReadRequiredAsync<MediaStatusResponse>(
                removedStatusResponse);
            Assert.Equal(MediaStatus.DeletePending.ToString(), removedStatus.Status);
            Assert.False(removedStatus.IsCurrent);
            Assert.Null(removedStatus.SortOrder);
        }

        var storage = factory.Services.GetRequiredService<IMediaObjectStorage>();
        Assert.NotNull(await storage.GetMetadataAsync(
            MediaObjectKey.Source(reservation.MediaId, revision: 1),
            TestContext.Current.CancellationToken));

        await using var verificationDb = _postgresql.NewDbContext();
        var attempts = await verificationDb.MediaUploadAttempts
            .AsNoTracking()
            .Where(item => item.MediaId == reservation.MediaId)
            .OrderBy(item => item.IssuedAt)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, attempts.Length);
        Assert.NotNull(attempts[0].SupersededAt);
        Assert.Null(attempts[0].CompletedAt);
        Assert.NotNull(attempts[1].CompletedAt);
    }

    [Fact]
    public async Task MutationEndpoints_RequireAuthenticationAntiforgeryAndOwnership()
    {
        var owner = await CreateAccountWithGameAsync();
        var foreign = await CreateAccountWithGameAsync();
        await using var baseFactory = new WebAppFactory(_postgresql.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            ConfigureMediaHost(builder, Prefix()));
        using var anonymousClient = CreateClient(factory);
        using var ownerClient = CreateClient(factory);

        using (var anonymous = await anonymousClient.GetAsync(
            $"/api/media/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            AssertNoStore(anonymous);
            Assert.Equal(
                "authentication_required",
                (await ReadRequiredAsync<MediaApiError>(anonymous)).Code);
        }

        await PageTestSupport.LoginAsync(ownerClient, owner.Email, owner.Password);
        var reservationRequest = new MediaReservationRequest(
            MediaTargetKind.Game.ToString(),
            owner.GameId,
            MediaAttachmentKind.GameCover.ToString(),
            "image/png",
            1024);
        using (var missingAntiforgery = await ownerClient.PostAsJsonAsync(
            "/api/media/reservations",
            reservationRequest,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
            Assert.Equal(
                "antiforgery_validation_failed",
                (await ReadRequiredAsync<MediaApiError>(missingAntiforgery)).Code);
        }

        var antiforgery = await GetAntiforgeryAsync(ownerClient);
        using (var emptyIdentifier = await ownerClient.GetAsync(
            $"/api/media/{Guid.Empty}",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, emptyIdentifier.StatusCode);
        }

        using (var foreignTarget = await SendJsonAsync(
            ownerClient,
            HttpMethod.Post,
            "/api/media/reservations",
            reservationRequest with { TargetId = foreign.GameId },
            antiforgery))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignTarget.StatusCode);
            Assert.Equal(
                "media_not_found",
                (await ReadRequiredAsync<MediaApiError>(foreignTarget)).Code);
        }

        using (var incompatibleTarget = await SendJsonAsync(
            ownerClient,
            HttpMethod.Post,
            "/api/media/reservations",
            reservationRequest with
            {
                TargetKind = MediaTargetKind.Profile.ToString(),
            },
            antiforgery))
        {
            Assert.Equal(
                HttpStatusCode.UnprocessableEntity,
                incompatibleTarget.StatusCode);
        }

        using (var numericEnum = await SendJsonAsync(
            ownerClient,
            HttpMethod.Post,
            "/api/media/reservations",
            reservationRequest with { TargetKind = "0" },
            antiforgery))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, numericEnum.StatusCode);
        }

        await using var verificationDb = _postgresql.NewDbContext();
        Assert.False(await verificationDb.MediaAssets.AnyAsync(
            item => item.OwnerUserId == owner.UserId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScreenshotOrderEndpoint_RequiresCsrfOwnershipAndTheCompleteSet()
    {
        var owner = await CreateAccountWithGameAsync();
        var other = await CreateAccountWithGameAsync();
        var screenshots = await AddReadyScreenshotsAsync(owner, count: 3);
        await using var baseFactory = new WebAppFactory(_postgresql.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            ConfigureMediaHost(builder, Prefix()));
        using var ownerClient = CreateClient(factory);
        using var otherClient = CreateClient(factory);
        await PageTestSupport.LoginAsync(ownerClient, owner.Email, owner.Password);
        await PageTestSupport.LoginAsync(otherClient, other.Email, other.Password);
        var request = new GameScreenshotOrderRequest(
            [screenshots[2], screenshots[0], screenshots[1]]);

        using (var missingAntiforgery = await ownerClient.PutAsJsonAsync(
            $"/api/media/games/{owner.GameId}/screenshots/order",
            request,
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
            Assert.Equal(
                "antiforgery_validation_failed",
                (await ReadRequiredAsync<MediaApiError>(missingAntiforgery)).Code);
        }

        var otherAntiforgery = await GetAntiforgeryAsync(otherClient);
        using (var foreign = await SendJsonAsync(
            otherClient,
            HttpMethod.Put,
            $"/api/media/games/{owner.GameId}/screenshots/order",
            request,
            otherAntiforgery))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        var ownerAntiforgery = await GetAntiforgeryAsync(ownerClient);
        using (var incomplete = await SendJsonAsync(
            ownerClient,
            HttpMethod.Put,
            $"/api/media/games/{owner.GameId}/screenshots/order",
            new GameScreenshotOrderRequest([screenshots[0]]),
            ownerAntiforgery))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, incomplete.StatusCode);
            Assert.Equal(
                "invalid_screenshot_order",
                (await ReadRequiredAsync<MediaApiError>(incomplete)).Code);
        }

        using var reordered = await SendJsonAsync(
            ownerClient,
            HttpMethod.Put,
            $"/api/media/games/{owner.GameId}/screenshots/order",
            request,
            ownerAntiforgery);
        Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);
        AssertNoStore(reordered);
        var response = await ReadRequiredAsync<GameScreenshotOrderResponse>(
            reordered);
        Assert.Equal(owner.GameId, response.GameId);
        Assert.Equal(request.MediaIds, response.MediaIds);
    }

    [Fact]
    public async Task GameMediaPage_RendersOwnerStateAndHidesForeignGames()
    {
        var owner = await CreateAccountWithGameAsync();
        var other = await CreateAccountWithGameAsync();
        var media = await AddOwnerPageMediaAsync(owner);
        await using var baseFactory = new WebAppFactory(_postgresql.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            ConfigureMediaHost(builder, Prefix()));
        using var anonymousClient = CreateClient(factory);
        using var ownerClient = CreateClient(factory);
        using var otherClient = CreateClient(factory);
        await PageTestSupport.LoginAsync(ownerClient, owner.Email, owner.Password);
        await PageTestSupport.LoginAsync(otherClient, other.Email, other.Password);

        using (var anonymous = await anonymousClient.GetAsync(
            $"/Games/Media/{owner.GameId}",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.StartsWith("/Login", anonymous.Headers.Location?.PathAndQuery);
        }

        using (var foreign = await otherClient.GetAsync(
            $"/Games/Media/{owner.GameId}",
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        using var response = await ownerClient.GetAsync(
            $"/Games/Media/{owner.GameId}",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-game-media", html);
        Assert.Contains($"data-game-id=\"{owner.GameId}\"", html);
        Assert.Contains("data-max-screenshots=\"8\"", html);
        Assert.Contains("data-max-file-bytes=\"10485760\"", html);
        Assert.Contains("accept=\"image/jpeg,image/png,image/webp\"", html);
        Assert.Contains("data-attachment-kind=\"GameCover\"", html);
        Assert.Contains("data-attachment-kind=\"GameScreenshot\"", html);
        Assert.Contains("data-media-upload-cancel", html);
        Assert.Contains("data-media-upload-mode=\"replace\"", html);
        Assert.Contains("data-media-action=\"remove\"", html);
        Assert.Contains("data-media-action=\"up\"", html);
        Assert.Contains("data-media-action=\"down\"", html);
        Assert.Contains($"data-media-id=\"{media.CoverId}\"", html);
        Assert.Contains($"data-media-id=\"{media.ReadyScreenshotId}\"", html);
        Assert.Contains($"data-media-id=\"{media.PendingId}\"", html);
        Assert.Contains($"data-media-id=\"{media.FailedId}\"", html);
        Assert.Contains("처리 대기", html);
        Assert.Contains("image_invalid_format", html);
        Assert.Contains("data-media-preview", html);
        Assert.Contains($"href=\"/Games/Detail/{owner.GameId}\"", html);
        Assert.Contains("/js/dist/games/media", html);

        using var moduleResponse = await ownerClient.GetAsync(
            "/js/dist/games/media.js",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, moduleResponse.StatusCode);

        using var detailResponse = await ownerClient.GetAsync(
            $"/Games/Detail/{owner.GameId}",
            TestContext.Current.CancellationToken);
        var detailHtml = WebUtility.HtmlDecode(
            await detailResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.Contains($"href=\"/Games/Media/{owner.GameId}\"", detailHtml);
        Assert.Contains($"data-media-id=\"{media.CoverId}\"", detailHtml);
        Assert.Contains(
            $"data-media-id=\"{media.ReadyScreenshotId}\"",
            detailHtml);
        Assert.DoesNotContain(media.PendingId.ToString(), detailHtml);
        Assert.DoesNotContain(media.FailedId.ToString(), detailHtml);
        Assert.Contains("data-game-cover", detailHtml);
        Assert.Contains("data-game-screenshot", detailHtml);
        Assert.Contains("loading=\"lazy\"", detailHtml);

        using var publicDetailResponse = await anonymousClient.GetAsync(
            $"/Games/Detail/{owner.GameId}",
            TestContext.Current.CancellationToken);
        var publicDetailHtml = WebUtility.HtmlDecode(
            await publicDetailResponse.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, publicDetailResponse.StatusCode);
        Assert.Contains($"data-media-id=\"{media.CoverId}\"", publicDetailHtml);
        Assert.Contains(
            $"data-media-id=\"{media.ReadyScreenshotId}\"",
            publicDetailHtml);
    }

    [Fact]
    public async Task ConcurrentScreenshotReservations_CannotCreateNinthAttachment()
    {
        var owner = await CreateAccountWithGameAsync();
        await AddPendingScreenshotsAsync(owner, count: 7);
        await using var baseFactory = new WebAppFactory(_postgresql.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            ConfigureMediaHost(builder, Prefix()));
        using var client = CreateClient(factory);
        await PageTestSupport.LoginAsync(client, owner.Email, owner.Password);
        var antiforgery = await GetAntiforgeryAsync(client);
        var request = new MediaReservationRequest(
            MediaTargetKind.Game.ToString(),
            owner.GameId,
            MediaAttachmentKind.GameScreenshot.ToString(),
            "image/png",
            1024);

        var responses = await Task.WhenAll(
            SendJsonAsync(
                client,
                HttpMethod.Post,
                "/api/media/reservations",
                request,
                antiforgery),
            SendJsonAsync(
                client,
                HttpMethod.Post,
                "/api/media/reservations",
                request,
                antiforgery));
        try
        {
            Assert.Single(
                responses,
                response => response.StatusCode == HttpStatusCode.Created);
            var rejected = Assert.Single(
                responses,
                response => response.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal(
                "attachment_limit_reached",
                (await ReadRequiredAsync<MediaApiError>(rejected)).Code);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using var verificationDb = _postgresql.NewDbContext();
        Assert.Equal(
            MediaPolicy.GameScreenshotMaximumCount,
            await verificationDb.MediaAssets.CountAsync(
                item => item.GameId == owner.GameId
                    && item.AttachmentKind == MediaAttachmentKind.GameScreenshot
                    && item.Status == MediaStatus.UploadPending,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentUploadUrlIssue_RealRedisAllowsExactlyTwenty()
    {
        var owner = await CreateAccountWithGameAsync();
        var prefix = Prefix();
        await using var baseFactory = new WebAppFactory(_postgresql.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            ConfigureMediaHost(builder, prefix));
        using var client = CreateClient(factory);
        await PageTestSupport.LoginAsync(client, owner.Email, owner.Password);
        var antiforgery = await GetAntiforgeryAsync(client);
        using var reservationResponse = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/media/reservations",
            new MediaReservationRequest(
                MediaTargetKind.Game.ToString(),
                owner.GameId,
                MediaAttachmentKind.GameCover.ToString(),
                "image/png",
                1024),
            antiforgery);
        var reservation = await ReadRequiredAsync<MediaReservationResponse>(
            reservationResponse);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, MediaPolicy.IssueLimitPerShortWindow + 1)
                .Select(_ => SendJsonAsync(
                    client,
                    HttpMethod.Post,
                    $"/api/media/{reservation.MediaId}/upload-attempts",
                    body: null,
                    antiforgery)));
        try
        {
            Assert.Equal(
                MediaPolicy.IssueLimitPerShortWindow,
                responses.Count(response =>
                    response.StatusCode == HttpStatusCode.Created));
            var rejected = Assert.Single(
                responses,
                response => response.StatusCode == HttpStatusCode.TooManyRequests);
            Assert.Equal(
                "upload_short_window_limit_reached",
                (await ReadRequiredAsync<MediaApiError>(rejected)).Code);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using var verificationDb = _postgresql.NewDbContext();
        Assert.Equal(
            MediaPolicy.IssueLimitPerShortWindow,
            await verificationDb.MediaUploadAttempts.CountAsync(
                item => item.MediaId == reservation.MediaId,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            1,
            await verificationDb.MediaUploadAttempts.CountAsync(
                item => item.MediaId == reservation.MediaId
                    && item.SupersededAt == null,
                TestContext.Current.CancellationToken));
        Assert.Equal(
            MediaPolicy.IssueLimitPerShortWindow,
            await (await _redis.GetDatabaseAsync()).SortedSetLengthAsync(
                $"{prefix}:{{account:{owner.UserId}}}:issues"));
    }

    private void ConfigureMediaHost(IWebHostBuilder builder, string rateLimitPrefix)
    {
        builder.UseSetting("WaitingRoom:Redis:Enabled", "true");
        builder.UseSetting("WaitingRoom:Redis:ConnectionString", _redis.ConnectionString);
        builder.UseSetting(
            "WaitingRoom:Redis:KeyPrefix",
            $"test:waiting-room:{Guid.NewGuid():N}");
        builder.UseSetting("WaitingRoom:Redis:ConnectTimeout", "00:00:05");
        builder.UseSetting("Media:UploadRateLimit:KeyPrefix", rateLimitPrefix);
        builder.UseSetting("Media:ObjectStorage:Enabled", "true");
        builder.UseSetting("Media:ObjectStorage:ServiceUrl", _minio.ServiceUrl);
        builder.UseSetting("Media:ObjectStorage:Region", "us-east-1");
        builder.UseSetting("Media:ObjectStorage:BucketName", MinioFixture.BucketName);
        builder.UseSetting("Media:ObjectStorage:AccessKeyId", MinioFixture.AppAccessKey);
        builder.UseSetting("Media:ObjectStorage:SecretAccessKey", MinioFixture.AppSecretKey);
        builder.UseSetting("Media:ObjectStorage:OperationTimeout", "00:00:10");
        builder.UseSetting("Media:ObjectStorage:ForcePathStyle", "true");
    }

    private async Task<TestAccount> CreateAccountWithGameAsync()
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _postgresql);
        await using var db = _postgresql.NewDbContext();
        var user = await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken);
        var game = new Game
        {
            OwnerId = user.Id,
            Title = $"Media HTTP game {Guid.NewGuid():N}",
        };
        db.Games.Add(game);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new TestAccount(user.Id, game.Id, email, password);
    }

    private async Task AddPendingScreenshotsAsync(TestAccount owner, int count)
    {
        await using var db = _postgresql.NewDbContext();
        db.MediaAssets.AddRange(Enumerable.Range(0, count).Select(_ =>
            new MediaAsset
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner.UserId,
                GameId = owner.GameId,
                AttachmentKind = MediaAttachmentKind.GameScreenshot,
                DeclaredContentType = "image/png",
                DeclaredBytes = 1024,
            }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> AddReadyScreenshotsAsync(
        TestAccount owner,
        int count)
    {
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        var screenshots = Enumerable.Range(0, count).Select(index =>
            new MediaAsset
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner.UserId,
                GameId = owner.GameId,
                AttachmentKind = MediaAttachmentKind.GameScreenshot,
                Status = MediaStatus.Ready,
                DeclaredContentType = "image/png",
                DeclaredBytes = 1024,
                SourceContentType = "image/png",
                SourceBytes = 1024,
                SourceETag = $"\"{Guid.NewGuid():N}\"",
                DetectedContentType = "image/png",
                PixelWidth = 80,
                PixelHeight = 45,
                FrameCount = 1,
                IsCurrent = true,
                SortOrder = index,
                PublishedAt = now.AddMinutes(-5),
                CreatedAt = now.AddHours(-1),
                UpdatedAt = now.AddMinutes(-5),
                StatusChangedAt = now.AddMinutes(-5),
                UploadExpiresAt = now.AddHours(23),
            }).ToArray();
        await using var db = _postgresql.NewDbContext();
        db.MediaAssets.AddRange(screenshots);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return screenshots.Select(item => item.Id).ToArray();
    }

    private async Task<OwnerPageMedia> AddOwnerPageMediaAsync(TestAccount owner)
    {
        var now = DateTimeOffset.UtcNow;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
        var cover = new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.UserId,
            GameId = owner.GameId,
            AttachmentKind = MediaAttachmentKind.GameCover,
            Status = MediaStatus.Ready,
            DeclaredContentType = "image/png",
            DeclaredBytes = 1024,
            SourceContentType = "image/png",
            SourceBytes = 1024,
            SourceETag = $"\"{Guid.NewGuid():N}\"",
            DetectedContentType = "image/png",
            PixelWidth = 80,
            PixelHeight = 45,
            FrameCount = 1,
            IsCurrent = true,
            PublishedAt = now.AddMinutes(-5),
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now.AddMinutes(-5),
            StatusChangedAt = now.AddMinutes(-5),
            UploadExpiresAt = now.AddHours(23),
        };
        var pending = new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.UserId,
            GameId = owner.GameId,
            AttachmentKind = MediaAttachmentKind.GameScreenshot,
            Status = MediaStatus.ProcessingPending,
            DeclaredContentType = "image/webp",
            DeclaredBytes = 2048,
            SourceContentType = "image/webp",
            SourceBytes = 2048,
            SourceETag = $"\"{Guid.NewGuid():N}\"",
            CreatedAt = now.AddMinutes(-4),
            UpdatedAt = now.AddMinutes(-3),
            StatusChangedAt = now.AddMinutes(-3),
            UploadExpiresAt = now.AddHours(23).AddMinutes(56),
        };
        var readyScreenshot = new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.UserId,
            GameId = owner.GameId,
            AttachmentKind = MediaAttachmentKind.GameScreenshot,
            Status = MediaStatus.Ready,
            DeclaredContentType = "image/webp",
            DeclaredBytes = 3072,
            SourceContentType = "image/webp",
            SourceBytes = 3072,
            SourceETag = $"\"{Guid.NewGuid():N}\"",
            DetectedContentType = "image/webp",
            PixelWidth = 160,
            PixelHeight = 90,
            FrameCount = 1,
            IsCurrent = true,
            SortOrder = 0,
            PublishedAt = now.AddMinutes(-5),
            CreatedAt = now.AddMinutes(-6),
            UpdatedAt = now.AddMinutes(-5),
            StatusChangedAt = now.AddMinutes(-5),
            UploadExpiresAt = now.AddHours(23).AddMinutes(54),
        };
        var failed = new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.UserId,
            GameId = owner.GameId,
            AttachmentKind = MediaAttachmentKind.GameScreenshot,
            Status = MediaStatus.Failed,
            DeclaredContentType = "image/jpeg",
            DeclaredBytes = 4096,
            FailureCode = "image_invalid_format",
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-1),
            StatusChangedAt = now.AddMinutes(-1),
            UploadExpiresAt = now.AddHours(23).AddMinutes(58),
        };
        await using var db = _postgresql.NewDbContext();
        db.MediaAssets.AddRange(cover, readyScreenshot, pending, failed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new OwnerPageMedia(
            cover.Id,
            readyScreenshot.Id,
            pending.Id,
            failed.Id);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    private static async Task<MediaAntiforgeryResponse> GetAntiforgeryAsync(
        HttpClient client)
    {
        using var response = await client.GetAsync(
            "/api/media/antiforgery",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadRequiredAsync<MediaAntiforgeryResponse>(response);
    }

    private static async Task<MediaUploadAttemptResponse> IssueUploadAttemptAsync(
        HttpClient client,
        Guid mediaId,
        MediaAntiforgeryResponse antiforgery)
    {
        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/media/{mediaId}/upload-attempts",
            body: null,
            antiforgery);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertNoStore(response);
        return await ReadRequiredAsync<MediaUploadAttemptResponse>(response);
    }

    private static Task<HttpResponseMessage> CompleteAsync(
        HttpClient client,
        Guid mediaId,
        Guid uploadAttemptId,
        MediaAntiforgeryResponse antiforgery) => SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/media/{mediaId}/upload-attempts/{uploadAttemptId}/complete",
            body: null,
            antiforgery);

    private static Task<HttpResponseMessage> DeleteAsync(
        HttpClient client,
        Guid mediaId,
        MediaAntiforgeryResponse antiforgery) => SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"/api/media/{mediaId}",
            body: null,
            antiforgery);

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body,
        MediaAntiforgeryResponse antiforgery)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(
            antiforgery.HeaderName,
            antiforgery.RequestToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        Uri requestUri,
        byte[] content,
        string contentType)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
        {
            Content = new ByteArrayContent(content),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Content.Headers.ContentLength = content.LongLength;
        try
        {
            return await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(
            TestContext.Current.CancellationToken);
        return value ?? throw new InvalidOperationException(
            $"The response did not contain {typeof(T).Name} JSON.");
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.Select(item => item.Name));
        Assert.Equal(
            "no-referrer",
            Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    private static string Prefix() =>
        $"test:media-http:{Guid.NewGuid():N}";

    private sealed record TestAccount(
        long UserId,
        long GameId,
        string Email,
        string Password);

    private sealed record OwnerPageMedia(
        Guid CoverId,
        Guid ReadyScreenshotId,
        Guid PendingId,
        Guid FailedId);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MediaHttpCollection :
    ICollectionFixture<PostgreSqlFixture>,
    ICollectionFixture<RedisFixture>,
    ICollectionFixture<MinioFixture>
{
    public const string Name = "Media HTTP";
}
