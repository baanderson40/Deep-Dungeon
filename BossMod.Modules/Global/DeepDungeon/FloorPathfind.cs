using static FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon;

namespace BossMod.Global.DeepDungeon;

public enum Direction
{
    North,
    South,
    East,
    West
}

// neat feature of deep dungeons - there is only one path from any room to any other room (no loops) and the grid is so small that brute forcing is basically free!
internal class FloorPathfind(ReadOnlySpan<RoomFlags> Map)
{
    public readonly RoomFlags[] Map = Map.ToArray();

    private readonly bool[] Explored = new bool[25];

    private readonly Queue<List<int>> Queue = new();

    public List<int> Pathfind(int startRoom, int destRoom)
    {
        if (Map.Length != Explored.Length || startRoom is < 0 or >= 25 || destRoom is < 0 or >= 25 || Map[startRoom] == RoomFlags.None || Map[destRoom] == RoomFlags.None)
            return [];
        if (startRoom == destRoom)
            return [];

        Explored[startRoom] = true;
        Queue.Enqueue([startRoom]);
        while (Queue.TryDequeue(out var v))
        {
            if (v[^1] == destRoom)
            {
                v.RemoveAt(0);
                return v;
            }
            foreach (var w in Edges(v[^1]))
            {
                if (!Explored[w])
                {
                    Explored[w] = true;
                    Queue.Enqueue([.. v, w]);
                }
            }
        }

        return [];
    }

    private IEnumerable<int> Edges(int roomIndex)
    {
        var md = Map[roomIndex];
        if (roomIndex >= 5 && md.HasFlag(RoomFlags.ConnectionN))
            yield return roomIndex - 5;
        if (roomIndex < 20 && md.HasFlag(RoomFlags.ConnectionS))
            yield return roomIndex + 5;
        if (roomIndex % 5 > 0 && md.HasFlag(RoomFlags.ConnectionW))
            yield return roomIndex - 1;
        if (roomIndex % 5 < 4 && md.HasFlag(RoomFlags.ConnectionE))
            yield return roomIndex + 1;
    }
}

abstract partial class AutoClear : ZoneModule
{
    enum PathfindReason
    {
        Nothing,
        ClosedPassage,
        Everything,
        Hunting,
        OpenPassage,
        Hoard,
        Candelabra,
        Manual
    }

    static string ReasonReadable(PathfindReason r) => r switch
    {
        PathfindReason.ClosedPassage => "revealing passage",
        PathfindReason.Everything => "revealing the map",
        PathfindReason.Hunting => "hunting mobs for progress",
        PathfindReason.OpenPassage => "heading for passage",
        PathfindReason.Hoard => "heading for hoard",
        PathfindReason.Candelabra => "heading for candelabra",
        PathfindReason.Manual => "navigating manually",
        _ => ""
    };

    private RoomBox PlayerRoomBox;
    private int PlayerRoom;

    private (int Index, PathfindReason Reason) DesiredRoom;
    private string? _lastNoPathState;
    private int _fullClearRevealWaitRoom;
    private DateTime _fullClearRevealWaitStarted;
    private bool _fullClearRevealStallLogged;
    private readonly DeepDungeonRoomRejoinPolicy _roomRejoin = new();
    private bool _roomRejoinOwnsFrame;
    private bool _roomRejoinHolding;
    private WPos _roomRejoinHoldAnchor;
    private long _roomRejoinLoggedGeneration;
    private string? _roomRejoinNotificationContext;
    private bool _roomRejoinFailureNotified;

