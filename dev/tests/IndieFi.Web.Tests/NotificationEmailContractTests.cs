using System.Net.Mail;
using IndieFi.Web.Modules.Notifications.Application;
using IndieFi.Web.Modules.Notifications.Domain;
using IndieFi.Web.Modules.Notifications.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class NotificationEmailContractTests
{
    [Fact]
    public void Render_UsesPlaintextTemplateAndAbsoluteLocalLinks()
    {
        var renderer = new NotificationEmailTemplateRenderer(
            Microsoft.Extensions.Options.Options.Create(CreateOptions()));
        var delivery = Delivery();

        var request = renderer.Render(delivery, "recipient@example.test");

        Assert.Equal(delivery.MessageId, request.MessageId);
        Assert.Equal("recipient@example.test", request.RecipientAddress);
        Assert.Equal("[IndieFi] A notification", request.Subject);
        Assert.Contains("Notification body", request.Body);
        Assert.Contains("https://indiefi.example/Games/Detail?id=10", request.Body);
        Assert.Contains("https://indiefi.example/Account/Notifications", request.Body);
        Assert.DoesNotContain("<a", request.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MessageFactory_UsesStableMessageIdAndPlainText()
    {
        var options = Microsoft.Extensions.Options.Options.Create(CreateOptions());
        var renderer = new NotificationEmailTemplateRenderer(options);
        var request = renderer.Render(Delivery(), "recipient@example.test");
        var factory = new NotificationEmailMessageFactory(options);

        using var first = factory.Create(request);
        using var second = factory.Create(request);

        Assert.False(first.IsBodyHtml);
        Assert.Equal(first.Headers["Message-ID"], second.Headers["Message-ID"]);
        Assert.Equal(
            $"<{request.MessageId:N}@example.test>",
            first.Headers["Message-ID"]);
    }

    [Theory]
    [InlineData(421, NotificationEmailFailureKind.Transient)]
    [InlineData(451, NotificationEmailFailureKind.Transient)]
    [InlineData(550, NotificationEmailFailureKind.Permanent)]
    [InlineData(554, NotificationEmailFailureKind.Permanent)]
    public void FailureClassifier_SeparatesSmtp4xxAnd5xx(
        int statusCode,
        NotificationEmailFailureKind expected)
    {
        var failure = NotificationEmailFailureClassifier.Classify(
            new SmtpException((SmtpStatusCode)statusCode));

        Assert.Equal(expected, failure.Kind);
    }

    [Fact]
    public void Options_WhenEnabled_RejectsUnsafeOrIncompleteConfiguration()
    {
        var options = CreateOptions();
        options.PublicBaseUrl = "https://user@example.test/path?token=secret";
        options.Smtp.Host = string.Empty;
        options.Smtp.Password = "orphaned-secret";

        var result = NotificationEmailDeliveryOptions.Validate(options);

        Assert.True(result.Failed);
        Assert.Equal(3, result.Failures.Count());
    }

    private static NotificationDelivery Delivery()
    {
        var notification = new Notification
        {
            Title = "A notification",
            Body = "Notification body",
            DestinationPath = "/Games/Detail?id=10",
            OccurredAt = new DateTimeOffset(2026, 8, 16, 1, 2, 3, TimeSpan.Zero),
        };
        return new NotificationDelivery
        {
            MessageId = Guid.Parse("fbc9de87-2b05-42ac-a262-60ec6d270110"),
            Notification = notification,
        };
    }

    private static NotificationEmailDeliveryOptions CreateOptions() => new()
    {
        Enabled = true,
        PublicBaseUrl = "https://indiefi.example/",
        Smtp = new NotificationSmtpOptions
        {
            Host = "smtp.example.test",
            Port = 2525,
            EnableSsl = false,
            FromAddress = "notifications@example.test",
            FromName = "IndieFi",
        },
    };
}
