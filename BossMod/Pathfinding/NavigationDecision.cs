using System.Numerics.Tensors;
using System.Threading.Tasks;

namespace BossMod.Pathfinding;

// utility for selecting player's navigation target
// there are several goals that navigation has to meet, in following rough priority
// 1. stay away from aoes; tricky thing is that sometimes it is ok to temporarily enter aoe, if we're sure we'll exit it in time
// 2. maintain uptime - this is represented by being in specified range of specified target, and not moving to interrupt casts unless needed
// 3. execute positionals - this is strictly less important than points above, we only do that if we can meet other conditions
// 4. be in range of healers - even less important, but still nice to do
public struct NavigationDecision
{
    // context that allows reusing large memory allocations
    public class Context
    {
        public List<float> ScratchG = [];
        public BitArray ScratchD = new(0);
        public Map Map = new();
        public ThetaStar ThetaStar = new();
    }

    public WPos? Destination;
    public WPos? NextWaypoint;
    public WPos? PathfindDestination;
    public WPos? SearchGoalPosition;
    public WPos PathfindMapCenter;
    public WDir PathfindMapLocalZDivRes;
    public float PathfindMapResolution;
    public int PathfindMapWidth;
    public int PathfindMapHeight;
    //public float NextTurn; // > 0 if we turn left after reaching first waypoint, < 0 if we turn right, 0 otherwise (no more waypoints)
    public float LeewaySeconds; // can be used for finishing casts / slidecasting etc.
    public float TimeToGoal;

    public bool HasIntermediateWaypoint => NextWaypoint != null && Destination == PathfindDestination;

    public (int x, int y) GridPosition(WPos position)
    {
        var offset = position - PathfindMapCenter;
        var fracX = PathfindMapWidth / 2 + offset.Dot(PathfindMapLocalZDivRes.OrthoL());
        var fracY = PathfindMapHeight / 2 + offset.Dot(PathfindMapLocalZDivRes);
        return ((int)MathF.Floor(fracX), (int)MathF.Floor(fracY));
    }

    // Intermediate waypoints are validated segment endpoints, not stopping goals. Keep
    // moving almost to the endpoint so NormalMovement's arrival deadzone cannot strand
    // the player before the next path segment is selected.
    public static float DestinationArrivalRadius(bool hasNextWaypoint) => hasNextWaypoint ? 0.01f : 0.1f;

    public static bool ReachedDestination(float distanceSq, bool hasNextWaypoint)
    {
        var radius = DestinationArrivalRadius(hasNextWaypoint);
        return distanceSq <= radius * radius;
    }

    public TimeSpan RasterizeTime;
    public TimeSpan PathfindTime;
    public TimeSpan TotalTime;
    public string? PathfindDiagnostic;
    public DateTime PathfindDiagnosticAt;
    public long Sequence;
    public DateTime RequestedAt;
    public WPos RequestedPosition;
    public float RequestedHeightY;
    public double BuildDurationMs;
    public double DiagnosticDurationMs;
    public DateTime BuildCompletedAt;
    public DateTime DiagnosticCompletedAt;

    public const float CushionDepriority = 0.5f; // should be lower than any priority specifically added by rotation modules

    // factors to account for:
    // 1. ping (of course)
    // 2. snapshot delay (probably 300ms for NPC casts, see NPCFinishDelay)
    // 3. unknown (variable) amount of delay between movement packet being sent and server deciding to update our position; can be bypassed (kind of) by forcing a position update through various means
    //  - jumping (has its own issues; player gets stuck in the air for a bit, you can't steer while jumping, etc)
    //  - sending position update packet (not an option, can't be done in the vanilla client, so it doesn't match our philosophy)
    public static float CushionFromRTT(float rtt) => rtt + 0.5f + ActorCastInfo.NPCFinishDelay;

