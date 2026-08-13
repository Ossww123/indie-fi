using System.Security.Claims;
using IndieFi.Web.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace IndieFi.Web.Tests;

public class HttpCurrentUserTests
{
    [Fact]
    public void Anonymous_request_has_no_current_user()
    {
        var currentUser = CreateCurrentUser(new ClaimsPrincipal());

        Assert.False(currentUser.IsAuthenticated);
        Assert.Null(currentUser.UserId);
        Assert.False(currentUser.IsInRole("Admin"));
    }

    [Fact]
    public void Authenticated_request_exposes_user_id_and_role()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(ClaimTypes.Role, "Admin"),
        ],
        authenticationType: "Test");
        var currentUser = CreateCurrentUser(new ClaimsPrincipal(identity));

        Assert.True(currentUser.IsAuthenticated);
        Assert.Equal(42, currentUser.UserId);
        Assert.True(currentUser.IsInRole("Admin"));
        Assert.False(currentUser.IsInRole("Member"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    public void Missing_or_malformed_identifier_is_not_a_user_id(string? identifier)
    {
        var claims = identifier is null
            ? []
            : new[] { new Claim(ClaimTypes.NameIdentifier, identifier) };
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        var currentUser = CreateCurrentUser(new ClaimsPrincipal(identity));

        Assert.True(currentUser.IsAuthenticated);
        Assert.Null(currentUser.UserId);
    }

    private static HttpCurrentUser CreateCurrentUser(ClaimsPrincipal principal)
    {
        var context = new DefaultHttpContext
        {
            User = principal,
        };

        return new HttpCurrentUser(new HttpContextAccessor { HttpContext = context });
    }
}
