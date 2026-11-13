using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Component.GUI;
using CP = BossMod.DeepDungeonContinuationPolicy;

namespace BossMod.Services;

internal sealed partial class TickService
{
    private CP.Session? _continuation;
    private long _continuationGeneration;
    private EventSubscription? _continuationMapSubscription;
    private (int Target, CP.Intent Intent)? _lastDepartureIntent;

    private CP.Intent CurrentDepartureIntent()
    {
        // Module reload can replace the config node; a cached interface may refer to the retired node.
        _autoDDConfig = ResolveAutoDDConfig(configRoot);
        var target = _autoDDConfig?.TargetBossFloor ?? -1;
        var intent = CP.Decide(_autoDDConfig?.AutoLeaveAfterBossDefeat == true, _trackedBossDungeon, _trackedBossFloor, target);
        if (_lastDepartureIntent != (target, intent))
        {
            _lastDepartureIntent = (target, intent);
            logger.Info($"[AutoDD] post-boss intent: dungeon={_trackedBossDungeon}, clearedBoss={_trackedBossFloor}, target={target}, intent={intent}, stage={_departureStage}.");
        }
        return intent;
    }

    private bool EnsureFinalExitAllowed()
    {
        var intent = CurrentDepartureIntent();
        if (_continuation != null)
            return false;
        var action = CP.RevalidateExit(intent, _exitConfirmationArmedAt != 0 || ContinuationUiVisible(), _exitConfirmationSubmittedAt != 0);
        if (action is CP.ExitAction.Exit or CP.ExitAction.AwaitTransition)
            return true; // Submitted Yes is handled by the existing bounded transition observer.

        StopOwnedDeparturePath("current target no longer permits final Exit");
        if (action == CP.ExitAction.Cancel)
        {
            CancelDeepDungeonDeparture("current departure config disabled or unavailable");
            return false;
        }
        if (_departurePathStopWhenReady)
            return false;
        BeginContinuationIfRequested();
        if (action == CP.ExitAction.Hold)
            FailContinuation("final Exit no longer permitted; target changed with pending UI or is invalid; dismiss menus manually before retry");
        else
        {
            _departureExitActorID = 0;
            _departureExitRawAddress = IntPtr.Zero;
            _departureExitRawEntityID = 0;
            _departureStage = DeepDungeonDepartureStage.WaitingRoom;
            ContinuationStatus("current target requires continuation; final Exit abandoned before interaction");
        }
        return false;
    }

    private void BeginContinuationIfRequested()
    {
        var intent = CurrentDepartureIntent();
        var target = _autoDDConfig?.TargetBossFloor ?? -1;
        if (intent is CP.Intent.Exit or CP.Intent.None)
            return;
        _continuation = new(_trackedBossFloor, target, ++_continuationGeneration, rotation.PresetSelectionVersion);
        if (intent == CP.Intent.Hold)
            FailContinuation("invalid target or unsupported continuation dungeon; target 0 retains existing Exit behavior");
        else
            ContinuationStatus($"boss {_trackedBossFloor} defeated; target {target}; waiting for boss-floor departure");
    }

    private void ContinuationStatus(string message)
    {
        if (_continuation is not { } session || session.LastStatus == message)
            return;
        session.LastStatus = message;
        worldstateGlobal.DeepDungeon.ContinuationStatus = message;
        logger.Info($"[AutoDD] continuation {session.Generation}: {message}.");
    }

    private void FailContinuation(string reason)
    {
        if (_continuation is not { } session || session.Stage == CP.Stage.FailedHold)
            return;
        StopOwnedDeparturePath("continuation stopped: " + reason);
        // FailedHold still actively owns the completion hold; explicit retry/reset ends that scope.
        // Manual/safety changes invalidate the manager token independently and are never restored here.
        session.Advance(CP.Stage.FailedHold, Environment.TickCount64);
        ContinuationStatus("STOPPED: " + reason + "; disable automatic departure to reset");
        logger.Warning($"[AutoDD] continuation stopped: {reason}; no further entry callbacks will be issued.");
    }