    public static NavigationDecision Build(Context ctx, DateTime currentTime, AIHints hints, WPos playerPosition, float playerSpeed, float rtt, float spaceCushion, bool useOldRasterizer = false, bool collectDiagnostics = false, long sequence = 0, float requestedHeightY = 0, WPos? comparisonGoal = null)
    {
        var startTime = DateTime.Now;

        hints.InitPathfindMap(ctx.Map);
        if (hints.ForbiddenZones.Count > 0)
        {
            if (useOldRasterizer)
                RasterizeForbiddenZonesOld(ctx.Map, hints.ForbiddenZones, currentTime, ctx.ScratchG, ctx.ScratchD, rtt, spaceCushion);
            else
                RasterizeForbiddenZones(ctx.Map, playerPosition, hints.ForbiddenZones, currentTime, ctx.ScratchG, ctx.ScratchD, rtt, spaceCushion);
        }

        var emergencyMode = false;
        var (pX, pZ) = ctx.Map.WorldToGrid(playerPosition);
        if (pX >= 0 && pX < ctx.Map.Width && pZ >= 0 && pZ < ctx.Map.Height)
            emergencyMode = ctx.Map.PixelMaxG[ctx.Map.GridToIndex(pX, pZ)] == 0;

        if (hints.GoalZones.Count > 0 && !emergencyMode)
            RasterizeGoalZones(ctx.Map, hints.GoalZones, spaceCushion > 0);
        else if (spaceCushion > 0)
            AddCushion(ctx.Map);

        var rasterFinish = DateTime.Now;

        // execute pathfinding
        ctx.ThetaStar.Start(ctx.Map, playerPosition, 1.0f / playerSpeed);
        var bestNodeIndex = ctx.ThetaStar.Execute();
        ref var bestNode = ref ctx.ThetaStar.NodeByIndex(bestNodeIndex);
        var waypoints = GetFirstWaypoints(ctx.ThetaStar, bestNodeIndex);
        var finishTime = DateTime.Now;
        var searchGoalPosition = ctx.ThetaStar.CellCenter(bestNodeIndex);
        var diagnostic = collectDiagnostics ? BuildPathfindDiagnostic(ctx, hints, playerPosition, requestedHeightY, waypoints.first, searchGoalPosition, comparisonGoal, currentTime, emergencyMode, bestNodeIndex) : null;
        var diagnosticFinish = DateTime.Now;
        return new NavigationDecision() { Destination = waypoints.first, NextWaypoint = waypoints.second, PathfindDestination = waypoints.first, SearchGoalPosition = searchGoalPosition, PathfindMapCenter = ctx.Map.Center, PathfindMapLocalZDivRes = ctx.Map.LocalZDivRes, PathfindMapResolution = ctx.Map.Resolution, PathfindMapWidth = ctx.Map.Width, PathfindMapHeight = ctx.Map.Height, LeewaySeconds = bestNode.PathLeeway, TimeToGoal = bestNode.GScore, PathfindTime = finishTime - rasterFinish, RasterizeTime = rasterFinish - startTime, TotalTime = finishTime - startTime, PathfindDiagnostic = diagnostic, PathfindDiagnosticAt = collectDiagnostics ? currentTime : default, Sequence = sequence, RequestedAt = currentTime, RequestedPosition = playerPosition, RequestedHeightY = requestedHeightY, BuildDurationMs = (finishTime - startTime).TotalMilliseconds, DiagnosticDurationMs = (diagnosticFinish - finishTime).TotalMilliseconds, BuildCompletedAt = finishTime, DiagnosticCompletedAt = diagnosticFinish };
    }

    public static Task<NavigationDecision> BuildAsync(Context ctx, DateTime currentTime, AIHints hints, WPos playerPos, float playerSpeed, float rtt, float spaceCushion, bool collectDiagnostics = false, long sequence = 0, float requestedHeightY = 0, WPos? comparisonGoal = null)
    {
        var hintsCopy = new AIHints()
        {
            PathfindMapBounds = hints.PathfindMapBounds,
            PathfindMapCenter = hints.PathfindMapCenter,
            PathfindMapObstacles = hints.PathfindMapObstacles,
            TemporaryObstacles = [.. hints.TemporaryObstacles],
            Portals = [.. hints.Portals],
            ForbiddenZones = [.. hints.ForbiddenZones],
            GoalZones = [.. hints.GoalZones],
            GoalZoneOwners = [.. hints.GoalZoneOwners],
        };
        return Task.Run(() => Build(ctx, currentTime, hintsCopy, playerPos, playerSpeed, rtt, spaceCushion, collectDiagnostics: collectDiagnostics, sequence: sequence, requestedHeightY: requestedHeightY, comparisonGoal: comparisonGoal));
    }

