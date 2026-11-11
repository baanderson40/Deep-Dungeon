namespace BossMod.Services;

public interface IMovementOverride
{
    bool IsMoving();
    bool IsMoveRequested();
    bool IsManualMoveRequested();
    bool IsForceUnblocked();

    WDir UserMove { get; }
}

internal sealed class MockMovementOverride : IMovementOverride
{
    public bool IsMoving() => false;
    public bool IsMoveRequested() => false;
    public bool IsManualMoveRequested() => false;
    public bool IsForceUnblocked() => false;

    public WDir UserMove => default;
}
