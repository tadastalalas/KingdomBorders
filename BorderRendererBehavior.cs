using System;
using System.Collections.Generic;
using System.Linq;
using SandBox;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace KingdomBorders
{
    internal enum BuildPhase
    {
        Idle,
        BuildingGrid,
        FindingEdges,
        ProcessingSegments,
        FlushingMeshes
    }

    public class BorderRendererBehavior : CampaignBehaviorBase
    {
        private bool _isInitialized;
        private BorderCalculator _calculator;

        // State machine
        private BuildPhase _phase = BuildPhase.Idle;

        // Incremental generation state
        private List<BorderSegment> _pendingSegments;
        private int _pendingIndex;
        private int _renderedCount;
        private int _skippedCount;
        private const int SegmentsPerTick = 15;
        private const int GridRowsPerTick = 25;

        // Per-kingdom mesh builders collected during incremental processing
        private Dictionary<Kingdom, KingdomMeshBuilder> _kingdomBuilders;

        // Incremental flush state
        private List<KingdomMeshBuilder> _pendingFlush;
        private int _flushIndex;
        private const int MaxFlushStripsPerTick = 30;

        // Queued regeneration — accumulated while builds may be in progress
        private HashSet<Settlement> _pendingRegenSettlements;
        private bool _regenRequested;
        private bool _fullRebuildRequested;

        // Track MCM settings to detect changes and trigger rebuild
        private bool _lastShowOnWater;
        private float _lastBorderWidth;
        private float _lastBorderGap;
        private float _lastHeightOffset;
        private int _lastCornerSmoothing;

        // Junction data for the current build
        private List<JunctionInfo> _junctions;
        private HashSet<long> _junctionKeys;

        public BorderRenderer Renderer { get; private set; }
        public Scene MapScene { get; private set; }

        public override void RegisterEvents()
        {
            CampaignEvents.OnGameLoadFinishedEvent.AddNonSerializedListener(this, OnGameLoadFinished);
            CampaignEvents.OnNewGameCreatedEvent.AddNonSerializedListener(this, OnNewGameCreated);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, OnSettlementOwnerChanged);
            CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(this, OnClanChangedKingdom);
            CampaignEvents.KingdomDestroyedEvent.AddNonSerializedListener(this, OnKingdomDestroyed);
            CampaignEvents.KingdomCreatedEvent.AddNonSerializedListener(this, OnKingdomCreated);
        }

        public override void SyncData(IDataStore dataStore)
        {
        }

        private void OnGameLoadFinished()
        {
            BeginBuildBorders();
        }

        private void OnNewGameCreated(CampaignGameStarter starter)
        {
            BeginBuildBorders();
        }

        private void OnKingdomDestroyed(Kingdom kingdom)
        {
            if (!_isInitialized)
                return;

            // Queue a full rebuild — don't run immediately since the destruction cascade
            // fires OnClanChangedKingdom + OnSettlementOwnerChanged for each clan/fief
            // before this event arrives. A full rebuild after everything settles is correct.
            ModLog.Log($"Kingdom destroyed: {kingdom.Name} — queuing full rebuild");
            _fullRebuildRequested = true;
        }

        private void OnKingdomCreated(Kingdom kingdom)
        {
            if (!_isInitialized)
                return;

            // Queue a full rebuild. CreateKingdom also fires OnClanChangedKingdom
            // so we just coalesce into a single full rebuild.
            ModLog.Log($"Kingdom created: {kingdom.Name} — queuing full rebuild");
            _fullRebuildRequested = true;
        }

        /// <summary>
        /// Called every application frame from SubModule.OnApplicationTick.
        /// Drives the build pipeline regardless of campaign tick state,
        /// so borders generate even while the game is paused on load.
        /// </summary>
        public void ApplicationTick()
        {
            switch (_phase)
            {
                case BuildPhase.BuildingGrid:
                    if (_calculator.BuildTerritoryGridIncremental(GridRowsPerTick))
                    {
                        _phase = BuildPhase.FindingEdges;
                    }
                    break;

                case BuildPhase.FindingEdges:
                    FindEdgesAndChain();
                    break;

                case BuildPhase.ProcessingSegments:
                    ProcessPendingSegments();
                    break;

                case BuildPhase.FlushingMeshes:
                    FlushKingdomMeshesIncremental();
                    break;

                case BuildPhase.Idle:
                    ProcessQueuedRegeneration();
                    break;
            }
        }

        /// <summary>
        /// Processes any queued regeneration requests once the build pipeline is idle.
        /// Full rebuilds take priority over partial ones. Multiple events that arrive
        /// during an active build are coalesced and handled in a single pass here.
        /// </summary>
        private void ProcessQueuedRegeneration()
        {
            if (_fullRebuildRequested)
            {
                _fullRebuildRequested = false;
                _regenRequested = false;
                _pendingRegenSettlements = null;

                ModLog.Log("Processing queued full rebuild");
                FullRebuild();
                return;
            }

            if (_regenRequested)
            {
                _regenRequested = false;
                var settlements = _pendingRegenSettlements;
                _pendingRegenSettlements = null;

                if (settlements != null && settlements.Count > 0)
                {
                    RegenerateForSettlements(settlements);
                }
                return;
            }

            if (_isInitialized)
            {
                CheckMCMSettingsChanged();
            }
        }

        /// <summary>
        /// Detects MCM setting changes that require a full border rebuild.
        /// </summary>
        private void CheckMCMSettingsChanged()
        {
            var settings = MCMSettings.Instance;
            if (settings == null)
                return;

            bool showOnWater = settings.ShowBordersOnWater;
            float borderWidth = settings.BorderWidth;
            float borderGap = settings.BorderGap;
            float heightOffset = settings.HeightOffset;
            int cornerSmoothing = settings.CornerSmoothing;

            if (showOnWater != _lastShowOnWater ||
                Math.Abs(borderWidth - _lastBorderWidth) > 0.001f ||
                Math.Abs(borderGap - _lastBorderGap) > 0.001f ||
                Math.Abs(heightOffset - _lastHeightOffset) > 0.001f ||
                cornerSmoothing != _lastCornerSmoothing)
            {
                ModLog.Log($"MCM settings changed — triggering full rebuild");
                SnapshotMCMSettings();
                FullRebuild();
            }
        }

        private void SnapshotMCMSettings()
        {
            var settings = MCMSettings.Instance;
            if (settings == null)
                return;

            _lastShowOnWater = settings.ShowBordersOnWater;
            _lastBorderWidth = settings.BorderWidth;
            _lastBorderGap = settings.BorderGap;
            _lastHeightOffset = settings.HeightOffset;
            _lastCornerSmoothing = settings.CornerSmoothing;
        }

        /// <summary>
        /// Clears all existing borders and starts a full rebuild from scratch.
        /// </summary>
        private void FullRebuild()
        {
            if (Renderer == null)
                return;

            Renderer.ClearAll();
            _calculator = new BorderCalculator(resolution: 150);
            _calculator.CalculateMapBounds();
            _calculator.BeginBuildTerritoryGrid();
            _phase = BuildPhase.BuildingGrid;
        }

        private void OnSettlementOwnerChanged(Settlement settlement, bool openToClaim,
            Hero newOwner, Hero oldOwner, Hero capturerHero,
            ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            if (!_isInitialized)
                return;

            var oldKingdom = oldOwner?.Clan?.Kingdom;
            var newKingdom = newOwner?.Clan?.Kingdom;

            // Skip if neither side belongs to a kingdom — no visible borders affected
            if (oldKingdom == null && newKingdom == null)
                return;

            // Skip villages — their bound town/castle changing hands already covers them,
            // and we add bound villages explicitly below when tracking a town/castle
            if (settlement.IsVillage)
                return;

            if (_pendingRegenSettlements == null)
                _pendingRegenSettlements = new HashSet<Settlement>();

            _pendingRegenSettlements.Add(settlement);

            // Include bound villages since they use the parent's kingdom for grid ownership
            if (settlement.BoundVillages != null)
            {
                foreach (var village in settlement.BoundVillages)
                {
                    if (village?.Settlement != null)
                        _pendingRegenSettlements.Add(village.Settlement);
                }
            }

            _regenRequested = true;
        }

        private void OnClanChangedKingdom(Clan clan, Kingdom oldKingdom, Kingdom newKingdom,
            ChangeKingdomAction.ChangeKingdomActionDetail detail, bool showNotification)
        {
            if (!_isInitialized)
                return;

            // --- Filter out events that don't affect borders ---

            // Mercenary clans never own fiefs, so joining/leaving as mercenary changes no borders.
            if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.JoinAsMercenary ||
                detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveAsMercenary)
            {
                return;
            }

            // LeaveKingdom: the game transfers each fief via ChangeOwnerOfSettlementAction
            // before this event fires, so OnSettlementOwnerChanged already covers everything.
            if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveKingdom)
                return;

            // LeaveByClanDestruction: same as above — fiefs are transferred to an heir clan
            // via ChangeOwnerOfSettlementAction before this fires.
            if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveByClanDestruction)
                return;

            // LeaveByKingdomDestruction: fiefs transferred before this fires, and
            // KingdomDestroyedEvent will queue a full rebuild separately.
            if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveByKingdomDestruction)
                return;

            // CreateKingdom: KingdomCreatedEvent will queue a full rebuild separately.
            if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.CreateKingdom)
                return;

            // --- Remaining cases that DO affect borders ---
            // JoinKingdom: clan with fiefs joins another kingdom (fiefs move with them, no
            //   OnSettlementOwnerChanged fires because the clan still owns the settlements).
            // JoinKingdomByDefection: same as JoinKingdom.
            // LeaveWithRebellion: clan keeps fiefs but is now independent.

            if (oldKingdom == null && newKingdom == null)
                return;

            // Check if this clan actually owns any fiefs — if not, no borders change
            bool hasRelevantFiefs = false;
            foreach (var s in clan.Settlements)
            {
                if (s.IsTown || s.IsCastle)
                {
                    hasRelevantFiefs = true;
                    break;
                }
            }

            if (!hasRelevantFiefs)
                return;

            if (_pendingRegenSettlements == null)
                _pendingRegenSettlements = new HashSet<Settlement>();

            foreach (var s in clan.Settlements)
            {
                if (s.IsTown || s.IsCastle)
                {
                    _pendingRegenSettlements.Add(s);
                    if (s.BoundVillages != null)
                    {
                        foreach (var village in s.BoundVillages)
                        {
                            if (village?.Settlement != null)
                                _pendingRegenSettlements.Add(village.Settlement);
                        }
                    }
                }
            }

            _regenRequested = true;
            ModLog.Log($"Clan {clan.Name} changed kingdom ({detail}) with {clan.Settlements.Count(s => s.IsTown || s.IsCastle)} fiefs — queued regen");
        }

        private void RegenerateForSettlements(HashSet<Settlement> changedSettlements)
        {
            if (Renderer == null || _calculator == null)
                return;

            ModLog.Log($"Regenerating borders for {changedSettlements.Count} changed settlements...");

            // Rebuild the grid around changed settlements (fast partial rebuild).
            // This also re-detects exclaves on the full grid.
            _calculator.RebuildTerritoryGridAroundSettlements(changedSettlements);

            // Clear ALL border entities and rebuild everything from the updated grid.
            // We can't selectively clear per-kingdom because each entity contains ALL
            // strips for that kingdom — clearing a neighbor's entity destroys its borders
            // with unrelated kingdoms that we wouldn't rebuild.
            // The grid is already built, so re-entering FindingEdges only redoes
            // edge detection + incremental segment processing, which is cheap.
            Renderer.ClearAll();
            _phase = BuildPhase.FindingEdges;

            ModLog.Log("Cleared all borders — re-entering edge finding from updated grid");
        }

        private void BeginBuildBorders()
        {
            _isInitialized = true;
            ModLog.Clear();
            ModLog.Log("=== Kingdom Border Renderer Starting ===");

            try
            {
                var mapScene = Campaign.Current?.MapSceneWrapper as MapScene;
                if (mapScene == null)
                {
                    ModLog.Log("FAIL: Could not get MapScene");
                    return;
                }
                MapScene = mapScene.Scene;
                if (MapScene == null)
                {
                    ModLog.Log("FAIL: Scene is null");
                    return;
                }
                ModLog.Log("Scene obtained");

                Renderer = new BorderRenderer(MapScene);

                var kingdoms = Kingdom.All;
                ModLog.Log($"Active kingdoms: {kingdoms.Count}");
                foreach (var k in kingdoms)
                {
                    uint col = BorderCalculator.GetKingdomColor(k);
                    ModLog.Log($"  {k.Name}: color=0x{col:X8}, fiefs={k.Fiefs.Count}");
                }

                SnapshotMCMSettings();

                _calculator = new BorderCalculator(resolution: 150);
                _calculator.CalculateMapBounds();
                _calculator.BeginBuildTerritoryGrid();

                _phase = BuildPhase.BuildingGrid;
            }
            catch (Exception ex)
            {
                ModLog.Log($"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                ModLog.Log(ex.StackTrace ?? "No stack trace");
            }
        }

        private void FindEdgesAndChain()
        {
            // Get main border edges from the cleaned grid (exclaves already removed)
            var edges = _calculator.FindBorderEdges();

            // Get exclave border edges (closed loops around each exclave)
            var exclaveEdges = _calculator.FindExclaveBorderEdges();
            edges.AddRange(exclaveEdges);

            if (edges.Count == 0)
            {
                ModLog.Log("No border edges found — nothing to render");
                _phase = BuildPhase.Idle;
                return;
            }

            var segments = _calculator.ChainEdges(edges);

            // Detect junctions before processing segments
            _junctions = _calculator.FindJunctions(segments);
            _junctionKeys = _calculator.GetJunctionKeys(_junctions);

            ModLog.Log($"Queuing {segments.Count} border segments for incremental rendering...");

            _pendingSegments = segments;
            _pendingIndex = 0;
            _renderedCount = 0;
            _skippedCount = 0;
            _kingdomBuilders = new Dictionary<Kingdom, KingdomMeshBuilder>();
            _phase = BuildPhase.ProcessingSegments;
        }

        private KingdomMeshBuilder GetBuilder(Kingdom kingdom)
        {
            if (!_kingdomBuilders.TryGetValue(kingdom, out var builder))
            {
                builder = new KingdomMeshBuilder
                {
                    Kingdom = kingdom,
                    Color = BorderCalculator.GetKingdomColor(kingdom)
                };
                _kingdomBuilders[kingdom] = builder;
            }
            return builder;
        }

        private void ProcessPendingSegments()
        {
            int processed = 0;

            // Read MCM settings for border dimensions
            float borderGap = MCMSettings.Instance?.BorderGap ?? 0.30f;
            float borderWidth = MCMSettings.Instance?.BorderWidth ?? 1.05f;
            int cornerSmoothing = MCMSettings.Instance?.CornerSmoothing ?? 3;
            float innerOffset = borderGap / 2f;
            float outerOffset = innerOffset + borderWidth;

            while (_pendingIndex < _pendingSegments.Count && processed < SegmentsPerTick)
            {
                var segment = _pendingSegments[_pendingIndex];
                _pendingIndex++;
                processed++;

                if (segment.Points.Count < 2)
                {
                    _skippedCount++;
                    continue;
                }

                if (segment.KingdomA == null || segment.KingdomB == null)
                {
                    _skippedCount++;
                    continue;
                }

                // === Closed loop (exclave) processing ===
                if (segment.IsClosedLoop)
                {
                    ProcessClosedLoopSegment(segment, innerOffset, outerOffset, cornerSmoothing);
                    continue;
                }

                // === Open segment processing (unchanged) ===
                var smoothed = _calculator.SmoothChaikin(segment.Points, iterations: cornerSmoothing);

                // Determine trim amount per endpoint:
                // - At a junction: trim by outerOffset to make room for the join
                // - Not at junction: trim by innerOffset (original behavior)
                var headArm = _calculator.FindArmAtJunction(segment, true, _junctions, _junctionKeys);
                var tailArm = _calculator.FindArmAtJunction(segment, false, _junctions, _junctionKeys);

                float headTrim = headArm != null ? outerOffset : innerOffset;
                float tailTrim = tailArm != null ? outerOffset : innerOffset;

                smoothed = BorderCalculator.TrimPolylineEnds(smoothed, headTrim, tailTrim);

                if (smoothed.Count < 2)
                {
                    _skippedCount++;
                    continue;
                }

                var (leftKingdom, rightKingdom) = _calculator.DetermineKingdomSides(
                    smoothed, segment.KingdomA, segment.KingdomB);

                var leftLineInner = BorderCalculator.OffsetPolyline(smoothed, innerOffset);
                var leftLineOuter = BorderCalculator.OffsetPolyline(smoothed, outerOffset);
                var rightLineInner = BorderCalculator.OffsetPolyline(smoothed, -innerOffset);
                var rightLineOuter = BorderCalculator.OffsetPolyline(smoothed, -outerOffset);

                GetBuilder(leftKingdom).Strips.Add((leftLineInner, leftLineOuter));
                GetBuilder(rightKingdom).Strips.Add((rightLineInner, rightLineOuter));

                // Compute outward direction at each junction endpoint.
                // "Outward" = direction the segment travels AWAY from the junction.
                if (headArm != null)
                {
                    headArm.LeftKingdom = leftKingdom;
                    headArm.RightKingdom = rightKingdom;
                    headArm.LeftInnerPt = leftLineInner[0];
                    headArm.LeftOuterPt = leftLineOuter[0];
                    headArm.RightInnerPt = rightLineInner[0];
                    headArm.RightOuterPt = rightLineOuter[0];
                    // Head is at index 0, so outward = from index 0 toward index 1
                    Vec2 headDir = (smoothed[1] - smoothed[0]).Normalized();
                    headArm.OutwardDir = headDir;
                }

                if (tailArm != null)
                {
                    tailArm.LeftKingdom = leftKingdom;
                    tailArm.RightKingdom = rightKingdom;
                    int lastIdx = leftLineInner.Count - 1;
                    tailArm.LeftInnerPt = leftLineInner[lastIdx];
                    tailArm.LeftOuterPt = leftLineOuter[lastIdx];
                    tailArm.RightInnerPt = rightLineInner[lastIdx];
                    tailArm.RightOuterPt = rightLineOuter[lastIdx];
                    // Tail is at last index, so outward = from last-1 toward last
                    int lastSmoothed = smoothed.Count - 1;
                    Vec2 tailDir = (smoothed[lastSmoothed] - smoothed[lastSmoothed - 1]).Normalized();
                    tailArm.OutwardDir = tailDir;
                }

                _renderedCount += 2;
            }

            if (_pendingIndex >= _pendingSegments.Count)
            {
                // Generate junction join caps before flushing
                GenerateJunctionJoins(innerOffset, outerOffset, cornerSmoothing);

                _pendingSegments = null;
                _junctions = null;
                _junctionKeys = null;

                _pendingFlush = new List<KingdomMeshBuilder>(_kingdomBuilders.Values);
                _flushIndex = 0;
                _kingdomBuilders = null;
                _phase = BuildPhase.FlushingMeshes;

                ModLog.Log($"Processed {_renderedCount} strips, skipped {_skippedCount} segments. Flushing {_pendingFlush.Count} kingdom meshes...");
            }
        }

        /// <summary>
        /// Processes a closed-loop exclave segment. Uses closed-loop smoothing and
        /// closed-loop offset so all corners are smooth with no gap at the seam.
        /// Only draws the exclave kingdom's strip — the surrounding kingdom's strip
        /// is suppressed because the main border already covers that area.
        /// </summary>
        private void ProcessClosedLoopSegment(BorderSegment segment, float innerOffset, float outerOffset, int cornerSmoothing)
        {
            var smoothed = _calculator.SmoothChaikinClosed(segment.Points, iterations: cornerSmoothing);

            if (smoothed.Count < 3)
            {
                _skippedCount++;
                return;
            }

            // No trimming for closed loops — they have no endpoints

            var (leftKingdom, rightKingdom) = _calculator.DetermineKingdomSides(
                smoothed, segment.KingdomA, segment.KingdomB);

            // Use closed-loop offset so the seam point gets properly averaged perpendiculars
            var leftLineInner = BorderCalculator.OffsetPolylineClosed(smoothed, innerOffset);
            var leftLineOuter = BorderCalculator.OffsetPolylineClosed(smoothed, outerOffset);
            var rightLineInner = BorderCalculator.OffsetPolylineClosed(smoothed, -innerOffset);
            var rightLineOuter = BorderCalculator.OffsetPolylineClosed(smoothed, -outerOffset);

            // Close the loops by appending the first point at the end
            leftLineInner.Add(leftLineInner[0]);
            leftLineOuter.Add(leftLineOuter[0]);
            rightLineInner.Add(rightLineInner[0]);
            rightLineOuter.Add(rightLineOuter[0]);

            // Determine which side the exclave kingdom is on.
            // Only draw the exclave kingdom's strip to avoid overlap with the main border.
            Kingdom exclaveKingdom = segment.ExclaveKingdom;

            if (exclaveKingdom != null)
            {
                // Only draw the exclave kingdom's border strip
                if (leftKingdom == exclaveKingdom)
                {
                    GetBuilder(leftKingdom).Strips.Add((leftLineInner, leftLineOuter));
                    _renderedCount++;
                }
                else if (rightKingdom == exclaveKingdom)
                {
                    GetBuilder(rightKingdom).Strips.Add((rightLineInner, rightLineOuter));
                    _renderedCount++;
                }
            }
            else
            {
                // Fallback: draw both sides (shouldn't normally happen for exclaves)
                GetBuilder(leftKingdom).Strips.Add((leftLineInner, leftLineOuter));
                GetBuilder(rightKingdom).Strips.Add((rightLineInner, rightLineOuter));
                _renderedCount += 2;
            }
        }

        /// <summary>
        /// For each junction, finds pairs of arms that share a kingdom and generates
        /// a smooth bevel/miter join connecting their strip endpoints.
        /// </summary>
        private void GenerateJunctionJoins(float innerOffset, float outerOffset, int cornerSmoothing)
        {
            if (_junctions == null || _junctions.Count == 0)
                return;

            int curveSegments = Math.Max(3, cornerSmoothing * 3);
            int joinsGenerated = 0;

            foreach (var junction in _junctions)
            {
                if (junction.Arms.Count < 2)
                    continue;

                // Collect all kingdoms present at this junction
                var kingdomsAtJunction = new HashSet<Kingdom>();
                foreach (var arm in junction.Arms)
                {
                    if (arm.LeftKingdom != null) kingdomsAtJunction.Add(arm.LeftKingdom);
                    if (arm.RightKingdom != null) kingdomsAtJunction.Add(arm.RightKingdom);
                }

                foreach (var kingdom in kingdomsAtJunction)
                {
                    if (kingdom == null)
                        continue;

                    // Collect strip endpoints and outward directions for this kingdom
                    var stripEnds = new List<(Vec2 inner, Vec2 outer, Vec2 outwardDir, float angle)>();

                    foreach (var arm in junction.Arms)
                    {
                        if (arm.LeftKingdom == kingdom)
                        {
                            Vec2 mid = (arm.LeftInnerPt + arm.LeftOuterPt) * 0.5f;
                            float angle = (float)Math.Atan2(
                                mid.y - junction.Position.y,
                                mid.x - junction.Position.x);
                            stripEnds.Add((arm.LeftInnerPt, arm.LeftOuterPt, arm.OutwardDir, angle));
                        }
                        else if (arm.RightKingdom == kingdom)
                        {
                            Vec2 mid = (arm.RightInnerPt + arm.RightOuterPt) * 0.5f;
                            float angle = (float)Math.Atan2(
                                mid.y - junction.Position.y,
                                mid.x - junction.Position.x);
                            stripEnds.Add((arm.RightInnerPt, arm.RightOuterPt, arm.OutwardDir, angle));
                        }
                    }

                    if (stripEnds.Count < 2)
                        continue;

                    // Sort by angle around the junction center
                    stripEnds.Sort((a, b) => a.angle.CompareTo(b.angle));

                    if (stripEnds.Count == 2)
                    {
                        var join = BorderCalculator.GenerateJunctionJoin(
                            stripEnds[0].inner, stripEnds[0].outer, stripEnds[0].outwardDir,
                            stripEnds[1].inner, stripEnds[1].outer, stripEnds[1].outwardDir,
                            curveSegments);

                        if (join.inner != null && join.inner.Count >= 2)
                        {
                            GetBuilder(kingdom).Strips.Add((join.inner, join.outer));
                            joinsGenerated++;
                        }
                    }
                    else
                    {
                        // 3+ strip endpoints: connect adjacent pairs around the junction
                        for (int i = 0; i < stripEnds.Count; i++)
                        {
                            int next = (i + 1) % stripEnds.Count;

                            var join = BorderCalculator.GenerateJunctionJoin(
                                stripEnds[i].inner, stripEnds[i].outer, stripEnds[i].outwardDir,
                                stripEnds[next].inner, stripEnds[next].outer, stripEnds[next].outwardDir,
                                curveSegments);

                            if (join.inner != null && join.inner.Count >= 2)
                            {
                                GetBuilder(kingdom).Strips.Add((join.inner, join.outer));
                                joinsGenerated++;
                            }
                        }
                    }
                }
            }

            ModLog.Log($"Generated {joinsGenerated} junction join strips");
        }

        private void FlushKingdomMeshesIncremental()
        {
            int stripsThisTick = 0;
            float heightOffset = MCMSettings.Instance?.HeightOffset ?? 0.55f;

            while (_flushIndex < _pendingFlush.Count)
            {
                var builder = _pendingFlush[_flushIndex];

                if (stripsThisTick > 0 && stripsThisTick + builder.Strips.Count > MaxFlushStripsPerTick)
                    break;

                _flushIndex++;
                stripsThisTick += builder.Strips.Count;

                var entity = Renderer.RenderKingdomStrips(builder, heightOffset);
                if (entity != null)
                {
                    ModLog.Log($"  {builder.Kingdom.Name}: {builder.Strips.Count} strips");
                }
            }

            if (_flushIndex >= _pendingFlush.Count)
            {
                ModLog.Log($"Total entities: {Renderer.EntityCount}");
                ModLog.Log("=== Kingdom Border Renderer Done ===");

                _pendingFlush = null;
                _phase = BuildPhase.Idle;
            }
        }

        public void Cleanup()
        {
            if (Renderer != null)
            {
                ModLog.Log("Cleaning up border entities...");
                Renderer.ClearAll();
                Renderer = null;
            }
            _isInitialized = false;
            _phase = BuildPhase.Idle;
        }
    }
}