    private static string BuildPathfindDiagnostic(Context ctx, AIHints hints, WPos playerPosition, float playerHeightY, WPos? firstWaypoint, WPos searchGoalPosition, WPos? comparisonGoal, DateTime currentTime, bool emergencyMode, int bestNodeIndex)
    {
        var map = ctx.Map;
        var startGridRaw = map.WorldToGrid(playerPosition);
        var startInBounds = map.InBounds(startGridRaw.x, startGridRaw.y);
        var startIndex = ctx.ThetaStar.StartNodeIndex;
        var startGrid = map.IndexToGrid(startIndex);
        var startMaxG = map.PixelMaxG[startIndex];
        var startPriority = map.PixelPriority[startIndex];
        var positivePriorityCells = 0;
        var safePriorityCells = 0;
        var reachablePriorityCells = 0;
        var reachableMaxPriorityCells = 0;
        var blockedCells = 0;

        var numPixels = map.Width * map.Height;
        for (var i = 0; i < numPixels; ++i)
        {
            var maxG = map.PixelMaxG[i];
            if (maxG != float.MaxValue)
                ++blockedCells;
            var priority = map.PixelPriority[i];
            if (priority <= 0)
                continue;
            ++positivePriorityCells;
            if (maxG == float.MaxValue)
                ++safePriorityCells;
            if (ctx.ThetaStar.NodeByIndex(i).OpenHeapIndex != 0)
            {
                ++reachablePriorityCells;
                if (priority == map.MaxPriority)
                    ++reachableMaxPriorityCells;
            }
        }

        var visited = ctx.ThetaStar.CountVisitedGoalCells();
        var goalCoverage = new List<string>(Math.Min(hints.GoalZones.Count, 8));
        string Owner(int index) => hints.GoalZoneOwners.FirstOrDefault(o => o.goalIndex == index).owner ?? "unknown";
        for (var zoneIndex = 0; zoneIndex < Math.Min(hints.GoalZones.Count, 8); ++zoneIndex)
        {
            var zone = hints.GoalZones[zoneIndex];
            var covered = 0;
            var maxScore = 0f;
            foreach (var (_, _, point) in map.EnumeratePixels())
            {
                var score = zone(point);
                if (score > 0)
                    ++covered;
                maxScore = MathF.Max(maxScore, score);
            }
            var selectedScore = zone(searchGoalPosition);
            var priorScore = comparisonGoal is { } prior ? zone(prior) : float.NaN;
            goalCoverage.Add($"{zoneIndex}/{Owner(zoneIndex)}:{covered}/{numPixels}@{maxScore:F2}/winner:{selectedScore:F3}/prior:{(comparisonGoal == null ? "n/a" : priorScore.ToString("F3"))}");
        }

        var forbiddenCoverage = new List<string>(Math.Min(hints.ForbiddenZones.Count, 8));
        for (var zoneIndex = 0; zoneIndex < Math.Min(hints.ForbiddenZones.Count, 8); ++zoneIndex)
        {
            var zone = hints.ForbiddenZones[zoneIndex];
            var covered = 0;
            foreach (var (_, _, point) in map.EnumeratePixels())
                if (zone.shape.Check(point))
                    ++covered;
            forbiddenCoverage.Add($"{zoneIndex}/src{zone.tag}:{covered}/{numPixels}");
        }

        var bestGrid = map.IndexToGrid(bestNodeIndex);
        var bestNode = ctx.ThetaStar.NodeByIndex(bestNodeIndex);
        var bestPos = ctx.ThetaStar.CellCenter(bestNodeIndex);
        var comparison = DescribeCandidateComparison(ctx, comparisonGoal, bestNodeIndex);
        var segment = DescribeFirstSegment(map, hints, playerPosition, firstWaypoint, currentTime);
        static string FormatG(float g) => g == float.MaxValue ? "safe" : g == float.MinValue ? "blocked" : g.ToString("F2");
        return $"pathTrace(at={currentTime:HH:mm:ss.fff},playerXZ=({playerPosition.X:F1},{playerPosition.Z:F1}),playerY={playerHeightY:F2},hints={hints.GoalZones.Count}goals/{hints.ForbiddenZones.Count}forbidden,map=center({map.Center.X:F1},{map.Center.Z:F1}),size={map.Width}x{map.Height}@{map.Resolution:F2},rotation={map.Rotation.Deg:F1},bounds={map.MinX}:{map.MinY}-{map.MaxX}:{map.MaxY},start=raw{startGridRaw}/inBounds:{startInBounds}/used{startGrid}/maxG:{FormatG(startMaxG)}/prio:{startPriority:F2},emergency={emergencyMode},constrained={blockedCells}/{numPixels},goalPixels={positivePriorityCells}/safe:{safePriorityCells}/maxPrio:{map.MaxPriority:F2},goalCoverage=[{string.Join(",", goalCoverage)}]{(hints.GoalZones.Count > goalCoverage.Count ? ",..." : "")},forbiddenCoverage=[{string.Join(",", forbiddenCoverage)}]{(hints.ForbiddenZones.Count > forbiddenCoverage.Count ? ",..." : "")},segment={segment},search(steps={ctx.ThetaStar.NumSteps},visitedGoals={visited.VisitedGoalCells}/{reachablePriorityCells},visitedMax={visited.VisitedMaxPriorityCells}/{reachableMaxPriorityCells},best=({bestGrid.x},{bestGrid.y})@({bestPos.X:F1},{bestPos.Z:F1}),score={bestNode.Score},g={bestNode.GScore:F2},h={bestNode.HScore:F2},leeway={FormatG(bestNode.PathLeeway)},startScore={ctx.ThetaStar.NodeByIndex(startIndex).Score}),{comparison}).";
    }

