namespace BossMod;

internal static class DeepDungeonExitConfirmation
{
    internal const long PromptTimeoutMs = 10000;
    internal const long TransitionTimeoutMs = 30000;

    internal static uint WarpQuestionRow(DeepDungeonState.DungeonType dungeon, ushort territory)
        => (dungeon, territory) switch
        {
            (DeepDungeonState.DungeonType.POTD, 570) => 131236,
            (DeepDungeonState.DungeonType.HOH, 780) => 131298,
            _ => 0,
        };

    internal static bool CanConfirm(DeepDungeonState.DungeonType dungeon, ushort territory, bool ownedInteraction,
        bool ready, long armedAt, long now, bool submitted, string? expected, string? actual)
        => WarpQuestionRow(dungeon, territory) != 0 && ownedInteraction && ready && !submitted
            && armedAt > 0 && now >= armedAt && now - armedAt < PromptTimeoutMs
            && !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(actual)
            && string.Equals(expected.Trim(), actual.Trim(), StringComparison.Ordinal);
}
