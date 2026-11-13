namespace BossMod;

internal static class DeepDungeonContinuationPolicy
{
    internal enum Intent { None, Exit, Continue, Hold }
    internal enum ExitAction { Exit, Continue, Hold, Cancel, AwaitTransition }
    internal enum Stage { WaitingRoom, DiscoveringEntry, ApproachingEntry, AwaitingMenu, AwaitingConfirmation, Entering, FailedHold }
    internal const uint EntryBaseID = 2006012;
    internal const ushort WaitingTerritory = 570;
    internal const long UiTimeoutMs = 10000;
    internal const long EntryTimeoutMs = 60000;
    internal const long PartyTimeoutMs = 180000;
    internal const string QuestionSheet = "content/DeepDungeonMiddle";
    internal const uint QuestionRow = 0;
    internal const int TextColumn = 1;
    internal const string CommandSheet = "content/DeepDungeonEntrance";
    internal const uint CommandRow = 79;

    internal static bool ValidTarget(int target) => target is >= 0 and <= 200 && target % 10 == 0;
    internal static Intent Decide(bool enabled, DeepDungeonState.DungeonType dungeon, int clearedBoss, int target)
        => !enabled ? Intent.None
        : !ValidTarget(target) || clearedBoss <= 0 ? Intent.Hold
        : target == 0 ? Intent.Exit
        : dungeon != DeepDungeonState.DungeonType.POTD || clearedBoss > 200 || clearedBoss % 10 != 0 ? Intent.Hold
        : clearedBoss >= target ? Intent.Exit : Intent.Continue;

    // A target change cannot retract an already submitted Yes or authorize replacing pending UI.
    internal static ExitAction RevalidateExit(Intent intent, bool interactionPending, bool confirmationSubmitted)
        => confirmationSubmitted ? ExitAction.AwaitTransition
        : intent == Intent.None ? ExitAction.Cancel
        : intent == Intent.Exit ? ExitAction.Exit
        : intent == Intent.Continue && !interactionPending ? ExitAction.Continue
        : ExitAction.Hold;

    internal static bool MatchesText(string? expected, string? actual)
        => !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(actual)
            && string.Equals(expected.Trim(), actual.Trim(), StringComparison.Ordinal);

    internal static bool PartyMemberReady(bool present, bool alive, bool combat, bool casting, float distance, float maxDistance, float heightDifference)
        => present && alive && !combat && !casting && float.IsFinite(distance) && distance >= 0
            && float.IsFinite(maxDistance) && maxDistance >= 1 && distance <= maxDistance
            && float.IsFinite(heightDifference) && Math.Abs(heightDifference) <= 3;

    internal static bool ExpectedNextSet(DeepDungeonState.DungeonType dungeon, int floor, int clearedBoss, bool initialized)
        => dungeon == DeepDungeonState.DungeonType.POTD && clearedBoss is > 0 and < 200 && clearedBoss % 10 == 0
            && floor == clearedBoss + 1 && initialized;

    internal sealed class Session(int clearedBoss, int target, long generation, long presetVersion)
    {
        internal readonly int ClearedBoss = clearedBoss;
        internal int Target = target;
        internal readonly long Generation = generation;
        internal readonly long PresetVersion = presetVersion;
        internal Stage Stage = Stage.WaitingRoom;
        internal long StageAt = Environment.TickCount64;
        internal long WaitingAt;
        internal string? Roster;
        internal ulong LeaderContentID;
        internal bool Follower;
        internal ulong EntryIdentity;
        internal bool MenuObserved;
        internal bool ConfirmationObserved;
        internal int InitializedFloor;
        internal long NextUiPollAt;
        private long _lastApproachEligibleTick;
        internal string? LastStatus;

        internal void Advance(Stage stage, long now)
        {
            Stage = stage;
            StageAt = now;
            _lastApproachEligibleTick = now;
        }

        internal void ObserveApproachEligibility(long now)
        {
            // Navigation/discovery clocks pause across non-eligible gaps. The overall party deadline does not.
            if ((Stage is Stage.DiscoveringEntry or Stage.ApproachingEntry) && _lastApproachEligibleTick > 0
                && now - _lastApproachEligibleTick > 500)
                StageAt += now - _lastApproachEligibleTick;
            _lastApproachEligibleTick = now;
        }

        internal bool CanSubmit(Stage expected, long now)
            => !Follower && (expected is Stage.AwaitingMenu or Stage.AwaitingConfirmation)
                && Stage == expected && now >= StageAt && now - StageAt < UiTimeoutMs;

        internal bool TryArmMenu(ulong actualTarget, long now)
        {
            if (Follower || Stage != Stage.ApproachingEntry || EntryIdentity == 0 || actualTarget != EntryIdentity)
                return false;
            Advance(Stage.AwaitingMenu, now);
            return true;
        }

        internal bool TryAdvanceUi(Stage expected, long now)
        {
            if (!CanSubmit(expected, now))
                return false;
            Advance(expected == Stage.AwaitingMenu ? Stage.AwaitingConfirmation : Stage.Entering, now);
            return true;
        }
    }
}
