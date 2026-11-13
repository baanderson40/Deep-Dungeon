using Xunit;

namespace BossMod.Tests;

public class DeepDungeonExitConfirmationTests
{
    [Theory]
    [InlineData(1, 570, 131236)]
    [InlineData(2, 780, 131298)]
    [InlineData(1, 780, 0)]
    [InlineData(2, 570, 0)]
    [InlineData(3, 780, 0)]
    public void WarpQuestionIsSpecificToSupportedDungeonWaitingRoom(int dungeon, ushort territory, uint expected)
        => Assert.Equal(expected, DeepDungeonExitConfirmation.WarpQuestionRow((DeepDungeonState.DungeonType)dungeon, territory));

    [Theory]
    [InlineData(true, true, 1000, 1001, false, true)]
    [InlineData(false, true, 1000, 1001, false, false)]
    [InlineData(true, false, 1000, 1001, false, false)]
    [InlineData(true, true, 0, 1001, false, false)]
    [InlineData(true, true, 1000, 999, false, false)]
    [InlineData(true, true, 1000, 10999, false, true)]
    [InlineData(true, true, 1000, 11000, false, false)]
    [InlineData(true, true, 1000, 1001, true, false)]
    public void ConfirmationRequiresFreshOwnedInteractionReadyDialogAndNoPriorSubmission(bool owned, bool ready,
        long armed, long now, bool submitted, bool expected)
        => Assert.Equal(expected, DeepDungeonExitConfirmation.CanConfirm(DeepDungeonState.DungeonType.POTD, 570,
            owned, ready, armed, now, submitted, "localized question", "localized question"));

    [Theory]
    [InlineData("Quitter le Palais des morts ?", "Quitter le Palais des morts ?", true)]
    [InlineData("死者の宮殿から退出しますか？", "死者の宮殿から退出しますか？", true)]
    [InlineData(" Exit Heaven-on-High? ", "Exit Heaven-on-High?", true)]
    [InlineData("Exit Heaven-on-High?", "Exit the Palace of the Dead?", false)]
    [InlineData("Exit Heaven-on-High?", "Abandon duty?", false)]
    [InlineData(null, "Exit Heaven-on-High?", false)]
    [InlineData("", "", false)]
    public void OnlyExactLocalizedQuestionMatches(string? expectedQuestion, string? actualQuestion, bool expected)
        => Assert.Equal(expected, DeepDungeonExitConfirmation.CanConfirm(DeepDungeonState.DungeonType.HOH, 780,
            true, true, 1000, 1001, false, expectedQuestion, actualQuestion));
}