    private void PickARoom(Actor player, AIHints hints)
    {
        // Manual destination takes precedence over automatic objective selection.
        if (DesiredRoom.Reason == PathfindReason.Manual)
            return;

        if (DesiredRoom.Reason == PathfindReason.Hunting && (Palace.PassageActive || player.InCombat))
            ClearDesiredRoom(Palace.PassageActive ? "passage became active" : "entered combat");

        if (DesiredRoom.Reason == PathfindReason.ClosedPassage && Palace.Rooms.Any(r => r.HasFlag(RoomFlags.Passage) && r.HasFlag(RoomFlags.Revealed)))
            ClearDesiredRoom("passage was revealed");

        if (DesiredRoom.Reason == PathfindReason.Everything)
        {
            var roomFlags = DesiredRoom.Index is >= 0 and < 25 ? Palace.Rooms[DesiredRoom.Index] : RoomFlags.None;
            var roomActive = roomFlags != RoomFlags.None;
            var roomRevealed = roomFlags.HasFlag(RoomFlags.Revealed);
            if (!DeepDungeonPolicy.ShouldKeepFullClearDestination(_config.FullClear, roomActive, roomRevealed))
                ClearDesiredRoom("full-clear objective no longer pending");
        }

        if (!TryGetCurrentRoomRects(out var rects, out _))
            return;

        // Once the hoard is revealed, keep it as the floor objective until collected.
        // Otherwise passage/full-clear selection can pull us away, then the still-present
        // indicator selects the hoard room again and creates a room-objective loop.
        if (Palace.Progress.HoardCurrentFloor
            && World.Actors.FirstOrDefault(a => a.OID == (uint)OID.BandedCofferIndicator) is { } activeHoard
            && TryFindClosestRoom(activeHoard.Position, out var activeHoardRoom))
        {
            if (DesiredRoom.Reason is not (PathfindReason.Nothing or PathfindReason.Hoard))
                ClearDesiredRoom("hoard objective takes priority");
            if (DesiredRoom.Reason == PathfindReason.Nothing)
            {
                var roomActive = activeHoardRoom.Index > 0 && activeHoardRoom.Index < Palace.Rooms.Length && Palace.Rooms[activeHoardRoom.Index] != RoomFlags.None;
                var playerInsideRoom = roomActive && rects[activeHoardRoom.Index].Contains(player.Position);
                if (DeepDungeonPolicy.ShouldSelectRoomDestination(roomActive, true, playerInsideRoom))
                    SetDesiredRoom((activeHoardRoom.Index, PathfindReason.Hoard), "hoard indicator");
            }
            return;
        }

        if (DesiredRoom.Index > 0)
            return;

        var destination = default((int Index, PathfindReason Reason));
        var destinationCause = "";
        void Consider(int room, PathfindReason reason, string cause, bool objectivePending = true)
        {
            var roomActive = room >= 0 && room < Palace.Rooms.Length && Palace.Rooms[room] != RoomFlags.None;
            var playerInsideRoom = roomActive && rects[room].Contains(player.Position);
            if (room > 0 && DeepDungeonPolicy.ShouldSelectRoomDestination(roomActive, objectivePending, playerInsideRoom))
            {
                destination = (room, reason);
                destinationCause = cause;
            }
        }

        if (Palace.PassageActive && !player.InCombat)
            Consider(Array.FindIndex(Palace.Rooms, d => d.HasFlag(RoomFlags.Passage)), PathfindReason.OpenPassage, "passage active");

        if (_config.FullClear)
        {
            var unexplored = Enumerable.Range(1, Palace.Rooms.Length - 1).FirstOrDefault(i => Palace.Rooms[i] != RoomFlags.None && !Palace.Rooms[i].HasFlag(RoomFlags.Revealed) && !rects[i].Contains(player.Position));
            Consider(unexplored, PathfindReason.Everything, "full-clear unexplored room");
        }

        if (!Palace.PassageActive && !player.InCombat && hints.PotentialTargets.MinBy(t => (t.Actor.Position - player.Position).LengthSq()) is { } closestMob && TryFindClosestRoom(closestMob.Actor.Position, out var mobRoom))
        {
            if (mobRoom.Index != PlayerRoom)
                Consider(mobRoom.Index, PathfindReason.Hunting, "nearest mob");
        }

        var unrevealedPassage = Enumerable.Range(1, Palace.Rooms.Length - 1).FirstOrDefault(i => Palace.Rooms[i].HasFlag(RoomFlags.Passage) && !Palace.Rooms[i].HasFlag(RoomFlags.Revealed) && !rects[i].Contains(player.Position));
        Consider(unrevealedPassage, PathfindReason.ClosedPassage, "unrevealed passage");

        // TODO: figure out if content director has a flag for the candelabra disappearing
        var unrevealedDevo = Enumerable.Range(1, Palace.Rooms.Length - 1).FirstOrDefault(i => Palace.Rooms[i].HasFlag((RoomFlags)0x100) && !Palace.Rooms[i].HasFlag(RoomFlags.Revealed) && !rects[i].Contains(player.Position));
        Consider(unrevealedDevo, PathfindReason.Candelabra, "unrevealed candelabra");

        if (Palace.Progress.HoardCurrentFloor && World.Actors.FirstOrDefault(a => a.OID == (uint)OID.BandedCofferIndicator) is { } hoard && TryFindClosestRoom(hoard.Position, out var hoardRoom))
            Consider(hoardRoom.Index, PathfindReason.Hoard, "hoard indicator");

        if (destination.Reason != PathfindReason.Nothing)
            SetDesiredRoom(destination, destinationCause);
    }

