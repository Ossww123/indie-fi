using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace IndieFi.Web.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuthenticationSensitiveLoggingTests
{
    private readonly PostgreSqlFixture _fixture;

    public AuthenticationSensitiveLoggingTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AuthenticationRequests_DoNotLogEmailTokenCiphertextOrHmac()
    {
        var email = $"sensitive-log-{Guid.NewGuid():N}@example.test";
        var token = $"sensitive-token-{Guid.NewGuid():N}";
        string ciphertext;
        string lookupValue;
        await using (var db = _fixture.NewDbContext())
        {
            var user = TestAccountProtection.AddUser(db, email);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var lookup = await db.UserEmailLookups.FindAsync(
                [user.Id, "lookup-test-v1"],
                TestContext.Current.CancellationToken);
            ciphertext = Convert.ToBase64String(user.EmailCiphertext);
            lookupValue = Convert.ToHexString(Assert.IsType<byte[]>(lookup?.Value));
        }

        var logs = new RecordingLoggerProvider();
        using var baseFactory = new WebAppFactory(_fixture.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(logs);
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });

        using (var tokenResponse = await client.GetAsync(
                   $"/ConfirmEmail?token={Uri.EscapeDataString(token)}",
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        }

        using var pageResponse = await client.GetAsync(
            "/ForgotPassword",
            TestContext.Current.CancellationToken);
        var pageHtml = await pageResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["__RequestVerificationToken"] =
                PageTestSupport.ExtractAntiforgeryToken(pageHtml),
        });
        using var resetResponse = await client.PostAsync(
            "/ForgotPassword",
            form,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, resetResponse.StatusCode);

        var output = string.Join(Environment.NewLine, logs.Messages);
        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(email, output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(token, output, StringComparison.Ordinal);
        Assert.DoesNotContain(ciphertext, output, StringComparison.Ordinal);
        Assert.DoesNotContain(lookupValue, output, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) =>
            new RecordingLogger(categoryName, _messages);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<string> _messages;

        public RecordingLogger(
            string category,
            ConcurrentQueue<string> messages)
        {
            _category = category;
            _messages = messages;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = $"{_category}: {formatter(state, exception)}";
            if (exception is not null)
            {
                message += $" {exception}";
            }

            _messages.Enqueue(message);
        }
    }
}