    private void ObserveContinuationMap()
    {
        var dd = worldstateGlobal.DeepDungeon;
        if (_continuation is { } session && CP.ExpectedNextSet(dd.DungeonId, dd.Floor, session.ClearedBoss, dd.Rooms.Any(room => room != 0)))
            session.InitializedFloor = dd.Floor;
    }

    private void ObserveContinuationArrival()
    {
        if (_continuation is not { } session)
            return;
        if (rotation.PresetSelectionVersion != session.PresetVersion)
        {
            CancelDeepDungeonDeparture("preset changed during continuation; pending game entry, if submitted, cannot be retracted");
            _deepDungeonSuspended = true;
            return;
        }
        if (_autoDungeonPreset != null && _autoDDConfig?.AutoActivateDungeonAI != true)
        {
            CancelDeepDungeonDeparture("automatic Dungeon AI activation disabled during continuation");
            return;
        }
        if (worldstateGlobal.DeepDungeon.ContinuationRetryRequested)
        {
            worldstateGlobal.DeepDungeon.ContinuationRetryRequested = false;
            if (session.Stage == CP.Stage.FailedHold && worldstateGlobal.CurrentZone == CP.WaitingTerritory
                && !IsLoadingOrTransitioning() && Utils.IsScreenReady() && rotation.Player is { IsDead: false, InCombat: false, CastInfo: null })
            {
                if (ContinuationUiVisible())
                    ContinuationStatus("STOPPED: close existing entry/confirmation menus before retry");
                else
                {
                    StopOwnedDeparturePath("explicit continuation retry");
                    if (!_departurePathStopWhenReady)
                    {
                        _continuation = null;
                        worldstateGlobal.DeepDungeon.ContinuationStatus = null;
                        _departureStage = DeepDungeonDepartureStage.WaitingRoom;
                        BeginContinuationIfRequested();
                    }
                }
            }
            return;
        }
        if (session.Stage == CP.Stage.Entering && Environment.TickCount64 - session.StageAt >= CP.EntryTimeoutMs)
        {
            FailContinuation($"next set was not confirmed within 60s; expected POTD/{session.ClearedBoss + 1}");
            return;
        }
        var dd = worldstateGlobal.DeepDungeon;
        var loading = IsLoadingOrTransitioning();
        if (session.WaitingAt != 0 && loading && session.Follower && session.Stage != CP.Stage.FailedHold && session.Stage != CP.Stage.Entering)
        {
            StopOwnedDeparturePath("party entry transition observed");
            session.Advance(CP.Stage.Entering, Environment.TickCount64);
            ContinuationStatus($"follower observing party entry; expecting floor {session.ClearedBoss + 1}");
        }
        if (dd.DungeonId == DeepDungeonState.DungeonType.None || dd.Floor == 0 || dd.DungeonId == _trackedBossDungeon && dd.Floor == session.ClearedBoss)
            return;
        if (!CP.ExpectedNextSet(dd.DungeonId, dd.Floor, session.ClearedBoss, true) || session.WaitingAt == 0)
        {
            FailContinuation($"unexpected dungeon/floor {dd.DungeonId}/{dd.Floor}; expected POTD/{session.ClearedBoss + 1}");
            return;
        }
        if (session.Stage == CP.Stage.FailedHold || loading || !Utils.IsScreenReady() || rotation.Player is not { IsDead: false }
            || session.InitializedFloor != dd.Floor || worldstateGlobal.CurrentZone == CP.WaitingTerritory
            || !dd.Party.Any(member => member.EntityId == rotation.Player.InstanceID))
            return;
        StopOwnedDeparturePath("expected next set initialized");
        if (_departurePathStopWhenReady)
            return;

        // Undo only our completion suppression, never an unrelated safety/manual disable.
        if (_autoDungeonPreset != null && _completionAutorotationSuppressed && rotation.OwnsDeepDungeonCompletionSuppression(_completionSuppressionToken)
            && rotation.PresetSelectionVersion == _presetVersionAfterAutoActivation && _autoDDConfig?.AutoActivateDungeonAI == true)
        {
            ReleaseCompletionSuppression();
            rotation.Activate(_autoDungeonPreset, exclusive: true);
            _presetVersionAfterAutoActivation = rotation.PresetSelectionVersion;
            _completionAutorotationSuppressed = false;
        }
        logger.Info($"[AutoDD] continuation confirmed: POTD floor {dd.Floor} initialized after boss {session.ClearedBoss}; target {_autoDDConfig?.TargetBossFloor}; normal owned automation may resume.");
        _deepDungeonTerritory = worldstateGlobal.CurrentZone;
        ResetDeepDungeonBossCompletion();
    }

