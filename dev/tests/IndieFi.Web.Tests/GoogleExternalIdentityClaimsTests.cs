using System.Security.Claims;
using IndieFi.Web.Infrastructure.Authentication;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class GoogleExternalIdentityClaimsTests
{
    [Fact]
    public void TryRead_WithVerifiedEmailAndSingleSubject_ReturnsNormalizedIdentity()
    {
        var principal = CreatePrincipal(
            new Claim(ClaimTypes.NameIdentifier, "immutable-subject"),
            new Claim(ClaimTypes.Email, "Person@Example.test"),
            new Claim(
                ExternalAuthenticationDefaults.GoogleEmailVerifiedClaim,
                bool.TrueString));

        var succeeded = GoogleExternalIdentityClaims.TryRead(
            principal,
            out var subject,
            out var email);

        Assert.True(succeeded);
        Assert.Equal("immutable-subject", subject);
        Assert.NotNull(email);
        Assert.Equal("PERSON@EXAMPLE.TEST", email.NormalizedValue);
    }

    [Fact]
    public void TryRead_WithDuplicateSubjects_IsRejected()
    {
        var principal = CreatePrincipal(
            new Claim(ClaimTypes.NameIdentifier, "first-subject"),
            new Claim(ClaimTypes.NameIdentifier, "second-subject"),
            new Claim(ClaimTypes.Email, "person@example.test"),
            new Claim(
                ExternalAuthenticationDefaults.GoogleEmailVerifiedClaim,
                bool.TrueString));

        Assert.False(GoogleExternalIdentityClaims.TryRead(
            principal,
            out _,
            out _));
    }

    [Theory]
    [InlineData("not-an-email", "True")]
    [InlineData("person@example.test", "False")]
    [InlineData("person@example.test", "not-a-boolean")]
    public void TryRead_WithInvalidOrUnverifiedEmail_IsRejected(
        string email,
        string emailVerified)
    {
        var principal = CreatePrincipal(
            new Claim(ClaimTypes.NameIdentifier, "immutable-subject"),
            new Claim(ClaimTypes.Email, email),
            new Claim(
                ExternalAuthenticationDefaults.GoogleEmailVerifiedClaim,
                emailVerified));

        Assert.False(GoogleExternalIdentityClaims.TryRead(
            principal,
            out _,
            out _));
    }

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(
            claims,
            ExternalAuthenticationDefaults.TemporaryScheme));
}
