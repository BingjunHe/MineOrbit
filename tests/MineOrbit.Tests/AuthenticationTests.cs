using CmlLib.Core.Auth.Microsoft;
using Microsoft.Identity.Client;
using MineOrbit.Infrastructure;
using Xunit;

namespace MineOrbit.Tests;
public sealed class AuthenticationTests
{
    [Fact] public async Task AccountEmailCannotBeUsedAsApplicationId()
    {
        var auth = new MicrosoftAuthenticationService(new(new() { MicrosoftClientId = "account@example.invalid" }));
        var error = await Assert.ThrowsAsync<AuthenticationFailureException>(() => auth.LoginForLaunch(default));
        Assert.Contains("Client ID", error.Message);
    }
    [Fact] public void ExpiredSessionRequiresInteractiveLogin()
    {
        var failure = MicrosoftAuthenticationService.ClassifyFailure(new Exception("wrapper", new MsalUiRequiredException("no_account", "do not log response")));
        Assert.True(failure.RequiresInteraction); Assert.Contains("授权", failure.Message);
    }
    [Fact] public void EmptyMsalHintRequiresFirstLogin()
    {
        var failure = MicrosoftAuthenticationService.ClassifyFailure(new MsalException("loginHint was empty. Interactive Microsoft OAuth with IdToken is required."));
        Assert.True(failure.RequiresInteraction);
    }
    [Fact] public void ApplicationFailureDoesNotCauseLoginLoop()
    {
        var failure = MicrosoftAuthenticationService.ClassifyFailure(new MsalServiceException("invalid_client", "untrusted response"));
        Assert.False(failure.RequiresInteraction); Assert.Contains("Client ID", failure.Message); Assert.DoesNotContain("untrusted response", failure.Message);
    }
    [Theory][InlineData(403, "拒绝")][InlineData(404, "档案")]
    public void MinecraftFailuresAreExplainedWithoutResponseBody(int status, string expected)
    {
        var failure = MicrosoftAuthenticationService.ClassifyFailure(new JEAuthException("error", "type", "private-response-sentinel", status));
        Assert.Contains(expected, failure.Message); Assert.DoesNotContain("private-response-sentinel", failure.Message); Assert.False(failure.RequiresInteraction);
    }
    [Fact] public void OwnershipFailureIsDistinctFromMicrosoftSignIn()
    {
        var failure = MicrosoftAuthenticationService.ClassifyFailure(new JEAuthException("The user doesn't own the game."));
        Assert.Contains("使用权限", failure.Message);
    }
    [Fact] public void NetworkFailureIsSafeAndUnderstandable()
    {
        var failure = MicrosoftAuthenticationService.ClassifyFailure(new HttpRequestException("private-url-or-token"));
        Assert.Contains("网络", failure.Message); Assert.DoesNotContain("private-url-or-token", failure.Message);
    }
    [Fact] public void UnknownFailureDoesNotExposePayload()
    {
        var failure = MicrosoftAuthenticationService.ClassifyFailure(new InvalidOperationException("private-token-sentinel"));
        Assert.DoesNotContain("private-token-sentinel", failure.Message);
    }
    [Fact] public void NonJsonMinecraftHttpFailureKeepsStatusWithoutBody()
    {
        var failure = MicrosoftAuthenticationService.ClassifyFailure(new JEAuthException("403: private-response-sentinel"));
        Assert.Contains("拒绝", failure.Message); Assert.DoesNotContain("private-response-sentinel", failure.Message);
    }
    [Fact] public void MinecraftUnauthorizedSessionRequiresFreshAuthorization()
    {
        Assert.True(MicrosoftAuthenticationService.ClassifyFailure(new JEAuthException("error", "type", "private", 401)).RequiresInteraction);
    }
}