    private bool ContinuationBlocksDeparture()
    {
        if (_continuation is not { } session)
            return false;
        if (session.Stage == CP.Stage.Entering)
        {
            if (Environment.TickCount64 - session.StageAt >= CP.EntryTimeoutMs)
                FailContinuation($"entry request/party transition did not produce initialized floor {session.ClearedBoss + 1} within 60s");
            return true;
        }
        return session.Stage == CP.Stage.FailedHold;
    }

    private bool ContinuationHasPendingUi()
        => _continuation?.Stage is CP.Stage.AwaitingMenu or CP.Stage.AwaitingConfirmation;

    private void ObserveContinuationTerritoryChange()
    {
        if (_continuation is not { } session)
            return;
        if (session.WaitingAt == 0)
        {
            FailContinuation("unexpected territory before the matching waiting room was observed");
            return;
        }
        StopOwnedDeparturePath("waiting-room territory changed during continuation");
        if (session.Stage != CP.Stage.FailedHold && session.Stage != CP.Stage.Entering)
            session.Advance(CP.Stage.Entering, Environment.TickCount64);
        ContinuationStatus($"waiting for initialized POTD floor {session.ClearedBoss + 1}; territory change alone is not success");
    }

    private unsafe bool UpdateWaitingRoomContinuation(Actor player)
    {
        if (_continuation is not { } session || _autoDDConfig == null)
            return false;
        var now = Environment.TickCount64;
        session.WaitingAt = session.WaitingAt == 0 ? now : session.WaitingAt;
        if (session.Stage is CP.Stage.Entering or CP.Stage.FailedHold)
            return true;
        if (now - session.WaitingAt >= CP.PartyTimeoutMs)
        {
            FailContinuation("waiting-room party/approach deadline exceeded (180s)");
            return true;
        }
        if (session.Target != _autoDDConfig.TargetBossFloor)
        {
            var intent = CP.Decide(true, _trackedBossDungeon, session.ClearedBoss, _autoDDConfig.TargetBossFloor);
            if (intent == CP.Intent.Continue)
                session.Target = _autoDDConfig.TargetBossFloor;
            else if (intent == CP.Intent.Exit && session.Stage is CP.Stage.WaitingRoom or CP.Stage.DiscoveringEntry or CP.Stage.ApproachingEntry)
            {
                if (ContinuationUiVisible())
                {
                    FailContinuation("target changed while entry UI is visible; dismiss it manually before selecting Exit");
                    return true;
                }
                StopOwnedDeparturePath("target changed to final Exit");
                if (_departurePathStopWhenReady)
                    return true;
                ContinuationStatus("target changed; selecting final Exit instead of continuation");
                _continuation = null;
                worldstateGlobal.DeepDungeon.ContinuationStatus = null;
                _departureExitActorID = 0;
                _departureExitRawAddress = IntPtr.Zero;
                _departureExitRawEntityID = 0;
                _departureStage = DeepDungeonDepartureStage.WaitingRoom;
                return false;
            }
            else
            {
                FailContinuation("target changed after interaction or became invalid; pending UI left untouched");
                return true;
            }
        }

        // No normal AutoClear zone module exists in the waiting room. Suppress legacy chase/follow goals.
        hintsGlobal.DeepDungeonPartyMode = true;
        hintsGlobal.DeepDungeonPartyGoals = [AIHints.GoalSingleTarget(player.Position, 1)];
        hintsGlobal.DeepDungeonPartyNavigationKey = $"continuation:{session.Generation}";

        if (!TryContinuationParty(player, out var leader, out var roster, out var ready))
        {
            if (ContinuationHasPendingUi())
            {
                FailContinuation("leader/party became unavailable during owned entry UI");
                return true;
            }
            StopOwnedDeparturePath("party leader or roster unavailable");
            ContinuationStatus("waiting for authoritative party leader and current roster");
            return true;
        }
        var leaderContentID = worldstateGlobal.Party.Members.FirstOrDefault(m => m.InstanceId == leader!.InstanceID).ContentId;
        if (session.Roster == null)
        {
            session.Roster = roster;
            session.LeaderContentID = leaderContentID;
            session.Follower = _autoDDConfig.IsDungeonFollower;
        }
        else if (session.Roster != roster || session.LeaderContentID != leaderContentID || session.Follower != _autoDDConfig.IsDungeonFollower)
        {
            FailContinuation("party roster, leader, or configured role changed; fresh interaction ownership required");
            return true;
        }
        if (session.Follower)
        {
            if (leader == player)
                FailContinuation("configured follower is party leader; refusing to initiate entry");
            else
                FollowContinuationLeader(session, player, leader!);
            return true;
        }
        if (leader != player)
        {
            FailContinuation("configured Leader is not actual party leader");
            return true;
        }
        if (!ready)
        {
            if (ContinuationHasPendingUi())
            {
                FailContinuation("party readiness changed during owned entry UI");
                return true;
            }
            StopOwnedDeparturePath("party not ready for entry");
            ContinuationStatus("waiting for all party members: present, alive, not casting/in combat, within maximum lead distance");
            return true;
        }

        try
        {
            var menu = (AtkUnitBase*)gameGui.GetAddonByName("DeepDungeonMenu").Address;
            var yesno = (AtkUnitBase*)gameGui.GetAddonByName("SelectYesno").Address;
            if (session.Stage is CP.Stage.AwaitingMenu or CP.Stage.AwaitingConfirmation)
                return UpdateContinuationUi(session, menu, yesno, now);
            if (menu != null && menu->IsVisible || yesno != null && yesno->IsVisible)
            {
                StopOwnedDeparturePath("unowned entry UI visible");
                ContinuationStatus("unowned DeepDungeonMenu/SelectYesno visible; waiting for manual dismissal");
                return true;
            }
            session.ObserveApproachEligibility(now);
            ApproachContinuationEntry(session, player, now);
        }
        catch (Exception ex)
        {
            FailContinuation($"entry UI/localized data unavailable: {ex.GetType().Name}: {ex.Message}");
        }
        return true;
    }

