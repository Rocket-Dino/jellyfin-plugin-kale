using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Kale.Api;
using Jellyfin.Plugin.Kale.Share;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Jellyfin.Plugin.Kale.Tests;

/// <summary>
/// Kale Share invitation sign-ins (kale#331): one secret, one account, one use, before it expires;
/// the same device may repeat a redemption it lost; only hashes are kept.
/// </summary>
public class ShareGrantsTests
{
    private const string Secret = "00112233445566778899aabbccddeeff";
    private static readonly Guid Alex = Guid.Parse("7a0c7f3e-4f42-4a51-9b39-6d0a7d0e2b11");

    private DateTime _now = new(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
    private List<ShareGrant> _stored = new();
    private int _saves;

    private ShareGrants Grants() => new(() => _stored.Select(Copy).ToList(), g => { _stored = g.Select(Copy).ToList(); _saves++; }, () => _now);

    private static ShareGrant Copy(ShareGrant g) => new()
    {
        Id = g.Id, UserId = g.UserId, SecretHash = g.SecretHash, Label = g.Label, CreatedUtc = g.CreatedUtc,
        ExpiresUtc = g.ExpiresUtc, RedeemedUtc = g.RedeemedUtc, RedeemedDeviceId = g.RedeemedDeviceId,
        RedeemedDeviceName = g.RedeemedDeviceName, Spares = g.Spares,
    };

    private ShareGrant Invite(ShareGrants grants, TimeSpan? lifetime = null) =>
        grants.Create(Alex, ShareGrants.HashOf(Secret)!, "Alex", _now + (lifetime ?? TimeSpan.FromDays(2)))!;

    [Fact]
    public void OnlyTheHashIsStored()
    {
        Invite(Grants());
        var stored = Assert.Single(_stored);
        Assert.Equal(ShareGrants.HashOf(Secret), stored.SecretHash);
        Assert.DoesNotContain(Secret, stored.SecretHash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSecretSignsInTheAccountItWasMadeFor()
    {
        var grants = Grants();
        Invite(grants);
        var (outcome, grant, fresh) = grants.Redeem(Secret, "alex-iphone", "Alex's iPhone");
        Assert.Equal(RedeemOutcome.SignIn, outcome);
        Assert.Equal(Alex, grant!.UserId);
        Assert.True(fresh);
        Assert.Equal("Alex's iPhone", _stored.Single().RedeemedDeviceName);
    }

    [Fact]
    public void ASecondDeviceCannotUseItAgain()
    {
        var grants = Grants();
        Invite(grants);
        grants.Redeem(Secret, "alex-iphone", "Alex's iPhone");
        Assert.Equal(RedeemOutcome.Used, grants.Redeem(Secret, "someone-else", "Forwarded").Outcome);
    }

    [Fact]
    public void TheSameDeviceMayRepeatALostRedemptionForAWhile()
    {
        var grants = Grants();
        Invite(grants);
        grants.Redeem(Secret, "alex-iphone", "Alex's iPhone");

        _now += TimeSpan.FromMinutes(5);
        var again = grants.Redeem(Secret, "alex-iphone", "Alex's iPhone");
        Assert.Equal(RedeemOutcome.SignIn, again.Outcome);
        Assert.False(again.Fresh);

        _now += ShareGrants.RetryWindow;
        Assert.Equal(RedeemOutcome.Used, grants.Redeem(Secret, "alex-iphone", "Alex's iPhone").Outcome);
    }

    [Fact]
    public void AnExpiredInvitationSignsNobodyIn()
    {
        var grants = Grants();
        Invite(grants, TimeSpan.FromDays(1));
        _now += TimeSpan.FromDays(1);
        Assert.Equal(RedeemOutcome.Expired, grants.Redeem(Secret, "alex-iphone", "Alex's iPhone").Outcome);
    }

    [Theory]
    [InlineData("00112233445566778899aabbccddeefe")] // one byte off
    [InlineData("")]
    [InlineData("not hex at all, not hex at all!!")]
    [InlineData("0011")] // too short to be a secret
    public void AnythingElseIsUnknown(string secret)
    {
        var grants = Grants();
        Invite(grants);
        Assert.Equal(RedeemOutcome.Unknown, grants.Redeem(secret, "alex-iphone", "Alex's iPhone").Outcome);
        Assert.Null(_stored.Single().RedeemedUtc);
    }

    [Fact]
    public void AWithdrawnInvitationIsUnknown()
    {
        var grants = Grants();
        var grant = Invite(grants);
        Assert.True(grants.Withdraw(grant.Id));
        Assert.Equal(RedeemOutcome.Unknown, grants.Redeem(Secret, "alex-iphone", "Alex's iPhone").Outcome);
    }

    [Fact]
    public void ARefusedSignInLeavesTheInvitationUsable()
    {
        var grants = Grants();
        var grant = Invite(grants);
        grants.Redeem(Secret, "alex-iphone", "Alex's iPhone");
        grants.Unredeem(grant.Id); // Jellyfin refused: account disabled
        Assert.Equal(RedeemOutcome.SignIn, grants.Redeem(Secret, "alex-ipad", "Alex's iPad").Outcome);
    }

    [Fact]
    public void SparesAttachUntilRedeemedAndComeBackWithTheGrant()
    {
        var grants = Grants();
        var grant = Invite(grants);
        Assert.True(grants.SetSpares(grant.Id, "c2VhbGVk"));
        var (_, redeemed, _) = grants.Redeem(Secret, "alex-iphone", "Alex's iPhone");
        Assert.Equal("c2VhbGVk", redeemed!.Spares);
        Assert.False(grants.SetSpares(grant.Id, "bGF0ZQ=="), "a redeemed grant takes no new spares");
    }

    [Fact]
    public void OversizedSparesAreRefused()
    {
        var grants = Grants();
        var grant = Invite(grants);
        Assert.False(grants.SetSpares(grant.Id, new string('a', ShareGrants.MaxSparesLength + 1)));
        Assert.Null(_stored.Single().Spares);
    }

    [Theory]
    [InlineData(0)] // already expired
    [InlineData(31)] // longer than the plugin accepts
    public void UnreasonableExpiriesAreRefused(int days)
    {
        Assert.Null(Grants().Create(Alex, ShareGrants.HashOf(Secret)!, "Alex", _now + TimeSpan.FromDays(days)));
        Assert.Empty(_stored);
    }

    [Fact]
    public void ARawSecretIsNotAcceptedAsAHash()
    {
        Assert.Null(Grants().Create(Alex, Secret, "Alex", _now + TimeSpan.FromDays(2)));
    }

    [Fact]
    public void FinishedInvitationsAreDroppedAfterAMonth()
    {
        var grants = Grants();
        Invite(grants, TimeSpan.FromDays(1));
        _now += TimeSpan.FromDays(1) + ShareGrants.KeepFinished;
        Assert.Empty(grants.List());
        Assert.Empty(_stored);
    }

    [Theory]
    [InlineData(nameof(KaleShareController.CreateGrant))]
    [InlineData(nameof(KaleShareController.ListGrants))]
    [InlineData(nameof(KaleShareController.WithdrawGrant))]
    [InlineData(nameof(KaleShareController.SetSpares))]
    public void OnlyAnAdminManagesInvitations(string action) =>
        Assert.Contains(
            typeof(KaleShareController).GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>(),
            a => a.Policy == Policies.RequiresElevation);

    [Fact]
    public void RedeemingNeedsNoSession() =>
        Assert.NotNull(typeof(KaleShareController).GetMethod(nameof(KaleShareController.Redeem))!.GetCustomAttribute<AllowAnonymousAttribute>());
}
