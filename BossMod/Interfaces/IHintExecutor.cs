using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;

namespace BossMod.Interfaces;

public interface IHintExecutor
{
    void Execute();
}

internal sealed class MockHintExecutor : IHintExecutor
{
    public void Execute() { }
}

internal sealed class HintExecutor(WorldState ws, MovementOverride movementOverride, ActionManagerEx amex, AIHints hints) : IHintExecutor
{
    private DateTime _throttleJump;
    private DateTime _throttleInteract;
    private DateTime _throttleFateSync;
    private DateTime _throttleLeaveDuty;
    private ulong _trackedTreasureTarget;
    private bool? _lastTreasureTargetable;
    private bool? _lastTreasureInRange;
    private string? _lastTreasureBlocker;
    private DateTime _firstTreasureRequest;
    private DateTime _lastTreasureRequestSummary;
    private int _treasureRequestsSinceSummary;
    private int _treasureRequestTotal;
    private ActionID _lastObservedPlayerCast;
    private bool? _lastDDInteractionMovementSuppressed;

    public unsafe void Execute()
    {
        movementOverride.DesiredDirection = hints.ForcedMovement;
        movementOverride.MisdirectionThreshold = hints.MisdirectionThreshold;
        movementOverride.DesiredSpinDirection = hints.SpinDirection;

        var targetSystem = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
        SetTarget(hints.ForcedTarget, &targetSystem->Target);
        SetTarget(hints.ForcedFocusTarget, &targetSystem->FocusTarget);

        foreach (var s in hints.StatusesToCancel)
        {
            var res = FFXIVClientStructs.FFXIV.Client.Game.StatusManager.ExecuteStatusOff(s.statusId, s.sourceId != 0 ? (uint)s.sourceId : 0xE0000000);
            Service.Log($"[ExecHints] Canceling status {s.statusId} from {s.sourceId:X} -> {res}");
        }
        if (hints.WantJump && ws.CurrentTime > _throttleJump)
        {
            //Service.Log($"[ExecHints] Jumping...");
            FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance()->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 2);
            _throttleJump = ws.FutureTime(0.1f);
        }

        if (hints.ShouldLeaveDuty && ws.CurrentTime >= _throttleLeaveDuty)
        {
            EventFramework.LeaveCurrentContent(false);
            _throttleLeaveDuty = ws.FutureTime(1.0f);
        }

        var player = ws.Party.Player();
        var interactionTarget = hints.InteractWithTarget;
        var inInteractionRange = CheckInteractRange(player, interactionTarget);
        movementOverride.RecordInteractionMovementSuppression(inInteractionRange, interactionTarget?.InstanceID ?? 0);
        UpdateTreasureInteractionDiagnostics(player, interactionTarget, inInteractionRange);
        if (ws.DeepDungeon.DungeonId != DeepDungeonState.DungeonType.None)
        {
            if (_lastDDInteractionMovementSuppressed != inInteractionRange)
            {
                _lastDDInteractionMovementSuppressed = inInteractionRange;
                Service.Log(inInteractionRange
                    ? $"[AutoDD] movement suppression active for interaction target={interactionTarget?.InstanceID.ToString() ?? "none"}, animationLock={amex.EffectiveAnimationLock}."
                    : $"[AutoDD] movement suppression cleared; interaction target={interactionTarget?.InstanceID.ToString() ?? "none"}.");
            }
        }
        else
            _lastDDInteractionMovementSuppressed = null;
        if (inInteractionRange)
        {
            // many eventobj interactions "immediately" start some cast animation (delayed by server roundtrip), and if we keep trying to move toward the target after sending the interact request, it will be canceled and force us to start over
            movementOverride.DesiredDirection = default;

            if (amex.EffectiveAnimationLock == 0 && ws.CurrentTime >= _throttleInteract)
            {
                FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->InteractWithObject(GetActorObject(interactionTarget), false);
                RecordTreasureInteractionRequest(interactionTarget);
                _throttleInteract = ws.FutureTime(0.1f);
            }
        }

