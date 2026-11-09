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

    private void HandleFloorPathfind(Actor player, AIHints hints)
    {
        if (DesiredRoom.Reason == PathfindReason.Nothing)
            return;

        if (!TryGetCurrentRoomRects(out var rects, out var geometryReason))
        {
            LogNoPathState($"geometry:{geometryReason}", $"room navigation unavailable: {geometryReason}");
            return;
        }
        if (DesiredRoom.Index is < 0 or >= 25 || Palace.Rooms[DesiredRoom.Index] == RoomFlags.None || !ValidRoomBox(rects[DesiredRoom.Index]))
        {
            LogNoPathState($"destination:{DesiredRoom.Index}", $"invalid destination room {DesiredRoom.Index}");
            return;
        }

        var slot = Array.FindIndex(Palace.Party, p => p.EntityId == player.InstanceID);
        if (slot < 0)
            return;

        var destRoom = rects[DesiredRoom.Index];
        if (destRoom.Contains(player.Position))
        {
            LogNoPathState(null, "path available");
            ClearDesiredRoom("destination room reached");
            return;
        }

        var goalWeight = player.InCombat ? 1 : 0.01f;

        // player is in a hallway/bridge near destination room, move closer
        if (PlayerRoom == DesiredRoom.Index)
        {
            LogNoPathState(null, "path available");
            hints.AddGoalZone(p => p.InRect(destRoom.Pos, default(Angle), destRoom.Scale.Z, destRoom.Scale.Z, destRoom.Scale.X) ? goalWeight : 0, $"deepdungeon-room:{DesiredRoom.Index}:{ReasonReadable(DesiredRoom.Reason)}");
            return;
        }

        var path = new FloorPathfind(Palace.Rooms).Pathfind(PlayerRoom, DesiredRoom.Index);
        if (path.Count == 0)
        {
            LogNoPathState($"{PlayerRoom}:{DesiredRoom.Index}", $"no path from room {PlayerRoom} to {DesiredRoom.Index} ({ReasonReadable(DesiredRoom.Reason)})");
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

        hints.AddGoalZone(p => DeepDungeonPolicy.ForwardRoomRoutePriority(p, srcBox.Pos, destBox.Pos, playerProgress, rectWidth, goalWeight),
            $"deepdungeon-route:{PlayerRoom}->{DesiredRoom.Index}:{ReasonReadable(DesiredRoom.Reason)}:via={string.Join(">", path)}:src={srcBox.Room}@({srcBox.Pos.X:F1},{srcBox.Pos.Z:F1})±({srcBox.Scale.X:F1},{srcBox.Scale.Z:F1}):next={destBox.Room}@({destBox.Pos.X:F1},{destBox.Pos.Z:F1})±({destBox.Scale.X:F1},{destBox.Scale.Z:F1}):corridor=forward-progress[{playerProgress:F1},{srcToDestLen:F1}],halfWidth={rectWidth:F1}");
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