    private bool TryContinuationParty(Actor player, out Actor? leader, out string roster, out bool ready)
    {
        var party = Service.PartyList;
        var ws = worldstateGlobal;
        var solo = party.Length <= 1 && ws.Party.Members.Take(PartyState.MaxPartySize).Count(m => m.IsValid()) <= 1;
        leader = solo ? player : null;
        ready = true;
        if (party.Length > DeepDungeonState.NumPartyMembers)
        {
            roster = "";
            ready = false;
            return false;
        }
        var ids = new List<ulong>();
        for (var i = 0; i < party.Length && i < PartyState.MaxPartySize; ++i)
        {
            var member = party[i];
            if (member == null || member.ContentId == 0)
            {
                ready = false;
                continue;
            }
            var contentID = (ulong)member.ContentId;
            ids.Add(contentID);
            var mapped = ws.Party.Members.Take(PartyState.MaxPartySize).FirstOrDefault(m => m.ContentId == contentID);
            var actor = ws.Actors.Find(mapped.InstanceId);
            if (i == party.PartyLeaderIndex)
                leader = actor;
            ready &= CP.PartyMemberReady(actor != null, actor is { IsDeadOrDestroyed: false, HPMP.CurHP: > 0 },
                actor?.InCombat ?? false, actor?.CastInfo != null,
                actor != null ? (actor.Position - player.Position).Length() : float.MaxValue,
                _autoDDConfig!.DungeonMaxLeadDistance, actor != null ? actor.PosRot.Y - player.PosRot.Y : float.MaxValue);
        }
        roster = solo ? "solo" : string.Join(",", ids.Order());
        return leader is { IsDeadOrDestroyed: false } && (solo || ids.Count == party.Length);
    }