        HandleFateSync();
    }

    private void UpdateTreasureInteractionDiagnostics(Actor? player, Actor? target, bool inRange)
    {
        if (target == null || !DeepDungeonInteractionHistory.IsTreasureInteractionTarget(target.Type, target.OID))
            target = null;

        var targetID = target?.InstanceID ?? 0;
        if (targetID != _trackedTreasureTarget)
        {
            if (_trackedTreasureTarget != 0)
                Service.Log($"[Interact] treasure target abandoned: actor={_trackedTreasureTarget}, totalRequests={_treasureRequestTotal}, outcome unknown (target change/clear is not proof it opened).");
            _trackedTreasureTarget = targetID;
            _lastTreasureTargetable = target?.IsTargetable;
            _lastTreasureInRange = null;
            _lastTreasureBlocker = null;
            _firstTreasureRequest = default;
            _lastTreasureRequestSummary = default;
            _treasureRequestsSinceSummary = 0;
            _treasureRequestTotal = 0;
            _lastObservedPlayerCast = default;
            if (target != null)
                Service.Log($"[Interact] treasure target selected: actor={targetID}, targetable={target.IsTargetable}; actor id correlates this trace with AutoDD candidate logs.");
        }

        if (target == null)
            return;

        if (_lastTreasureTargetable != target.IsTargetable)
        {
            _lastTreasureTargetable = target.IsTargetable;
            Service.Log($"[Interact] treasure targetability changed: actor={targetID}, targetable={target.IsTargetable}; change is observation, not proof of opening.");
        }

        if (_lastTreasureInRange != inRange)
        {
            _lastTreasureInRange = inRange;
            Service.Log($"[Interact] treasure range state changed: actor={targetID}, inRange={inRange}.");
        }

        var blocker = !target.IsTargetable ? "target not targetable"
            : !inRange ? "outside interact range or native object unavailable"
            : amex.EffectiveAnimationLock != 0 ? "animation lock active"
            : null;
        if (_lastTreasureBlocker != blocker)
        {
            _lastTreasureBlocker = blocker;
            if (blocker != null)
                Service.Log($"[Interact] treasure request blocked: actor={targetID}, reason={blocker}.");
            else
                Service.Log($"[Interact] treasure request unblocked: actor={targetID}.");
        }

        if (player?.CastInfo is { } cast && _firstTreasureRequest != default && ws.CurrentTime - _firstTreasureRequest <= TimeSpan.FromSeconds(5) && _lastObservedPlayerCast != cast.Action)
        {
            _lastObservedPlayerCast = cast.Action;
            Service.Log($"[Interact] player cast observed after treasure request: actor={targetID}, action={cast.Action}; relation to interaction is unconfirmed.");
        }
        else if (player?.CastInfo == null)
            _lastObservedPlayerCast = default;
    }

    private void RecordTreasureInteractionRequest(Actor? target)
    {
        if (target == null || !DeepDungeonInteractionHistory.IsTreasureInteractionTarget(target.Type, target.OID))
            return;

        var now = ws.CurrentTime;
        ws.DeepDungeonInteractions.RecordTreasureCall(target.InstanceID, target.OID, (int)ws.DeepDungeon.DungeonId, ws.DeepDungeon.Floor, now);
        if (target.InstanceID != _trackedTreasureTarget)
            return;

        if (_firstTreasureRequest == default)
            _firstTreasureRequest = now;
        ++_treasureRequestsSinceSummary;
        ++_treasureRequestTotal;
        if (DeepDungeonPolicy.ShouldSummarizeInteractionRequests(_lastTreasureRequestSummary, now))
        {
            Service.Log($"[Interact] treasure interact call issued: actor={target.InstanceID}, requestsSinceLastSummary={_treasureRequestsSinceSummary}, firstRequestAge={(now - _firstTreasureRequest).TotalSeconds:F1}s; server outcome unknown.");
            _lastTreasureRequestSummary = now;
            _treasureRequestsSinceSummary = 0;
        }
    }

    private unsafe void SetTarget(Actor? target, FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject** targetPtr)
    {
        if (target == null || !target.IsTargetable)
            return;

        var obj = GetActorObject(target);

        // 50 in-game units is the maximum distance before nameplates stop rendering
        // if you target something and then move further away, the client automatically untargets at 54.5 units
        // targeting a mob that isn't visible is bad UX
        if (ws.Party.Player() is { } player)
        {
            var distSq = (player.Position - target.Position).LengthSq();
            if (distSq < 2500)
                *targetPtr = obj;
        }
    }

    private unsafe bool CheckInteractRange(Actor? player, Actor? target)
    {
        var playerObj = GetActorObject(player);
        var targetObj = GetActorObject(target);
        if (playerObj == null || targetObj == null)
            return false;

        // treasure chests have no client-side interact range check at all; just assume they use the standard "small" range, seems to be accurate from testing
        if (targetObj->ObjectKind is FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.Treasure)
            return player?.DistanceToHitbox(target) <= 2.09f;

        return EventFramework.Instance()->CheckInteractRange(playerObj, targetObj, 1, false);
    }

    private unsafe void HandleFateSync()
    {
        var fm = FateManager.Instance();
        var fate = fm->CurrentFate;
        if (fate == null)
            return;

        var shouldDoSomething = hints.WantFateSync switch
        {
            AIHints.FateSync.Enable => !fm->IsSyncedToFate(fate),
            AIHints.FateSync.Disable => fm->IsSyncedToFate(fate),
            _ => false
        };

        if (shouldDoSomething && ws.CurrentTime >= _throttleFateSync)
        {
            fm->LevelSync();
            _throttleFateSync = ws.FutureTime(0.5f);
        }
    }

    private unsafe FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* GetActorObject(Actor? actor)
    {
        if (actor == null)
            return null;

        var obj = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance()->Objects.IndexSorted[actor.SpawnIndex].Value;
        if (obj == null)
            return null;

        if (obj->EntityId != actor.InstanceID)
            Service.Log($"[ExecHints] Unexpected actor: expected {actor.InstanceID:X} at #{actor.SpawnIndex}, but found {obj->EntityId:X}");

        return obj;
    }
}
