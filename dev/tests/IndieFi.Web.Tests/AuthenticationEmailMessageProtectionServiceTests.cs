using System.Security.Cryptography;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Accounts.Domain;
using Xunit;

namespace IndieFi.Web.Tests;

public sealed class AuthenticationEmailMessageProtectionServiceTests
{
    private const long UserId = 1042;
    private const string PlaintextToken =
        "LJUBgJ6r7SQWPQkP6dYH5B2m5fY3Qq8hBcVv2gL3g9A";

    [Fact]
    public void Protect_ThenUnprotect_RestoresTokenWithSeparateMessageKey()
    {
        var service = TestAccountProtection.CreateMessageProtectionService();

        var protectedPayload = service.Protect(
            UserId,
            AuthenticationEmailMessageKind.EmailConfirmation,
            PlaintextToken);

        Assert.Equal(
            PlaintextToken,
            service.Unprotect(
                UserId,
                AuthenticationEmailMessageKind.EmailConfirmation,
                protectedPayload));
        Assert.Equal(
            AuthenticationEmailMessageProtectionService.NonceSize,
            protectedPayload.Nonce.Length);
        Assert.Equal(
            AuthenticationEmailMessageProtectionService.AuthenticationTagSize,
            protectedPayload.AuthenticationTag.Length);
        Assert.Equal("message-test-v1", protectedPayload.KeyId);
        Assert.NotEqual(TestAccountProtection.EmailKey, TestAccountProtection.MessageKey);
        Assert.DoesNotContain(
            PlaintextToken,
            Convert.ToBase64String(protectedPayload.Ciphertext));
    }

    [Fact]
    public void ProtectingSamePayloadTwice_UsesDifferentNonceAndCiphertext()
    {
        var service = TestAccountProtection.CreateMessageProtectionService();

        var first = service.Protect(
            UserId,
            AuthenticationEmailMessageKind.EmailConfirmation,
            PlaintextToken);
        var second = service.Protect(
            UserId,
            AuthenticationEmailMessageKind.EmailConfirmation,
            PlaintextToken);

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Theory]
    [InlineData(ProtectedPart.Ciphertext)]
    [InlineData(ProtectedPart.Nonce)]
    [InlineData(ProtectedPart.AuthenticationTag)]
    public void Unprotect_WhenProtectedDataWasChanged_RejectsIt(ProtectedPart part)
    {
        var service = TestAccountProtection.CreateMessageProtectionService();
        var original = service.Protect(
            UserId,
            AuthenticationEmailMessageKind.EmailConfirmation,
            PlaintextToken);
        var changed = Copy(original);

        switch (part)
        {
            case ProtectedPart.Ciphertext:
                changed.Ciphertext[0] ^= 1;
                break;
            case ProtectedPart.Nonce:
                changed.Nonce[0] ^= 1;
                break;
            case ProtectedPart.AuthenticationTag:
                changed.AuthenticationTag[0] ^= 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(part));
        }

        Assert.ThrowsAny<CryptographicException>(
            () => service.Unprotect(
                UserId,
                AuthenticationEmailMessageKind.EmailConfirmation,
                changed));
    }

    [Fact]
    public void Unprotect_WhenUserWasChanged_RejectsPayloadSwap()
    {
        var service = TestAccountProtection.CreateMessageProtectionService();
        var protectedPayload = service.Protect(
            UserId,
            AuthenticationEmailMessageKind.EmailConfirmation,
            PlaintextToken);

        Assert.ThrowsAny<CryptographicException>(
            () => service.Unprotect(
                UserId + 1,
                AuthenticationEmailMessageKind.EmailConfirmation,
                protectedPayload));
    }

    [Fact]
    public void Unprotect_WhenKeyIdWasChanged_RejectsIt()
    {
        var service = TestAccountProtection.CreateMessageProtectionService();
        var original = service.Protect(
            UserId,
            AuthenticationEmailMessageKind.EmailConfirmation,
            PlaintextToken);
        var changed = original with { KeyId = "message-test-v2" };

        Assert.ThrowsAny<CryptographicException>(
            () => service.Unprotect(
                UserId,
                AuthenticationEmailMessageKind.EmailConfirmation,
                changed));
    }

    private static ProtectedAuthenticationEmailPayload Copy(
        ProtectedAuthenticationEmailPayload value)
    {
        return new ProtectedAuthenticationEmailPayload(
            value.Ciphertext.ToArray(),
            value.Nonce.ToArray(),
            value.AuthenticationTag.ToArray(),
            value.KeyId);
    }

    public enum ProtectedPart
    {
        Ciphertext,
        Nonce,
        AuthenticationTag,
    }
}
