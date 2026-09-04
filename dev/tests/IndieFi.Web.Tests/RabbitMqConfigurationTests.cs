using IndieFi.Web.Modules.Notifications.Infrastructure;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class RabbitMqConfigurationTests
{
    [Fact]
    public void IsValid_WhenDisabled_AllowsEmptyCredentials()
    {
        Assert.True(RabbitMqOptions.IsValid(new RabbitMqOptions()));
    }

    [Fact]
    public void IsValid_WhenEnabled_AcceptsExplicitNonGuestCredentials()
    {
        var options = ValidOptions();

        Assert.True(RabbitMqOptions.IsValid(options));
    }

    [Theory]
    [InlineData("", "indiefi", "secret", "indiefi")]
    [InlineData("localhost", "guest", "secret", "indiefi")]
    [InlineData("localhost", "indiefi", "", "indiefi")]
    [InlineData("localhost", "indiefi", "secret", "contains space")]
    public void IsValid_WhenEnabled_RejectsIncompleteOrUnsafeSettings(
        string host,
        string username,
        string password,
        string endpointPrefix)
    {
        var options = ValidOptions();
        options.Host = host;
        options.Username = username;
        options.Password = password;
        options.EndpointPrefix = endpointPrefix;

        Assert.False(RabbitMqOptions.IsValid(options));
    }

    private static RabbitMqOptions ValidOptions() => new()
    {
        Enabled = true,
        Host = "localhost",
        Port = 5672,
        VirtualHost = "/",
        Username = "indiefi",
        Password = "test-secret",
        EndpointPrefix = "indiefi",
    };
}
