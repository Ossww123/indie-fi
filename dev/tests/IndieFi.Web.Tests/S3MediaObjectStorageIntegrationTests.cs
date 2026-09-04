using System.Net;
using System.Net.Http.Headers;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(MinioCollection.Name)]
public sealed class S3MediaObjectStorageIntegrationTests
{
    private readonly MinioFixture _fixture;

    public S3MediaObjectStorageIntegrationTests(MinioFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SignedUploadAndDownload_AreSharedAcrossIndependentClients()
    {
        await using var providerA = _fixture.NewServiceProvider();
        await using var providerB = _fixture.NewServiceProvider();
        var storageA = providerA.GetRequiredService<IMediaObjectStorage>();
        var storageB = providerB.GetRequiredService<IMediaObjectStorage>();
        var cancellationToken = TestContext.Current.CancellationToken;
        var report = await providerA
            .GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(
                registration => registration.Name == "media-object-storage",
                cancellationToken);
        Assert.Equal(HealthStatus.Healthy, report.Status);

        var mediaId = Guid.NewGuid();
        var stagingKey = MediaObjectKey.Staging(mediaId, Guid.NewGuid());
        var sourceKey = MediaObjectKey.Source(mediaId, revision: 1);
        var derivedKey = MediaObjectKey.Derived(
            mediaId,
            revision: 1,
            MediaDerivedVariant.GameDisplay);
        byte[] original = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a];
        byte[] derived = [0x52, 0x49, 0x46, 0x46, 0x57, 0x45, 0x42, 0x50];
        using var http = new HttpClient();

        var uploadUrl = await storageA.CreateUploadUrlAsync(
            MediaObjectAccessPolicy.CreateUploadUrlRequest(
                stagingKey,
                MediaUploadKind.GameImage,
                "image/png",
                original.LongLength),
            cancellationToken);
        using (var upload = await PutAsync(
            http,
            uploadUrl,
            original,
            "image/png",
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        }

        var stagingMetadata = await storageB.GetMetadataAsync(
            stagingKey,
            cancellationToken);
        Assert.NotNull(stagingMetadata);
        Assert.Equal(original.LongLength, stagingMetadata.ContentLength);
        Assert.Equal("image/png", stagingMetadata.ContentType);

        await storageB.CopyAsync(
            MediaObjectAccessPolicy.CreateSourceCopyRequest(
                stagingKey,
                sourceKey,
                stagingMetadata.ETag,
                "image/png"),
            cancellationToken);
        await using (var content = new MemoryStream(derived, writable: false))
        {
            await storageA.WriteAsync(
                MediaObjectAccessPolicy.CreateDerivedWriteRequest(
                    derivedKey,
                    content,
                    derived.LongLength),
                cancellationToken);
        }

        await using (var read = await storageB.OpenReadAsync(
            derivedKey,
            cancellationToken))
        {
            Assert.Equal("image/webp", read.Metadata.ContentType);
            Assert.Equal(derived, await ReadAllBytesAsync(
                read.Content,
                cancellationToken));
        }

        var downloadUrl = await storageB.CreateDownloadUrlAsync(
            MediaObjectAccessPolicy.CreateDownloadUrlRequest(derivedKey),
            cancellationToken);
        using var download = await http.GetAsync(downloadUrl, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/webp", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            derived,
            await download.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    [Fact]
    public async Task ConditionalCopyDeleteAndBucketPolicy_EnforceStableBoundaries()
    {
        await using var provider = _fixture.NewServiceProvider();
        var storage = provider.GetRequiredService<IMediaObjectStorage>();
        var client = provider.GetRequiredService<IAmazonS3>();
        var cancellationToken = TestContext.Current.CancellationToken;
        var mediaId = Guid.NewGuid();
        var stagingKey = MediaObjectKey.Staging(mediaId, Guid.NewGuid());
        var sourceKey = MediaObjectKey.Source(mediaId, revision: 1);
        byte[] payload = [1, 2, 3, 4];
        using var http = new HttpClient();
        var uploadUrl = await storage.CreateUploadUrlAsync(
            MediaObjectAccessPolicy.CreateUploadUrlRequest(
                stagingKey,
                MediaUploadKind.GameImage,
                "image/png",
                payload.LongLength),
            cancellationToken);
        using (var upload = await PutAsync(
            http,
            uploadUrl,
            payload,
            "image/png",
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        }

        var metadata = await storage.GetMetadataAsync(stagingKey, cancellationToken);
        Assert.NotNull(metadata);
        var changed = await Assert.ThrowsAsync<MediaObjectStorageException>(() =>
            storage.CopyAsync(
                MediaObjectAccessPolicy.CreateSourceCopyRequest(
                    stagingKey,
                    sourceKey,
                    "\"stale-etag\"",
                    "image/png"),
                cancellationToken));
        Assert.Equal(
            MediaObjectStorageFailureCode.PreconditionFailed,
            changed.FailureCode);

        await storage.CopyAsync(
            MediaObjectAccessPolicy.CreateSourceCopyRequest(
                stagingKey,
                sourceKey,
                metadata.ETag,
                "image/png"),
            cancellationToken);
        await storage.DeleteIfExistsAsync(stagingKey, cancellationToken);
        await storage.DeleteIfExistsAsync(stagingKey, cancellationToken);
        Assert.Null(await storage.GetMetadataAsync(stagingKey, cancellationToken));
        Assert.NotNull(await storage.GetMetadataAsync(sourceKey, cancellationToken));

        var createBucket = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            client.PutBucketAsync(
                new PutBucketRequest
                {
                    BucketName = $"not-allowed-{Guid.NewGuid():N}",
                },
                cancellationToken));
        Assert.Equal(HttpStatusCode.Forbidden, createBucket.StatusCode);

        using var anonymous = await http.GetAsync(
            ObjectUri(sourceKey),
            cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);

        await using var wrongProvider = _fixture.NewServiceProvider(
            "wrong-access-key",
            "wrong-secret-key");
        var wrongStorage = wrongProvider.GetRequiredService<IMediaObjectStorage>();
        var denied = await Assert.ThrowsAsync<MediaObjectStorageException>(() =>
            wrongStorage.GetMetadataAsync(sourceKey, cancellationToken));
        Assert.Equal(
            MediaObjectStorageFailureCode.AccessDenied,
            denied.FailureCode);
    }

    [Fact]
    public async Task SignedRequests_RejectHeaderChangesAndExpiration()
    {
        await using var provider = _fixture.NewServiceProvider();
        var storage = provider.GetRequiredService<IMediaObjectStorage>();
        var client = provider.GetRequiredService<IAmazonS3>();
        var cancellationToken = TestContext.Current.CancellationToken;
        var stagingKey = MediaObjectKey.Staging(Guid.NewGuid(), Guid.NewGuid());
        byte[] payload = [9, 8, 7, 6];
        using var http = new HttpClient();
        var uploadUrl = await storage.CreateUploadUrlAsync(
            MediaObjectAccessPolicy.CreateUploadUrlRequest(
                stagingKey,
                MediaUploadKind.GameImage,
                "image/png",
                payload.LongLength),
            cancellationToken);

        using (var changedType = await PutAsync(
            http,
            uploadUrl,
            payload,
            "image/jpeg",
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, changedType.StatusCode);
        }

        using (var changedLength = await PutAsync(
            http,
            uploadUrl,
            [9, 8, 7, 6, 5],
            "image/png",
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, changedLength.StatusCode);
        }

        Assert.Null(await storage.GetMetadataAsync(stagingKey, cancellationToken));
        using (var accepted = await PutAsync(
            http,
            uploadUrl,
            payload,
            "image/png",
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        var expiresSoon = await client.GetPreSignedURLAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = MinioFixture.BucketName,
                Key = stagingKey.Value,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.AddSeconds(1),
                Protocol = Protocol.HTTP,
            });
        using (var beforeExpiry = await http.GetAsync(
            expiresSoon,
            cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, beforeExpiry.StatusCode);
        }

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        using var expired = await http.GetAsync(expiresSoon, cancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
    }

    private Uri ObjectUri(MediaObjectKey key) => new(
        $"{_fixture.ServiceUrl}/{MinioFixture.BucketName}/{key.Value}");

    private static async Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        Uri requestUri,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, requestUri)
        {
            Content = new ByteArrayContent(content),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Content.Headers.ContentLength = content.LongLength;
        try
        {
            return await client.SendAsync(request, cancellationToken);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