    private string? RoomRejoinContext => DesiredRoom.Reason != PathfindReason.Nothing
        && DesiredRoom.Index is >= 0 and < 25 && Palace.Rooms[DesiredRoom.Index] != RoomFlags.None
            ? $"{Palace.DungeonId}/{Palace.Floor}/{World.CurrentZone}/{DesiredRoom.Index}/{DesiredRoom.Reason}" : null;

    private void HandleFloorPathfind(Actor player, AIHints hints, DeepDungeonRoomRejoinPolicy.NavigationOwner owner)
    {
        if (World.DeepDungeon.RoomRejoinRetryRequested)
        {
            World.DeepDungeon.RoomRejoinRetryRequested = false;
            ResetRoomRejoin();
        }
        if (DesiredRoom.Reason == PathfindReason.Nothing)
            return;
        if (owner == DeepDungeonRoomRejoinPolicy.NavigationOwner.Manual)
            ResetRoomRejoin();
        else
            _roomRejoin.Bind(RoomRejoinContext);

        if (!TryGetCurrentRoomRects(out var rects, out var geometryReason))
        {
            LogNoPathState($"geometry:{geometryReason}", $"room navigation unavailable: {geometryReason}");
            _roomRejoin.Pause(Environment.TickCount64);
            return;
        }
        if (DesiredRoom.Index is < 0 or >= 25 || Palace.Rooms[DesiredRoom.Index] == RoomFlags.None || !ValidRoomBox(rects[DesiredRoom.Index]))
        {
            LogNoPathState($"destination:{DesiredRoom.Index}", $"invalid destination room {DesiredRoom.Index}");
            ResetRoomRejoin();
            return;
        }

        var slot = Array.FindIndex(Palace.Party, p => p.EntityId == player.InstanceID);
        if (slot < 0)
            return;

        var destRoom = rects[DesiredRoom.Index];
        if (destRoom.Contains(player.Position))
        {
            ResetRoomRejoin(); // actual destination-room entry supersedes any intermediate rejoin
            var roomFlags = Palace.Rooms[DesiredRoom.Index];
            var fullClearPending = !DeepDungeonPolicy.ShouldClearReachedRoomDestination(
                DesiredRoom.Reason == PathfindReason.Everything,
                _config.FullClear,
                roomFlags != RoomFlags.None,
                roomFlags.HasFlag(RoomFlags.Revealed));
            if (fullClearPending)
            {
                if (_fullClearRevealWaitRoom != DesiredRoom.Index)
                {
                    _fullClearRevealWaitRoom = DesiredRoom.Index;
                    _fullClearRevealWaitStarted = World.CurrentTime;
                    _fullClearRevealStallLogged = false;
                    Service.Log($"[AutoDD] full-clear room entered but map reveal is pending: room={DesiredRoom.Index}, floor={Palace.Floor}; holding objective and moving toward room interior.");
                }

                if (!_fullClearRevealStallLogged && World.CurrentTime - _fullClearRevealWaitStarted >= TimeSpan.FromSeconds(20))
                {
                    _fullClearRevealStallLogged = true;
                    Service.Log($"[AutoDD] full-clear reveal stalled: room={DesiredRoom.Index}, floor={Palace.Floor}, wait={World.CurrentTime - _fullClearRevealWaitStarted}; objective retained to prevent passage/full-clear oscillation.");
                }

                // Crossing the room bounds does not prove the map reveal has arrived. Keep this
                // objective and gently move toward the interior while waiting for authoritative map state.
                var roomCenter = destRoom.Pos;
                AddRoomNavigationGoal(hints, owner, p => p.InCircle(roomCenter, 3) ? 0.02f : 0,
                    $"deepdungeon-fullclear-interior:{DesiredRoom.Index}", $"{RoomRejoinContext}/interior");
                return;
            }

            LogNoPathState(null, "path available");
            ClearDesiredRoom("destination room reached");
            return;
        }

        var goalWeight = player.InCombat ? 1 : 0.01f;
        var manualMovement = DeepDungeonPartyPolicy.RecentManualMovement(hints.DeepDungeonCoordinationManualAt, World.CurrentTime);
        var suspendedRejoin = manualMovement || player.InCombat || player.CastInfo != null || !Utils.IsScreenReady()
            || hints.ForcedMovement != null || player.PendingKnockbacks.Count > 0 || World.Client.Flying
            || hints.InteractWithTarget != null || hints.InteractWithRawTarget != null
            || Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInQuestEvent];
        // player is in a hallway/bridge near destination room, move closer
        if (PlayerRoom == DesiredRoom.Index)
        {
            _roomRejoin.Pause(Environment.TickCount64);
            LogNoPathState(null, "path available");
            AddRoomNavigationGoal(hints, owner,
                p => p.InRect(destRoom.Pos, default(Angle), destRoom.Scale.Z, destRoom.Scale.Z, destRoom.Scale.X) ? goalWeight : 0,
                $"deepdungeon-room:{DesiredRoom.Index}:{ReasonReadable(DesiredRoom.Reason)}", $"{RoomRejoinContext}/final-approach");
            return;
        }

