using Xunit;
using CP = BossMod.DeepDungeonContinuationPolicy;

namespace BossMod.Tests;

public class DeepDungeonContinuationTests
{
    [Fact]
    public void UpdatedTargetBeforeBossDefeatChoosesContinuationAt170InsteadOfExit()
    {
        var target = 0;
        Assert.Equal(CP.Intent.Exit, CP.Decide(true, DeepDungeonState.DungeonType.POTD, 170, target));
        target = 200; // Always decide from the current config, not a run-start snapshot.
        var intent = CP.Decide(true, DeepDungeonState.DungeonType.POTD, 170, target);
        Assert.Equal(CP.Intent.Continue, intent);
        Assert.Equal(CP.ExitAction.Continue, CP.RevalidateExit(intent, false, false));
    }

    [Fact]
    public void TargetRaisedAfterExitDecisionButBeforeDispatchRequiresContinuation()
    {
        Assert.Equal(CP.ExitAction.Exit, CP.RevalidateExit(
            CP.Decide(true, DeepDungeonState.DungeonType.POTD, 170, 0), false, false));
        Assert.Equal(CP.ExitAction.Continue, CP.RevalidateExit(
            CP.Decide(true, DeepDungeonState.DungeonType.POTD, 170, 200), false, false));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(175)]
    public void TargetChangeDuringExitPromptCannotSubmitYesOrReplacePendingUi(int target)
    {
        var intent = CP.Decide(true, DeepDungeonState.DungeonType.POTD, 170, target);
        Assert.Equal(CP.ExitAction.Hold, CP.RevalidateExit(intent, true, false));
        // Already-submitted native callbacks cannot be retracted by a later config change.
        Assert.Equal(CP.ExitAction.AwaitTransition, CP.RevalidateExit(intent, true, true));
    }

    [Fact]
    public void ExitRemainsAllowedOnlyForCurrentExitIntentAndInvalidTargetHoldsWithoutUi()
    {
        Assert.Equal(CP.ExitAction.Exit, CP.RevalidateExit(CP.Intent.Exit, false, false));
        Assert.Equal(CP.ExitAction.Exit, CP.RevalidateExit(CP.Intent.Exit, true, false));
        Assert.Equal(CP.ExitAction.Hold, CP.RevalidateExit(CP.Intent.Hold, false, false));
        Assert.Equal(CP.ExitAction.Cancel, CP.RevalidateExit(CP.Intent.None, false, false));
        Assert.Equal(CP.ExitAction.Cancel, CP.RevalidateExit(CP.Intent.None, true, false));
        Assert.Equal(CP.ExitAction.AwaitTransition, CP.RevalidateExit(CP.Intent.None, true, true));
    }