    private void FollowContinuationLeader(CP.Session session, Actor player, Actor leader)
    {
        if (session.Stage != CP.Stage.WaitingRoom)
            return;
        var range = _autoDDConfig!.DungeonFollowDistance;
        if (!float.IsFinite(range) || range < 1)
        {
            FailContinuation("invalid follower distance");
            return;
        }
        var distance = Vector3.Distance(player.PosRot.XYZ(), leader.PosRot.XYZ());
        if (distance <= range)
            StopOwnedDeparturePath("follower regrouped for party entry");
        else if (SetContinuationDestination(leader.InstanceID, leader.PosRot.XYZ(), leader.HitboxRadius, IntPtr.Zero, 0, 2))
            UpdateDepartureVnav(player, leader.InstanceID, leader.OID, _departureExitPosition, range, distance, "party leader");
        ContinuationStatus($"follower waiting for leader entry; expected floor {session.ClearedBoss + 1}; target {session.Target}");
    }

    private void ApproachContinuationEntry(CP.Session session, Actor player, long now)
    {
        if (session.Stage == CP.Stage.WaitingRoom)
            session.Advance(CP.Stage.DiscoveringEntry, now);
        if (session.Stage == CP.Stage.DiscoveringEntry && now - session.StageAt >= 30000
            || session.Stage == CP.Stage.ApproachingEntry && now - session.StageAt >= 60000)
        {
            FailContinuation("Entry Point discovery/approach timed out");
            return;
        }
        var candidates = Service.ObjectTable.Where(o => o.IsTargetable && o.BaseId == CP.EntryBaseID
            && o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
            .GroupBy(DepartureExitObjectIdentity).Select(g => g.First())
            .OrderBy(o => Vector3.Distance(o.Position, player.PosRot.XYZ())).ToArray();
        var selected = candidates.FirstOrDefault(o => o.Address == Service.TargetManager.Target?.Address);
        if (selected == null && (candidates.Length == 1 || candidates.Length >= 2
            && DeepDungeonPolicy.IsUnambiguousNearestWaitingRoomExit(Vector3.Distance(candidates[0].Position, player.PosRot.XYZ()), Vector3.Distance(candidates[1].Position, player.PosRot.XYZ()))))
            selected = candidates[0];
        if (selected == null)
        {
            StopOwnedDeparturePath("Entry Point unavailable/ambiguous");
            ContinuationStatus("waiting for unique/clearly nearest targetable PotD Entry Point 2006012");
            return;
        }
        var id = DepartureExitObjectIdentity(selected);
        if (session.EntryIdentity != 0 && session.EntryIdentity != id)
        {
            FailContinuation("selected Entry Point changed during attempt");
            return;
        }
        if (!SetContinuationDestination(id, selected.Position, selected.HitboxRadius, selected.Address, selected.EntityId, 0.25f))
            return;
        session.EntryIdentity = id;
        if (session.Stage == CP.Stage.DiscoveringEntry)
            session.Advance(CP.Stage.ApproachingEntry, now);
        var range = selected.HitboxRadius + 2;
        var distance = Vector3.Distance(player.PosRot.XYZ(), selected.Position);
        if (distance > range)
        {
            ContinuationStatus($"approaching Entry Point {id:X}; expecting floor {session.ClearedBoss + 1}");
            UpdateDepartureVnav(player, id, CP.EntryBaseID, selected.Position, range, distance, "Entry Point");
            return;
        }
        StopOwnedDeparturePath("within Entry Point interaction range");
        _departureStage = DeepDungeonDepartureStage.InteractingWithExit; // shared departure movement state only
        hintsGlobal.InteractWithRawTarget = selected;
        hintsGlobal.CanIssueInteraction = actual => CanDispatchContinuationInteraction(session, actual);
        hintsGlobal.OnInteractionIssued = actual =>
        {
            if (_continuation != session || !session.TryArmMenu(actual, Environment.TickCount64))
                return;
            ContinuationStatus($"Entry Point interaction dispatched to {actual:X}; awaiting owned DeepDungeonMenu");
        };
    }

    private bool SetContinuationDestination(ulong id, Vector3 position, float hitbox, IntPtr rawAddress, ulong entityID, float changeThreshold)
    {
        if (_departureExitActorID != id || Vector3.Distance(_departureExitPosition, position) > changeThreshold)
        {
            StopOwnedDeparturePath("continuation destination changed");
            if (_departurePathStopWhenReady)
                return false;
            _departureExitActorID = id;
            _departureExitPosition = position;
            _departureExitHitbox = hitbox;
            _departureExitRawAddress = rawAddress;
            _departureExitRawEntityID = entityID;
        }
        return !_departurePathStopWhenReady;
    }

    private bool CanDispatchContinuationInteraction(CP.Session session, ulong actual)
    {
        // Config UI is drawn between hint production and native dispatch; revalidate user overrides here.
        var config = _autoDDConfig = ResolveAutoDDConfig(configRoot);
        if (_continuation != session || session.Stage != CP.Stage.ApproachingEntry || actual != session.EntryIdentity
            || session.Follower || config == null || config.IsDungeonFollower
            || CP.Decide(config.AutoLeaveAfterBossDefeat, _trackedBossDungeon, session.ClearedBoss, config.TargetBossFloor) != CP.Intent.Continue
            || rotation.PresetSelectionVersion != session.PresetVersion || worldstateGlobal.CurrentZone != CP.WaitingTerritory
            || IsLoadingOrTransitioning() || !Utils.IsScreenReady() || movementOverride.IsManualMoveRequested()
            || ContinuationUiVisible() || rotation.Player is not { IsDead: false, InCombat: false, CastInfo: null } player)
            return false;
        if (!Service.ObjectTable.Any(o => o.Address == _departureExitRawAddress && o.IsTargetable && o.BaseId == CP.EntryBaseID
            && o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj && DepartureExitObjectIdentity(o) == actual))
            return false;
        return TryContinuationParty(player, out var leader, out var roster, out var ready)
            && leader == player && roster == session.Roster && ready;
    }

    private unsafe bool UpdateContinuationUi(CP.Session session, AtkUnitBase* menu, AtkUnitBase* yesno, long now)
    {
        if (!session.CanSubmit(session.Stage, now))
        {
            FailContinuation($"{session.Stage} timed out after 10s; no UI retry");
            return true;
        }
        if (now < session.NextUiPollAt)
            return true;
        session.NextUiPollAt = now + 250;
        if (session.Stage == CP.Stage.AwaitingMenu)
        {
            var visible = menu != null && menu->IsVisible;
            if (!visible)
            {
                if (session.MenuObserved)
                    FailContinuation("owned entry menu dismissed before Proceed submission");
                return true;
            }
            session.MenuObserved = true;
            if (!menu->IsReady)
                return true;
            var expected = ContinuationText(CP.CommandSheet, CP.CommandRow);
            var texts = ContinuationAddonTexts(menu);
            if (!texts.Any(text => CP.MatchesText(expected, text)))
            {
                ContinuationStatus($"DeepDungeonMenu not recognized; localized command source={CP.CommandSheet}/{CP.CommandRow}/1, expected='{expected}'; observed=[{string.Join("|", texts.Take(8))}]");
                return true;
            }
            if (!session.TryAdvanceUi(CP.Stage.AwaitingMenu, now))
                return true;
            FireContinuationCallback(menu);
            ContinuationStatus("owned Proceed callback Int 0 issued; awaiting localized entry question");
        }
        else if (session.Stage == CP.Stage.AwaitingConfirmation)
        {
            var visible = yesno != null && yesno->IsVisible;
            if (!visible)
            {
                if (session.ConfirmationObserved)
                    FailContinuation("entry confirmation dismissed without automatic Yes");
                return true;
            }
            session.ConfirmationObserved = true;
            if (!yesno->IsReady || yesno->AtkValues == null || yesno->AtkValuesCount < 3)
                return true;
            var expected = ContinuationText(CP.QuestionSheet, CP.QuestionRow);
            var actual = ContinuationString(yesno->AtkValues[0]);
            if (!CP.MatchesText(expected, actual))
            {
                ContinuationStatus($"entry SelectYesno left untouched: question mismatch; source={CP.QuestionSheet}/0/1, expected='{expected}', actual='{actual}'");
                return true;
            }
            if (!session.TryAdvanceUi(CP.Stage.AwaitingConfirmation, now))
                return true;
            FireContinuationCallback(yesno);
            ContinuationStatus($"entry Yes callback Int 0 issued; awaiting initialized POTD floor {session.ClearedBoss + 1}, not yet successful");
        }
        return true;
    }

    private static string ContinuationText(string sheet, uint row)
    {
        var text = Service.LuminaGameData.Excel.GetSheet<Lumina.Excel.RawRow>(null, sheet).GetRowOrDefault(row)?.ReadStringColumn(CP.TextColumn).ToString();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"Localized continuation text missing: {sheet}/{row}/{CP.TextColumn}");
        return text;
    }

