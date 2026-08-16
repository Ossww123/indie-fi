using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class NotificationHubTests
{
    [Fact]
    public void Hub_RequiresAuthorizationAndExposesNoClientInvokableMethods()
    {
        Assert.NotNull(typeof(NotificationHub)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .SingleOrDefault());
        Assert.DoesNotContain(
            typeof(NotificationHub).GetMethods(),
            method => method.DeclaringType == typeof(NotificationHub));
    }

    [Fact]
    public void RealtimeSignal_HasStableNameAndNoPayloadType()
    {
        Assert.Equal(
            "NotificationsChanged",
            SignalRNotificationRealtimeNotifier.ChangedMethod);
        Assert.Equal(
            typeof(Task),
            typeof(INotificationRealtimeNotifier)
                .GetMethod(nameof(INotificationRealtimeNotifier.NotifyChangedAsync))!
                .ReturnType);
    }

    [Fact]
    public async Task Notifier_TargetsOneUserWithEmptyPayload()
    {
        var clients = new RecordingHubClients();
        var notifier = new SignalRNotificationRealtimeNotifier(
            new FakeHubContext(clients),
            NullLogger<SignalRNotificationRealtimeNotifier>.Instance);

        await notifier.NotifyChangedAsync(
            42,
            TestContext.Current.CancellationToken);

        Assert.Equal("42", clients.RequestedUser);
        Assert.Equal(
            SignalRNotificationRealtimeNotifier.ChangedMethod,
            clients.Proxy.Method);
        Assert.Empty(clients.Proxy.Arguments);
    }

    [Fact]
    public async Task Notifier_SignalRFailure_DoesNotFailCaller()
    {
        var clients = new RecordingHubClients();
        clients.Proxy.ShouldFail = true;
        var notifier = new SignalRNotificationRealtimeNotifier(
            new FakeHubContext(clients),
            NullLogger<SignalRNotificationRealtimeNotifier>.Instance);

        await notifier.NotifyChangedAsync(
            42,
            TestContext.Current.CancellationToken);
    }

    private sealed class FakeHubContext : IHubContext<NotificationHub>
    {
        public FakeHubContext(IHubClients clients)
        {
            Clients = clients;
        }

        public IHubClients Clients { get; }
        public IGroupManager Groups { get; } = null!;
    }

    private sealed class RecordingHubClients : IHubClients
    {
        public RecordingClientProxy Proxy { get; } = new();
        public string? RequestedUser { get; private set; }
        public IClientProxy All => throw new NotSupportedException();

        public IClientProxy User(string userId)
        {
            RequestedUser = userId;
            return Proxy;
        }

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) =>
            throw new NotSupportedException();
        public IClientProxy Client(string connectionId) =>
            throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) =>
            throw new NotSupportedException();
        public IClientProxy Group(string groupName) =>
            throw new NotSupportedException();
        public IClientProxy GroupExcept(
            string groupName,
            IReadOnlyList<string> excludedConnectionIds) =>
            throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) =>
            throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingClientProxy : IClientProxy
    {
        public bool ShouldFail { get; set; }
        public string? Method { get; private set; }
        public IReadOnlyList<object?> Arguments { get; private set; } = [];

        public Task SendCoreAsync(
            string method,
            object?[] args,
            CancellationToken cancellationToken = default)
        {
            if (ShouldFail)
            {
                throw new InvalidOperationException("Expected SignalR failure.");
            }

            Method = method;
            Arguments = args;
            return Task.CompletedTask;
        }
    }
}