    private static string DescribeCandidateComparison(Context ctx, WPos? comparisonGoal, int bestNodeIndex)
    {
        if (comparisonGoal is not { } prior)
            return "paired=none";
        var map = ctx.Map;
        var raw = map.WorldToGrid(prior);
        if (!map.InBounds(raw.x, raw.y))
            return $"paired(prior=({prior.X:F1},{prior.Z:F1}),status=outside-map,winner={bestNodeIndex})";
        var priorIndex = map.GridToIndex(raw);
        var winnerNode = ctx.ThetaStar.NodeByIndex(bestNodeIndex);
        var priorNode = ctx.ThetaStar.NodeByIndex(priorIndex);
        var priorOpen = priorNode.OpenHeapIndex != 0;
        var winnerOpen = winnerNode.OpenHeapIndex != 0;
        var priority = map.PixelPriority[priorIndex];
        var maxG = map.PixelMaxG[priorIndex];
        var priorStatus = priorOpen ? "evaluated" : "not-evaluated";
        var comparison = priorOpen && winnerOpen ? ctx.ThetaStar.CompareNodeScores(ref priorNode, ref winnerNode).ToString() : "not-comparable";
        return $"paired(prior=({prior.X:F1},{prior.Z:F1})/cell:{raw.x}:{raw.y}/status:{priorStatus}/priority:{priority:F3}/maxG:{maxG:F2}/score:{(priorOpen ? priorNode.Score.ToString() : "n/a")}/g:{(priorOpen ? priorNode.GScore.ToString("F2") : "n/a")}/h:{(priorOpen ? priorNode.HScore.ToString("F2") : "n/a")},winner=cell:{map.IndexToGrid(bestNodeIndex)}/status:{(winnerOpen ? "evaluated" : "not-evaluated")}/priority:{map.PixelPriority[bestNodeIndex]:F3}/score:{winnerNode.Score}/g:{winnerNode.GScore:F2}/h:{winnerNode.HScore:F2},compareNodeScores={comparison}; negative means prior candidate ranks better, positive means winner ranks better; ranking=safety-class then higher goal priority then G+H then larger G tie-break)";
    }

