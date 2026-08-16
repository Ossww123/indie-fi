using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using IndieFi.Web.Modules.Media;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class MinioFixture : IAsyncLifetime
{
    public const string BucketName = "indiefi-media-tests";
    public const string AppAccessKey = "indiefi-media-test";
    public const string AppSecretKey = "minio-app-test-password";
    private const string RootAccessKey = "indiefi-test-root";
    private const string RootSecretKey = "minio-root-test-password";

    private readonly IContainer _container =
        new ContainerBuilder("quay.io/minio/minio:RELEASE.2025-07-23T15-54-02Z")
            .WithEnvironment("MINIO_ROOT_USER", RootAccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", RootSecretKey)
            .WithCommand("server", "/data", "--console-address", ":9001")
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request
                    .ForPort(9000)
                    .ForPath("/minio/health/ready")))
            .Build();

    public string ServiceUrl =>
        $"http://{_container.Hostname}:{_container.GetMappedPublicPort(9000)}";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        var initialization = await _container.ExecAsync(
            ["/bin/sh", "-c", InitializationScript()],
            TestContext.Current.CancellationToken);
        if (initialization.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"MinIO test initialization failed: {initialization.Stderr}");
        }
    }

    public ServiceProvider NewServiceProvider(
        string accessKey = AppAccessKey,
        string secretKey = AppSecretKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Media:ObjectStorage:Enabled"] = "true",
                ["Media:ObjectStorage:ServiceUrl"] = ServiceUrl,
                ["Media:ObjectStorage:Region"] = "us-east-1",
                ["Media:ObjectStorage:BucketName"] = BucketName,
                ["Media:ObjectStorage:AccessKeyId"] = accessKey,
                ["Media:ObjectStorage:SecretAccessKey"] = secretKey,
                ["Media:ObjectStorage:OperationTimeout"] = "00:00:10",
                ["Media:ObjectStorage:ForcePathStyle"] = "true",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(
            new TestHostEnvironment(Environments.Development));
        services.AddMediaModule(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    public Task SuspendAppAccessAsync(
        CancellationToken cancellationToken = default) =>
        ChangeAppPolicyAsync("detach", cancellationToken);

    public Task RestoreAppAccessAsync(
        CancellationToken cancellationToken = default) =>
        ChangeAppPolicyAsync("attach", cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    private static string InitializationScript() => $$"""
        set -eu
        mc alias set local http://127.0.0.1:9000 {{RootAccessKey}} {{RootSecretKey}}
        mc mb --ignore-existing local/{{BucketName}}
        mc anonymous set private local/{{BucketName}}
        cat >/tmp/indiefi-media-test-policy.json <<'EOF'
        {
          "Version": "2012-10-17",
          "Statement": [
            {
              "Effect": "Allow",
              "Action": ["s3:GetBucketLocation", "s3:ListBucket"],
              "Resource": ["arn:aws:s3:::{{BucketName}}"]
            },
            {
              "Effect": "Allow",
              "Action": [
                "s3:AbortMultipartUpload",
                "s3:DeleteObject",
                "s3:GetObject",
                "s3:ListMultipartUploadParts",
                "s3:PutObject"
              ],
              "Resource": ["arn:aws:s3:::{{BucketName}}/*"]
            }
          ]
        }
        EOF
        mc admin user add local {{AppAccessKey}} {{AppSecretKey}}
        mc admin policy create local indiefi-media-test-rw /tmp/indiefi-media-test-policy.json
        mc admin policy attach local indiefi-media-test-rw --user {{AppAccessKey}}
        """;

    private async Task ChangeAppPolicyAsync(
        string operation,
        CancellationToken cancellationToken)
    {
        var result = await _container.ExecAsync(
            [
                "mc",
                "admin",
                "policy",
                operation,
                "local",
                "indiefi-media-test-rw",
                "--user",
                AppAccessKey,
            ],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"MinIO app policy {operation} failed: {result.Stderr}");
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "IndieFi.Web.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class MinioCollection : ICollectionFixture<MinioFixture>
{
    public const string Name = "MinIO";
}