        var path = new FloorPathfind(Palace.Rooms).Pathfind(PlayerRoom, DesiredRoom.Index);
        if (path.Count == 0)
        {
            LogNoPathState($"{PlayerRoom}:{DesiredRoom.Index}", $"no path from room {PlayerRoom} to {DesiredRoom.Index} ({ReasonReadable(DesiredRoom.Reason)})");
            _roomRejoin.Pause(Environment.TickCount64);
            return;
        }
        LogNoPathState(null, "path available");

        var destBox = rects[path[0]];
        var srcBox = PlayerRoomBox;

        var srcToDest = destBox.Pos - srcBox.Pos;
        var srcToDestLen = srcToDest.Length();
        if (srcToDestLen <= 0.01f)
            return;
        var playerPos = player.Position;
        var routeDirection = srcToDest / srcToDestLen;
        var playerProgress = (playerPos - srcBox.Pos).Dot(routeDirection);
        var rectWidth = 10f;

        var routeKey = $"{RoomRejoinContext}/segment:{PlayerRoom}>{path[0]}";
        var now = Environment.TickCount64;
        var feedback = hints.DeepDungeonRoomRouteResult;
        var routeFailed = DeepDungeonRoomRejoinPolicy.FreshFailedRoute(routeKey, feedback.Key, now, feedback.At, feedback.Failed);
        var rejoinOwns = _roomRejoin.Step(now, RoomRejoinContext, owner, !suspendedRejoin, routeFailed,
            playerPos, srcBox.Pos, srcBox.Size, destBox.Pos, destBox.Size);
        if (rejoinOwns)
        {
            if (ApplyRoomRejoinGoals(player, hints, false, manualMovement, goalWeight))
                return;
        }
        else
            World.DeepDungeon.RoomRejoinStatus = _roomRejoin.Attempts == 0 ? null
                : owner != DeepDungeonRoomRejoinPolicy.NavigationOwner.RoomRoute || suspendedRejoin
                    ? "Room-route recovery paused for another owner; budget retained"
                    : "Rejoined corridor; verifying room-route progress, budget retained";