    private static string DescribeFirstSegment(Map map, AIHints hints, WPos start, WPos? waypoint, DateTime currentTime)
    {
        if (waypoint is not { } end)
            return "none";
        var from = map.WorldToGrid(start);
        var to = map.WorldToGrid(end);
        var checkedCells = 0;
        var constrained = 0;
        var firstConstrained = "none";
        var firstForbidden = "none";
        var firstTemporaryObstacle = "none";
        var outside = false;
        foreach (var cell in map.EnumeratePixelsInLine(from.x, from.y, to.x, to.y))
        {
            if (++checkedCells > 1024)
                break;
            if (!map.InBounds(cell.x, cell.y))
            {
                outside = true;
                if (firstConstrained == "none")
                    firstConstrained = $"outOfBounds@{cell.x}:{cell.y}";
                continue;
            }
            var g = map.PixelMaxG[map.GridToIndex(cell.x, cell.y)];
            var point = map.GridToWorld(cell.x, cell.y, 0.5f, 0.5f);
            if (firstForbidden == "none")
                for (var i = 0; i < hints.ForbiddenZones.Count; ++i)
                {
                    var zone = hints.ForbiddenZones[i];
                    if (zone.shape.Check(point))
                    {
                        firstForbidden = $"{i}/src:{zone.tag}/activeIn:{(zone.activation - currentTime).TotalSeconds:F1}s";
                        break;
                    }
                }
            if (firstTemporaryObstacle == "none")
                for (var i = 0; i < hints.TemporaryObstacles.Count; ++i)
                    if (hints.TemporaryObstacles[i].Check(point))
                    {
                        firstTemporaryObstacle = i.ToString();
                        break;
                    }
            if (g == float.MaxValue)
                continue;
            ++constrained;
            if (firstConstrained == "none")
                firstConstrained = $"{cell.x}:{cell.y}/g:{(g == float.MinValue ? "blocked" : g <= -900 ? $"static:{g:F0}" : g <= 0 ? $"immediate:{g:F2}" : $"timeLimited:{g:F2}")}";
        }
        return $"from={from.x}:{from.y},to={to.x}:{to.y},sampler=bresenham-grid-centers,cells={checkedCells},constrained={constrained},outside={outside},bresenhamFirst={firstConstrained},forbidden={firstForbidden},temporaryObstacle={firstTemporaryObstacle},note=conservative raster sample; diagonal corner side-cell is not proof solver LOS is blocked";
    }

    public static void RasterizeForbiddenZones(Map map, WPos playerPos, List<(Sdf distance, DateTime activation, ulong source)> zones, DateTime current, List<float> gScratch, BitArray dScratch, float rtt, float cushion)
    {
        // very slight difference in activation times cause issues for pathfinding - cluster them together
        var zonesFixed = new List<(Sdf distance, float g)>(zones.Count);
        DateTime clusterEnd = default, globalStart = current, globalEnd = current.AddSeconds(120);
        float clusterG = 0;
        foreach (var zone in zones)
        {
            var activation = zone.activation.Clamp(globalStart, globalEnd);
            if (activation > clusterEnd)
            {
                clusterG = ActivationToG(activation, current, rtt);
                clusterEnd = activation.AddSeconds(0.5f);
            }
            zonesFixed.Add((zone.distance, clusterG));
        }

        map.MaxG = clusterG;
        var dim = (map.Width + 1) * (map.Height + 1);
        if (gScratch.Count != dim)
        {
            gScratch.Clear();
            gScratch.AddRange(Enumerable.Repeat(0f, dim));
        }
        dScratch.Length = dim;

        // TODO: group continuous sdfs with same gscore together
        //zonesFixed.SortBy(z => (z.g, z.distance.IsContinuous));
        foreach (var (d, g) in zonesFixed)
            RasterizeForbiddenZone(map, d, g, gScratch.AsSpan(), dScratch, cushion);

        // whole grid is blocked, unblock cells with highest gscore so pathfinding produces a reasonable result
        var ipx = map.Width * map.Height;
        var maxGSpan = map.PixelMaxG.AsSpan()[..ipx];
        var realMaxG = TensorPrimitives.Max(maxGSpan);
        if (realMaxG < float.MaxValue)
        {
            for (var i = 0; i < ipx; i++)
                if (map.PixelMaxG[i] == realMaxG)
                {
                    map.PixelMaxG[i] = float.MaxValue;
                    map.PixelPriority[i] = 0;
                }
        }

        /*
        // PrecisePosition unblocks exactly one cell
        if (maxGSpan.Count(float.MaxValue) <= 1)
            return;

        var (pX, pY) = map.WorldToGrid(playerPos);
        var pix = map.GridToIndex(pX, pY);
        var playerG = map.PixelMaxG[pix];

        // only run the unblock code if the player is actually not safe here
        if (playerG > map.MaxG)
            return;

        float pixG(int x, int y)
        {
            if (x < 0 || x >= map.Width)
                return -1;
            if (y < 0 || y >= map.Height)
                return -1;

            return map.PixelMaxG[map.GridToIndex(x, y)];
        }

        var pN = pixG(pX, pY - 1);
        var pE = pixG(pX + 1, pY);
        var pW = pixG(pX - 1, pY);
        var pS = pixG(pX, pY + 1);

        if (pN < playerG && pE < playerG && pW < playerG && pS < playerG)
        {
            var neighbourG = playerG;
            if (pN >= 0)
                neighbourG = MathF.Min(pN, neighbourG);
            if (pE >= 0)
                neighbourG = MathF.Min(pE, neighbourG);
            if (pW >= 0)
                neighbourG = MathF.Min(pW, neighbourG);
            if (pS >= 0)
                neighbourG = MathF.Min(pS, neighbourG);

            map.PixelMaxG[pix] = neighbourG;
        }
        */
    }

