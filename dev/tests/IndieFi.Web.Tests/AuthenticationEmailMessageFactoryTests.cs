using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class AuthenticationEmailMessageFactoryTests
{
    private const string Token =
        "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE";

    [Fact]
    public void CreateEmailConfirmation_IncludesRecipientAndUsableConfirmationLink()
    {
        var factory = new AuthenticationEmailMessageFactory(
            Options.Create(ValidOptions()));
        var delivery = new AuthenticationEmailDelivery(
            "owner@example.test",
            Token,
            new DateTimeOffset(2026, 8, 15, 6, 0, 0, TimeSpan.Zero));

        using var message = factory.CreateEmailConfirmation(delivery);

        Assert.Equal("IndieFi", message.From!.DisplayName);
        Assert.Equal("no-reply@example.test", message.From.Address);
        Assert.Equal("owner@example.test", Assert.Single(message.To).Address);
        Assert.Contains("IndieFi 이메일 주소 확인", message.Subject, StringComparison.Ordinal);
        Assert.Contains(
            $"http://localhost:5258/ConfirmEmail?token={Token}",
            message.Body,
            StringComparison.Ordinal);
        Assert.Contains("2026-08-15 06:00 UTC", message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePasswordReset_IncludesRecipientAndTrustedResetLink()
    {
        var factory = new AuthenticationEmailMessageFactory(
            Options.Create(ValidOptions()));
        var delivery = new AuthenticationEmailDelivery(
            "owner@example.test",
            Token,
            new DateTimeOffset(2026, 8, 14, 6, 30, 0, TimeSpan.Zero));

        using var message = factory.CreatePasswordReset(delivery);

        Assert.Equal("IndieFi", message.From!.DisplayName);
        Assert.Equal("no-reply@example.test", message.From.Address);
        Assert.Equal("owner@example.test", Assert.Single(message.To).Address);
        Assert.Equal("IndieFi 비밀번호 재설정", message.Subject);
        Assert.Contains(
            $"http://localhost:5258/ResetPassword?token={Token}",
            message.Body,
            StringComparison.Ordinal);
        Assert.Contains("2026-08-14 06:30 UTC", message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WhenEnabledConfigurationIsIncomplete_RejectsIt()
    {
        var options = ValidOptions();
        options.Smtp.Host = string.Empty;

        var result = AuthenticationEmailDeliveryOptions.Validate(options);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Validate_WhenDeliveryIsExplicitlyDisabled_AllowsMissingSmtpConfiguration()
    {
        var options = new AuthenticationEmailDeliveryOptions
        {
            Enabled = false,
        };

        var result = AuthenticationEmailDeliveryOptions.Validate(options);

        Assert.True(result.Succeeded);
    }

    private static AuthenticationEmailDeliveryOptions ValidOptions() => new()
    {
        Enabled = true,
        PublicBaseUrl = "http://localhost:5258",
        WorkerInterval = TimeSpan.FromSeconds(5),
        BatchSize = 20,
        Smtp = new SmtpDeliveryOptions
        {
            Host = "localhost",
            Port = 1025,
            EnableSsl = false,
            FromAddress = "no-reply@example.test",
            FromName = "IndieFi",
            Timeout = TimeSpan.FromSeconds(10),
        },
    };
}