    [Theory]
    [InlineData(true, true, false, false, 10, 15, 0, true)]
    [InlineData(false, true, false, false, 10, 15, 0, false)]
    [InlineData(true, false, false, false, 10, 15, 0, false)]
    [InlineData(true, true, true, false, 10, 15, 0, false)]
    [InlineData(true, true, false, true, 10, 15, 0, false)]
    [InlineData(true, true, false, false, 16, 15, 0, false)]
    [InlineData(true, true, false, false, 10, 15, 10, false)]
    [InlineData(true, true, false, false, 10, float.NaN, 0, false)]
    public void PartyEntryRequiresCurrentLivingReadyMembersWithinLeash(bool present, bool alive, bool combat, bool casting,
        float distance, float maxDistance, float height, bool expected)
        => Assert.Equal(expected, CP.PartyMemberReady(present, alive, combat, casting, distance, maxDistance, height));

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(80, true)]
    [InlineData(200, true)]
    [InlineData(-10, false)]
    [InlineData(1, false)]
    [InlineData(57, false)]
    [InlineData(210, false)]
    public void TargetMustBeZeroOrTenFloorStepWithinPotdLimit(int target, bool expected)
        => Assert.Equal(expected, CP.ValidTarget(target));

    [Theory]
    [InlineData(false, 1, 60, 80, "None")]
    [InlineData(true, 1, 60, 0, "Exit")]
    [InlineData(true, 2, 60, 0, "Exit")]
    [InlineData(true, 1, 60, 80, "Continue")]
    [InlineData(true, 1, 70, 80, "Continue")]
    [InlineData(true, 1, 80, 80, "Exit")]
    [InlineData(true, 1, 80, 60, "Exit")]
    [InlineData(true, 1, 190, 200, "Continue")]
    [InlineData(true, 1, 200, 200, "Exit")]
    [InlineData(true, 2, 60, 80, "Hold")]
    [InlineData(true, 3, 90, 100, "Hold")]
    [InlineData(true, 0, 60, 80, "Hold")]
    [InlineData(true, 1, 0, 80, "Hold")]
    [InlineData(true, 1, 61, 80, "Hold")]
    [InlineData(true, 1, 60, 57, "Hold")]
    public void OnlyConfirmedSupportedBossContextCanSelectContinuation(bool enabled, int dungeon, int cleared, int target, string expected)
        => Assert.Equal(expected, CP.Decide(enabled, (DeepDungeonState.DungeonType)dungeon, cleared, target).ToString());

    [Theory]
    [InlineData(1, 61, 60, true, true)]
    [InlineData(1, 71, 70, true, true)]
    [InlineData(1, 191, 190, true, true)]
    [InlineData(1, 61, 60, false, false)]
    [InlineData(1, 60, 60, true, false)]
    [InlineData(1, 71, 60, true, false)]
    [InlineData(2, 61, 60, true, false)]
    [InlineData(1, 201, 200, true, false)]
    public void SuccessRequiresExactNextPotdSetAndCurrentInitialization(int dungeon, int floor, int cleared, bool initialized, bool expected)
        => Assert.Equal(expected, CP.ExpectedNextSet((DeepDungeonState.DungeonType)dungeon, floor, cleared, initialized));

    [Fact]
    public void MatchingInteractionAndUiCallbacksAdvanceOnlyOnce()
    {
        var session = new CP.Session(60, 80, 1, 123) { EntryIdentity = 42 };
        Assert.False(session.TryArmMenu(42, 1000)); // Merely selecting an object does not arm UI ownership.
        session.Advance(CP.Stage.ApproachingEntry, 1000);
        Assert.False(session.TryArmMenu(43, 1001));
        Assert.True(session.TryArmMenu(42, 1001));
        Assert.False(session.TryArmMenu(42, 1002));
        Assert.True(session.TryAdvanceUi(CP.Stage.AwaitingMenu, 1002));
        Assert.False(session.TryAdvanceUi(CP.Stage.AwaitingMenu, 1003));
        Assert.True(session.TryAdvanceUi(CP.Stage.AwaitingConfirmation, 1003));
        Assert.False(session.TryAdvanceUi(CP.Stage.AwaitingConfirmation, 1004));
        Assert.Equal(CP.Stage.Entering, session.Stage);
    }

    [Fact]
    public void FollowerCannotArmMenuOrSubmitEitherCallback()
    {
        var session = new CP.Session(60, 80, 1, 123) { EntryIdentity = 42, Follower = true };
        session.Advance(CP.Stage.ApproachingEntry, 1000);
        Assert.False(session.TryArmMenu(42, 1001));
        session.Advance(CP.Stage.AwaitingMenu, 1000);
        Assert.False(session.TryAdvanceUi(CP.Stage.AwaitingMenu, 1001));
        session.Advance(CP.Stage.AwaitingConfirmation, 1000);
        Assert.False(session.TryAdvanceUi(CP.Stage.AwaitingConfirmation, 1001));
    }

    [Theory]
    [InlineData(999, false)]
    [InlineData(1000, true)]
    [InlineData(10999, true)]
    [InlineData(11000, false)]
    public void UiEvidenceExpiresAtTenSecondsAndRejectsClockRegression(long now, bool expected)
    {
        var session = new CP.Session(60, 80, 1, 123);
        session.Advance(CP.Stage.AwaitingMenu, 1000);
        Assert.Equal(expected, session.TryAdvanceUi(CP.Stage.AwaitingMenu, now));
    }

    [Fact]
    public void NewRetryGenerationHasNoOldObjectOrUiOwnership()
    {
        var previous = new CP.Session(60, 80, 1, 123) { EntryIdentity = 42 };
        previous.Advance(CP.Stage.ApproachingEntry, 1000);
        Assert.True(previous.TryArmMenu(42, 1001));
        var retry = new CP.Session(previous.ClearedBoss, previous.Target, 2, previous.PresetVersion);
        Assert.Equal(0UL, retry.EntryIdentity);
        Assert.False(retry.TryAdvanceUi(CP.Stage.AwaitingMenu, 1002));
        Assert.NotEqual(previous.Generation, retry.Generation);
        Assert.Equal(60, retry.ClearedBoss);
    }

    [Fact]
    public void DiscoveryClockPausesDuringNoneligibleGapsButUiEvidenceDoesNot()
    {
        var session = new CP.Session(60, 80, 1, 123);
        session.Advance(CP.Stage.DiscoveringEntry, 1000);
        session.ObserveApproachEligibility(1250);
        session.ObserveApproachEligibility(20000);
        Assert.Equal(250, 20000 - session.StageAt);
        session.Advance(CP.Stage.AwaitingMenu, 20000);
        session.ObserveApproachEligibility(40000);
        Assert.False(session.CanSubmit(CP.Stage.AwaitingMenu, 40000));
    }

    [Theory]
    [InlineData("Proceed to the next floor with your current party?", "Proceed to the next floor with your current party?", true)]
    [InlineData("Weiter?", " Weiter? ", true)]
    [InlineData("次の階層へ進みますか？", "次の階層へ進みますか？", true)]
    [InlineData("Proceed to the next floor with your current party?", "Exit the Palace of the Dead?", false)]
    [InlineData("Proceed to the next floor", "Proceed to the next floor with your current party?", false)]
    [InlineData(null, "Proceed", false)]
    [InlineData("", "", false)]
    public void MenuAndConfirmationUseExactNonemptyLocalizedText(string? expectedText, string? actualText, bool expected)
        => Assert.Equal(expected, CP.MatchesText(expectedText, actualText));
}
