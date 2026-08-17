using System.Net;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using IndieFi.Web.Modules.Profiles.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ProfileMediaManagementPageTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture _fixture;

    public ProfileMediaManagementPageTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_WithoutImage_ShowsDefaultCreateUploader()
    {
        var account = await CreateAccountAsync(createProfile: false);
        await using var baseFactory = new WebAppFactory(_fixture.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(ConfigureMediaHost);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        await PageTestSupport.LoginAsync(client, account.Email, account.Password);

        using var response = await client.GetAsync(
            "/Account/Profile",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("data-profile-media", html);
        Assert.Contains($"data-profile-user-id=\"{account.UserId}\"", html);
        Assert.Contains("data-max-file-bytes=\"5242880\"", html);
        Assert.Contains("data-attachment-kind=\"ProfileImage\"", html);
        Assert.Contains("data-media-upload-mode=\"create\"", html);
        Assert.Contains("accept=\"image/jpeg,image/png,image/webp\"", html);
        Assert.Contains("data-default-profile-image", html);
        Assert.Contains("기본 프로필 이미지", html);
        Assert.Contains("등록된 프로필 이미지가 없습니다.", html);
    }

    [Fact]
    public async Task Get_WithCurrentAndCandidates_ShowsOnlyReadyPreviewAndActions()
    {
        var account = await CreateAccountAsync(createProfile: true);
        var current = Media(account.UserId, MediaStatus.Ready, minutesAgo: 15);
        var pending = Media(
            account.UserId,
            MediaStatus.ProcessingPending,
            minutesAgo: 10);
        var failed = Media(account.UserId, MediaStatus.Failed, minutesAgo: 5);
        failed.FailureCode = "image_invalid_format";
        await AddMediaAsync(current, pending, failed);
        await using var baseFactory = new WebAppFactory(_fixture.ConnectionString);
        await using var factory = baseFactory.WithWebHostBuilder(ConfigureMediaHost);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        await PageTestSupport.LoginAsync(client, account.Email, account.Password);

        using var response = await client.GetAsync(
            "/Account/Profile",
            TestContext.Current.CancellationToken);
        var html = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-media-upload-mode=\"replace\"", html);
        Assert.Contains($"data-media-id=\"{current.Id}\"", html);
        Assert.Contains($"data-media-id=\"{pending.Id}\"", html);
        Assert.Contains($"data-media-id=\"{failed.Id}\"", html);
        Assert.Contains("data-media-preview", html);
        Assert.Contains(
            $"https://media.example.test/{MediaObjectKey.Derived(current.Id, 1, MediaDerivedVariant.ProfileThumbnail)}",
            html);
        Assert.Contains("현재 공개", html);
        Assert.Contains("처리 대기", html);
        Assert.Contains("처리 실패", html);
        Assert.Contains("image_invalid_format", html);
        Assert.Equal(2, CountOccurrences(html, "data-media-action=\"remove\""));
        Assert.DoesNotContain("data-default-profile-image", html);
    }

    private async Task<TestAccount> CreateAccountAsync(bool createProfile)
    {
        var (email, password) = await PageTestSupport.CreateUserWithKnownPasswordAsync(
            _fixture);
        await using var db = _fixture.NewDbContext();
        var user = await TestAccountProtection.FindUserAsync(
            db,
            email,
            TestContext.Current.CancellationToken);
        if (createProfile)
        {
            db.UserProfiles.Add(new UserProfile
            {
                UserId = user.Id,
                Biography = string.Empty,
                CreatedAt = Now.AddDays(-1),
                UpdatedAt = Now.AddDays(-1),
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return new TestAccount(user.Id, email, password);
    }

    private async Task AddMediaAsync(params MediaAsset[] media)
    {
        await using var db = _fixture.NewDbContext();
        db.MediaAssets.AddRange(media);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static MediaAsset Media(
        long userId,
        MediaStatus status,
        int minutesAgo)
    {
        var ready = status == MediaStatus.Ready;
        var hasSource = status is MediaStatus.ProcessingPending
            or MediaStatus.Processing
            or MediaStatus.Ready;
        var changedAt = Now.AddMinutes(-minutesAgo);
        return new MediaAsset
        {
            Id = Guid.NewGuid(),
            OwnerUserId = userId,
            ProfileUserId = userId,
            AttachmentKind = MediaAttachmentKind.ProfileImage,
            Status = status,
            DeclaredContentType = "image/png",
            DeclaredBytes = 128,
            SourceContentType = hasSource ? "image/png" : null,
            SourceBytes = hasSource ? 128 : null,
            SourceETag = hasSource ? $"\"{Guid.NewGuid():N}\"" : null,
            DetectedContentType = ready ? "image/png" : null,
            PixelWidth = ready ? 80 : null,
            PixelHeight = ready ? 80 : null,
            FrameCount = ready ? 1 : null,
            IsCurrent = ready,
            PublishedAt = ready ? changedAt : null,
            CreatedAt = changedAt,
            UpdatedAt = changedAt,
            StatusChangedAt = changedAt,
            UploadExpiresAt = changedAt.AddHours(24),
        };
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static void ConfigureMediaHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Media:ObjectStorage:Enabled", "true");
        builder.UseSetting(
            "Media:ObjectStorage:ServiceUrl",
            "https://storage.example.test");
        builder.UseSetting("Media:ObjectStorage:Region", "auto");
        builder.UseSetting("Media:ObjectStorage:BucketName", "profile-media-test");
        builder.UseSetting("Media:ObjectStorage:AccessKeyId", "test-access-key");
        builder.UseSetting("Media:ObjectStorage:SecretAccessKey", "test-secret-key");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMediaObjectStorage>();
            services.AddSingleton<IMediaObjectStorage, StubObjectStorage>();
        });
    }

    private sealed record TestAccount(
        long UserId,
        string Email,
        string Password);

    private sealed class StubObjectStorage : IMediaObjectStorage
    {
        public Task<Uri> CreateDownloadUrlAsync(
            ObjectStorageDownloadUrlRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Uri(
                $"https://media.example.test/{request.ObjectKey.Value}"));

        public Task<Uri> CreateUploadUrlAsync(
            ObjectStorageUploadUrlRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ObjectStorageMetadata?> GetMetadataAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ObjectStorageReadResult> OpenReadAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteAsync(
            ObjectStorageWriteRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CopyAsync(
            ObjectStorageCopyRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteIfExistsAsync(
            MediaObjectKey objectKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