    private static unsafe string? ContinuationString(AtkValue value)
        => (value.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString) && (byte*)value.String != null
            ? SeString.Parse(value.String).TextValue : null;

    private static unsafe List<string> ContinuationAddonTexts(AtkUnitBase* addon)
    {
        var result = new List<string>();
        if (addon->AtkValues != null)
            for (var i = 0; i < Math.Min((int)addon->AtkValuesCount, 256); ++i)
                if (ContinuationString(addon->AtkValues[i]) is { } text)
                    result.Add(text);
        CollectContinuationTexts(&addon->UldManager, result, new HashSet<nint>(), 0);
        return result;
    }

    private static unsafe void CollectContinuationTexts(AtkUldManager* manager, List<string> result, HashSet<nint> visited, int depth)
    {
        if (depth > 8 || visited.Count >= 4096 || manager->NodeList == null)
            return;
        for (var i = 0; i < Math.Min((int)manager->NodeListCount, 4096) && visited.Count < 4096; ++i)
        {
            var node = manager->NodeList[i];
            if (node == null || !visited.Add((nint)node) || !node->IsVisible())
                continue;
            if (node->Type == NodeType.Text)
                result.Add(((AtkTextNode*)node)->NodeText.ToString());
            else if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                    CollectContinuationTexts(&component->UldManager, result, visited, depth + 1);
            }
        }
    }

    private static unsafe void FireContinuationCallback(AtkUnitBase* addon)
    {
        var value = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        addon->FireCallback(1, &value, true);
    }

    private unsafe bool ContinuationUiVisible()
    {
        var menu = (AtkUnitBase*)gameGui.GetAddonByName("DeepDungeonMenu").Address;
        var yesno = (AtkUnitBase*)gameGui.GetAddonByName("SelectYesno").Address;
        return menu != null && menu->IsVisible || yesno != null && yesno->IsVisible;
    }

    private unsafe bool DeepDungeonMenuVisible()
    {
        var menu = (AtkUnitBase*)gameGui.GetAddonByName("DeepDungeonMenu").Address;
        return menu != null && menu->IsVisible;
    }
}