    public static void RasterizeForbiddenZone(Map map, in Sdf sdf, float g, Span<float> gScratch, BitArray dScratch, float cushion)
    {
        gScratch.Fill(float.MinValue);
        dScratch.SetAll(false);

        var discrete = !sdf.IsContinuous;

        var dy = map.LocalZDivRes * map.Resolution * map.Resolution;
        var dx = dy.OrthoL();
        var cy = map.Center - map.Width / 2 * dx - map.Height / 2 * dy;

        for (var y = 0; y <= map.Height; y++)
        {
            for (var x = 0; x <= map.Width; x++)
            {
                var iCell = y * (map.Width + 1) + x;

                // cell is already blocked by a more dangerous zone
                // TODO: this adds a 1-pixel-wide cushion with the current gscore to previously rasterized zones if they happen to be axis aligned
                //if (y < map.Height && x < map.Width && map.PixelMaxG[iCell] < g)
                //    continue;

                // filled by previous iteration
                if (gScratch[iCell] != float.MinValue)
                    continue;

                var point = cy + x * dx + y * dy;

                if (discrete)
                {
                    gScratch[iCell] = sdf.CheckIndexed(point, iCell) ? g : float.MaxValue;
                    continue;
                }

                var distance = sdf.Distance(point);
                if (!float.IsFinite(distance))
                    throw new InvalidOperationException("Misbehaving distance function");

                var toRowEnd = map.Width - x;

                int distPixels;

                if (distance >= cushion)
                {
                    // TODO optimize; this drastically increases the number of sdf evaluations since they need to be executed for each grid point within the cushion zone
                    distPixels = (int)((distance - cushion) / map.Resolution);
                    gScratch.Slice(iCell, Math.Min(distPixels, toRowEnd) + 1).Fill(float.MaxValue);
                }
                else if (cushion > 0 && distance >= 0)
                {
                    dScratch[iCell] = true;
                    gScratch[iCell] = float.MaxValue;
                }
                else
                {
                    distPixels = (int)MathF.Ceiling(-distance / map.Resolution);
                    gScratch.Slice(iCell, Math.Min(distPixels, toRowEnd + 1)).Fill(g);
                }
            }
        }

        for (var y = 0; y < map.Height; y++)
            for (var x = 0; x < map.Width; x++)
            {
                var iG = y * (map.Width + 1) + x;
                var iM = y * map.Width + x;

                if (map.PixelMaxG[iM] < float.MaxValue)
                    continue;

                var cellG = map.PixelMaxG[iM] = Math.Min(
                    Math.Min(gScratch[iG], gScratch[iG + 1]),
                    Math.Min(gScratch[iG + map.Width + 1], gScratch[iG + map.Width + 2])
                );
                //if (cellG == float.MinValue)
                //    throw new InvalidOperationException($"grid was not filled at {x}x{y}");
                if (cellG < float.MaxValue)
                    map.PixelPriority[iM] = float.MinValue;

                if (dScratch[iG] || dScratch[iG + 1] || dScratch[iG + map.Width + 1] || dScratch[iG + map.Width + 2])
                    map.PixelAvoid[iM] = true;
            }
    }

