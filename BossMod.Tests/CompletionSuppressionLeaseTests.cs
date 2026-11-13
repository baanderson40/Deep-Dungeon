using BossMod.Autorotation;
using Xunit;

namespace BossMod.Tests;

public class CompletionSuppressionLeaseTests
{
    [Fact]
    public void DelayedCombatEndKeepsOnlyStillOwnedCompletionSuppression()
    {
        var lease = new CompletionSuppressionLease();
        var token = lease.Acquire(7);
        Assert.True(lease.Owns(token, 7, true));
        Assert.False(lease.ShouldClearOnCombatEnd(true, true, false, true, 7));
        // The delayed end-of-combat event must not clear the owned disable or advance selection.
        Assert.False(lease.ShouldClearOnCombatEnd(false, true, false, true, 7));
        Assert.True(lease.Owns(token, 7, true));
    }

    [Fact]
    public void OrdinaryForceDisableHasNoCompletionProtection()
    {
        var lease = new CompletionSuppressionLease();
        Assert.False(lease.Owns(lease.Token, 7, true));
        Assert.False(lease.Owns(123, 7, true));
        Assert.True(lease.ShouldClearOnCombatEnd(false, true, false, true, 7));
    }

    [Fact]
    public void ManualOrSafetyOverrideInvalidatesEvenWhenPresetIsAlreadyDisabled()
    {
        var lease = new CompletionSuppressionLease();
        var token = lease.Acquire(7);
        Assert.True(lease.Invalidate()); // Tells the manager to advance selection version even for a no-op disable.
        Assert.False(lease.Owns(token, 7, true));
        Assert.False(lease.Owns(token, 8, true));
        Assert.False(lease.Invalidate());
    }

    [Fact]
    public void VersionOrActualPresetMismatchNeverAuthorizesRestoration()
    {
        var lease = new CompletionSuppressionLease();
        var token = lease.Acquire(7);
        Assert.False(lease.Owns(token, 8, true));
        Assert.False(lease.Owns(token, 7, false));
    }

    [Fact]
    public void EndingOwnedScopeReleasesIdempotentlyAndRestoresOrdinaryCombatEndPolicy()
    {
        var lease = new CompletionSuppressionLease();
        var token = lease.Acquire(7);
        Assert.True(lease.Release(token));
        Assert.False(lease.Owns(token, 7, true));
        Assert.False(lease.Release(token));
        Assert.True(lease.ShouldClearOnCombatEnd(false, true, false, true, 7));
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(false, true, false, true, true)]
    [InlineData(true, true, true, true, false)]
    public void UnownedCombatEndClearingKeepsExistingSettings(bool combat, bool forceDisabled, bool clearPreset, bool clearForce, bool expected)
        => Assert.Equal(expected, new CompletionSuppressionLease().ShouldClearOnCombatEnd(combat, forceDisabled, clearPreset, clearForce, 7));

    [Fact]
    public void StaleOwnerCannotReleaseNewCompletionScope()
    {
        var lease = new CompletionSuppressionLease();
        var first = lease.Acquire(7);
        var second = lease.Acquire(8);
        Assert.NotEqual(first, second);
        Assert.False(lease.Release(first));
        Assert.True(lease.Owns(second, 8, true));
        Assert.False(lease.Owns(first, 7, true));
    }
}
