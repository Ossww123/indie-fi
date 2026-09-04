using IndieFi.Web.Modules.Media;
using IndieFi.Web.Modules.Media.Application;
using IndieFi.Web.Modules.Media.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class MediaModuleRegistrationTests
{
    [Fact]
    public void AddMediaModule_WebHostWithWorkerEnabled_RejectsConfiguration()
    {
        var services = new ServiceCollection();
        var configuration = Configuration(
            objectStorageEnabled: true,
            workerEnabled: true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediaModule(configuration, MediaHostRole.Web));

        Assert.Contains("IndieFi.Worker", exception.Message);
    }

    [Fact]
    public void AddMediaModule_WorkerHostWithStorageButWorkerDisabled_RejectsConfiguration()
    {
        var services = new ServiceCollection();
        var configuration = Configuration(
            objectStorageEnabled: true,
            workerEnabled: false);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddMediaModule(configuration, MediaHostRole.Worker));

        Assert.Contains("Media:Worker:Enabled", exception.Message);
    }

    [Fact]
    public void AddMediaModule_WebHost_DoesNotRegisterMediaBackgroundWorkers()
    {
        var services = new ServiceCollection();

        services.AddMediaModule(
            Configuration(objectStorageEnabled: true, workerEnabled: false),
            MediaHostRole.Web);

        Assert.Empty(MediaHostedServiceTypes(services));
    }

    [Fact]
    public void AddMediaModule_EnabledWorkerHost_RegistersOnlyMediaBackgroundWorkers()
    {
        var services = new ServiceCollection();

        services.AddMediaModule(
            Configuration(objectStorageEnabled: true, workerEnabled: true),
            MediaHostRole.Worker);

        Assert.Equal(
            [
                typeof(MediaProcessingRecoveryWorker),
                typeof(MediaCleanupWorker),
                typeof(MediaOperationsWorker),
            ],
            MediaHostedServiceTypes(services));
    }

    [Fact]
    public void AddMediaModule_DisabledWorkerHost_AllowsNotificationOnlyRuntime()
    {
        var services = new ServiceCollection();

        services.AddMediaModule(
            Configuration(objectStorageEnabled: false, workerEnabled: false),
            MediaHostRole.Worker);

        Assert.Empty(MediaHostedServiceTypes(services));
    }

    private static Type[] MediaHostedServiceTypes(IServiceCollection services) =>
        services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .OfType<Type>()
            .Where(type => type.Namespace?.Contains("Modules.Media") == true)
            .ToArray();

    private static IConfiguration Configuration(
        bool objectStorageEnabled,
        bool workerEnabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Media:ObjectStorage:Enabled"] = objectStorageEnabled.ToString(),
                ["Media:Worker:Enabled"] = workerEnabled.ToString(),
            })
            .Build();
}
