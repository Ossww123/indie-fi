using Testcontainers.RabbitMq;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    public const string Username = "indiefi_test";
    public const string Password = "rabbit-test-password";

    public RabbitMqContainer Container { get; } =
        new RabbitMqBuilder("rabbitmq:4.3.4-management-alpine")
            .WithUsername(Username)
            .WithPassword(Password)
            .Build();

    public async ValueTask InitializeAsync()
    {
        await Container.StartAsync(TestContext.Current.CancellationToken);
        var readiness = await Container.ExecAsync(
            ["rabbitmq-diagnostics", "-q", "check_running"],
            TestContext.Current.CancellationToken);
        if (readiness.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"RabbitMQ did not become ready: {readiness.Stderr}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Container.DisposeAsync();
    }
}