    public static void RasterizeGoalZones(Map map, List<Func<WPos, float>> goals, bool cushion)
    {
        // see Map.EnumeratePixels, note that we care about corners rather than centers
        var dy = map.LocalZDivRes * map.Resolution * map.Resolution;
        var dx = dy.OrthoL();
        var cy = map.Center - map.Width / 2 * dx - map.Height / 2 * dy;

        var iCell = 0;
        for (var y = 0; y < map.Height; ++y)
        {
            var cx = cy;
            var leftP = goals.Sum(g => g(cx));
            for (var x = 0; x < map.Width; ++x)
            {
                cx += dx;
                var rightP = goals.Sum(g => g(cx));
                map.PixelPriority[iCell++] = Math.Min(leftP, rightP);
                leftP = rightP;
            }
            cy += dy;
        }
        var bleftP = goals.Sum(g => g(cy));
        iCell -= map.Width;
        for (var x = 0; x < map.Width; ++x, ++iCell)
        {
            cy += dx;
            var brightP = goals.Sum(g => g(cy));
            var bottomP = Math.Min(bleftP, brightP);
            var jCell = iCell;
            for (var y = map.Height; y > 0; --y, jCell -= map.Width)
            {
                var topP = map.PixelPriority[jCell];
                if (map.PixelMaxG[jCell] == float.MaxValue)
                {
                    // TODO: is there a way to track whether this pixel has been previously avoided without creating a whole extra array to store that info?
                    var cellP = map.PixelPriority[jCell] = Math.Min(topP, bottomP);
                    map.MaxPriority = Math.Max(map.MaxPriority, cellP);
                }
                else
                {
                    map.PixelPriority[jCell] = float.MinValue;
                }
                bottomP = topP;
            }
            bleftP = brightP;
        }

        if (cushion)
            AddCushion(map);
    }