        AddRoomNavigationGoal(hints, owner,
            p => DeepDungeonPolicy.ForwardRoomRoutePriority(p, srcBox.Pos, destBox.Pos, playerProgress, rectWidth, goalWeight),
            $"deepdungeon-route:{PlayerRoom}->{DesiredRoom.Index}:{ReasonReadable(DesiredRoom.Reason)}:via={string.Join(">", path)}:src={srcBox.Room}@({srcBox.Pos.X:F1},{srcBox.Pos.Z:F1})±({srcBox.Scale.X:F1},{srcBox.Scale.Z:F1}):next={destBox.Room}@({destBox.Pos.X:F1},{destBox.Pos.Z:F1})±({destBox.Scale.X:F1},{destBox.Scale.Z:F1}):corridor=forward-progress[{playerProgress:F1},{srcToDestLen:F1}],halfWidth={rectWidth:F1}", routeKey);
    }

    private void AddRoomNavigationGoal(AIHints hints, DeepDungeonRoomRejoinPolicy.NavigationOwner owner,
        Func<WPos, float> goal, string diagnosticOwner, string routeKey)
    {
        // Even weaker room attraction adds to actor priority. At a room boundary this can
        // alternate winning endpoints while the actor itself stays selected. One owner, one objective.
        if (!DeepDungeonRoomRejoinPolicy.AllowsRoomGoal(owner))
            return;
        hints.AddGoalZone(goal, diagnosticOwner);
        hints.DeepDungeonPartyNavigationKey = AutoNavigationKey($"{owner}:{routeKey}");
        if (owner == DeepDungeonRoomRejoinPolicy.NavigationOwner.RoomRoute)
        {
            hints.DeepDungeonRoomRouteKey = routeKey;
            hints.DeepDungeonRoomRouteGoal = goal;
        }
    }

    private void ResetRoomRejoin()
    {
        _roomRejoin.Reset();
        _roomRejoinOwnsFrame = _roomRejoinHolding = _roomRejoinFailureNotified = false;
        World.DeepDungeon.RoomRejoinStatus = null;
    }

    private bool ApplyRoomRejoinGoals(Actor player, AIHints hints, bool suspended, bool manual, float weight)
    {
        if (_roomRejoinNotificationContext != _roomRejoin.Context || _roomRejoin.Attempts == 0)
        {
            _roomRejoinNotificationContext = _roomRejoin.Context;
            _roomRejoinFailureNotified = false;
        }
        if (_roomRejoinLoggedGeneration != _roomRejoin.Generation)
        {
            _roomRejoinLoggedGeneration = _roomRejoin.Generation;
            _roomRejoinHolding = false;
            if (_roomRejoin.Attempts > 0)
            {
                var summary = $"room-route rejoin: context={_roomRejoin.Context},phase={_roomRejoin.State},attempt={_roomRejoin.Attempts}/3,anchor={_roomRejoin.Anchor},lateral={DeepDungeonRoomRejoinPolicy.LateralDistance(player.Position, _roomRejoin.Source, _roomRejoin.Next):F2},source={_roomRejoin.Source},next={_roomRejoin.Next}; room bounds are not traversability evidence";
                hints.DeepDungeonDiagnosticRequest = summary;
                Service.Log($"[AutoDD] {summary}.");
            }
        }
        if (!_roomRejoin.OwnsNavigation)
        {
            World.DeepDungeon.RoomRejoinStatus = _roomRejoin.Attempts > 0 ? "Rejoined corridor; verifying forward progress, budget retained" : null;
            return false;
        }
        _roomRejoinOwnsFrame = true;
        hints.DeepDungeonPartyNavigationKey = PartyNavigationKey($"room-rejoin:{_roomRejoin.Context}:{_roomRejoin.Generation}:suspended:{suspended}");
        if (_roomRejoin.State == DeepDungeonRoomRejoinPolicy.Phase.FailedHold || suspended)
        {
            if (!_roomRejoinHolding || manual)
            {
                _roomRejoinHolding = true;
                _roomRejoinHoldAnchor = player.Position;
            }
            hints.AddGoalZone(AIHints.GoalSingleTarget(_roomRejoinHoldAnchor, 1, 1), "deepdungeon-room-rejoin-hold");
            hints.DeepDungeonIntentionalHoldReason = _roomRejoin.State == DeepDungeonRoomRejoinPolicy.Phase.FailedHold
                ? $"room-route rejoin failed; context={_roomRejoin.Context},attempts={_roomRejoin.Attempts}/3; bounded recovery stopped"
                : "room-route rejoin suspended for manual input, combat, casting, readiness, or forced movement";
        }
        else
        {
            _roomRejoinHolding = false;
            hints.AddGoalZone(DeepDungeonRoomRejoinPolicy.RejoinGoal(player.Position, _roomRejoin.Anchor, weight),
                $"deepdungeon-room-rejoin:{DesiredRoom.Index}:attempt:{_roomRejoin.Attempts}");
        }
        World.DeepDungeon.RoomRejoinStatus = _roomRejoin.State == DeepDungeonRoomRejoinPolicy.Phase.FailedHold
            ? "STOPPED: corridor re-entry failed; move safely into the route or explicitly retry"
            : suspended ? "Corridor re-entry suspended; budget retained" : $"Rejoining corridor; attempt {_roomRejoin.Attempts}/3";
        if (_roomRejoin.State == DeepDungeonRoomRejoinPolicy.Phase.FailedHold && !_roomRejoinFailureNotified)
        {
            _roomRejoinFailureNotified = true;
            Service.ChatMessage($"AutoDD: room {DesiredRoom.Index} corridor re-entry failed; holding. Use manual movement or Retry room-route recovery.");
        }
        return true;
    }

    private void LogNoPathState(string? state, string message)
    {
        if (_lastNoPathState == state)
            return;
        _lastNoPathState = state;
        if (state != null)
            Service.Log($"[AutoDD] {message}; dungeon={Palace.DungeonId}, floor={Palace.Floor}.");
        else if (message == "path available")
            Service.Log($"[AutoDD] path available again; dungeon={Palace.DungeonId}, floor={Palace.Floor}.");
    }
}