    public static void RasterizeForbiddenZonesOld(Map map, List<(Sdf distance, DateTime activation, ulong source)> zones, DateTime current, List<float> gScratch, BitArray dScratch, float rtt, float cushion)
    {
        // very slight difference in activation times cause issues for pathfinding - cluster them together
        var zonesFixed = new (Sdf distance, float g)[zones.Count];
        DateTime clusterEnd = default, globalStart = current, globalEnd = current.AddSeconds(120);
        float clusterG = 0;
        for (var i = 0; i < zonesFixed.Length; ++i)
        {
            var activation = zones[i].activation.Clamp(globalStart, globalEnd);
            if (activation > clusterEnd)
            {
                clusterG = ActivationToG(activation, current, rtt);
                clusterEnd = activation.AddSeconds(0.5f);
            }
            zonesFixed[i] = (zones[i].distance, clusterG);
        }

        // note that a zone can partially intersect a pixel; so what we do is check each corner and set the maxg value of a pixel equal to the minimum of 4 corners
        // to avoid 4x calculations, we do a slightly tricky loop:
        // - outer loop fills row i to with g values corresponding to the 'upper edge' of cell i
        // - inner loop calculates the g value at the left border, then iterates over all right corners and fills minimums of two g values to the cells
        // - second outer loop calculates values at 'bottom' edge and then updates the values of all cells to correspond to the cells rather than edges
        map.MaxG = clusterG;
        if (gScratch.Count != map.PixelMaxG.Length)
        {
            gScratch.Clear();
            gScratch.AddRange(Enumerable.Repeat(0f, map.PixelMaxG.Length));
        }
        dScratch.Length = map.PixelMaxG.Length;
        var numBlockedCells = 0;

        // see Map.EnumeratePixels, note that we care about corners rather than centers
        var dy = map.LocalZDivRes * map.Resolution * map.Resolution;
        var dx = dy.OrthoL();
        var cy = map.Center - map.Width / 2 * dx - map.Height / 2 * dy;

        var iCell = 0;
        for (var y = 0; y < map.Height; ++y)
        {
            var cx = cy;
            var (leftG, leftD) = CalculateMaxG(zonesFixed, cx, cushion);
            for (var x = 0; x < map.Width; ++x)
            {
                cx += dx;
                var (rightG, rightD) = CalculateMaxG(zonesFixed, cx, cushion);
                dScratch[iCell] = leftD || rightD;
                gScratch[iCell++] = Math.Min(leftG, rightG);
                leftD = rightD;
                leftG = rightG;
            }
            cy += dy;
        }
        var (bleftG, bleftD) = CalculateMaxG(zonesFixed, cy, cushion);
        iCell -= map.Width;
        for (var x = 0; x < map.Width; ++x, ++iCell)
        {
            cy += dx;
            var (brightG, brightD) = CalculateMaxG(zonesFixed, cy, cushion);
            var bottomD = bleftD || brightD;
            var bottomG = Math.Min(bleftG, brightG);
            var jCell = iCell;
            for (var y = map.Height; y > 0; --y, jCell -= map.Width)
            {
                var topG = gScratch[jCell];
                var topD = dScratch[jCell];
                var cellG = map.PixelMaxG[jCell] = Math.Min(Math.Min(topG, bottomG), map.PixelMaxG[jCell]);
                if (cellG != float.MaxValue)
                {
                    map.PixelPriority[jCell] = float.MinValue;
                    ++numBlockedCells;
                }
                else if (bottomD || topD)
                    map.PixelAvoid[jCell] = true;
                bottomD = topD;
                bottomG = topG;
            }
            bleftD = brightD;
            bleftG = brightG;
        }

        if (numBlockedCells == map.Width * map.Height)
        {
            // everything is dangerous, clear least dangerous so that pathfinding works reasonably
            // note that max value could be smaller than MaxG, if more dangerous stuff overlaps it
            float realMaxG = 0;
            for (iCell = 0; iCell < numBlockedCells; ++iCell)
                realMaxG = Math.Max(realMaxG, map.PixelMaxG[iCell]);
            for (iCell = 0; iCell < numBlockedCells; ++iCell)
            {
                if (map.PixelMaxG[iCell] == realMaxG)
                {
                    map.PixelMaxG[iCell] = float.MaxValue;
                    map.PixelPriority[iCell] = 0;
                }
            }
        }
    }

    private static (float G, bool D) CalculateMaxG(Span<(Sdf d, float g)> zones, WPos p, float cushion)
    {
        var g = float.MaxValue;
        var d = false;

        foreach (ref var z in zones)
        {
            var dist = z.d.Distance(p);
            if (dist < 0)
            {
                g = z.g;
                break;
            }

            if (dist < cushion)
                d = true;
        }
        return (g, d);
    }

    public static void AddCushion(Map map)
    {
        var pMax = float.MinValue;
        for (var i = 0; i < map.Width * map.Height; i++)
        {
            if (map.PixelPriority[i] >= 0 && map.PixelAvoid[i])
                map.PixelPriority[i] -= CushionDepriority;
            pMax = Math.Max(map.PixelPriority[i], pMax);
        }
        map.MaxPriority = pMax;
    }

    private static float ActivationToG(DateTime activation, DateTime current, float rtt) => MathF.Max(0, (float)(activation - current).TotalSeconds - CushionFromRTT(rtt));

    internal static (WPos? first, WPos? second) GetFirstWaypoints(ThetaStar pf, int cell)
    {
        ref var startingNode = ref pf.NodeByIndex(cell);
        if (startingNode.GScore == 0 && startingNode.PathMinG == float.MaxValue)
            return (null, null); // we're already in safe zone

        var nextCell = cell;
        do
        {
            ref var node = ref pf.NodeByIndex(cell);
            if (pf.NodeByIndex(node.ParentIndex).GScore == 0)
            {
                // Theta* validated segments to each node's EnterOffset position. Replacing
                // those endpoints with cell centers can move the issued segment through a
                // blocked corner, even though the searched segment was clear.
                var dest = pf.NodePosition(cell);
                var next = nextCell != cell ? pf.NodePosition(nextCell) : (WPos?)null;
                return (dest, next);
            }
            nextCell = cell;
            cell = node.ParentIndex;
        }
        while (true);
    }
}
