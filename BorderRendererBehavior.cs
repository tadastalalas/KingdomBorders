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

        // Queued kingdom regeneration from ownership changes
        private HashSet<Kingdom> _pendingRegenKingdoms;
        private HashSet<Settlement> _pendingRegenSettlements;
        private bool _regenRequested;

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
            if (!_isInitialized || _phase != BuildPhase.Idle)
                return;

            ModLog.Log($"Kingdom destroyed: {kingdom.Name} — triggering full rebuild");
            FullRebuild();
        }

        private void OnKingdomCreated(Kingdom kingdom)
        {
            if (!_isInitialized || _phase != BuildPhase.Idle)
                return;

            ModLog.Log($"Kingdom created: {kingdom.Name} — triggering full rebuild");
            FullRebuild();
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
                    if (_regenRequested)
                    {
                        _regenRequested = false;
                        var kingdoms = _pendingRegenKingdoms;
                        var settlements = _pendingRegenSettlements;
                        _pendingRegenKingdoms = null;
                        _pendingRegenSettlements = null;

                        if (kingdoms != null && kingdoms.Count > 0)
                        {
                            RegenerateForKingdoms(kingdoms, settlements);
                        }
                    }
                    else if (_isInitialized)
                    {
                        CheckMCMSettingsChanged();
                    }
                    break;
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
            if (!_isInitialized || _phase != BuildPhase.Idle)
                return;

            var oldKingdom = oldOwner?.Clan?.Kingdom;
            var newKingdom = newOwner?.Clan?.Kingdom;

            if (oldKingdom == null && newKingdom == null)
                return;

            if (_pendingRegenKingdoms == null)
            {
                _pendingRegenKingdoms = new HashSet<Kingdom>();
                _pendingRegenSettlements = new HashSet<Settlement>();
            }

            if (oldKingdom != null) _pendingRegenKingdoms.Add(oldKingdom);
            if (newKingdom != null) _pendingRegenKingdoms.Add(newKingdom);

            // Track the specific settlement and its villages
            _pendingRegenSettlements.Add(settlement);
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
            if (!_isInitialized || _phase != BuildPhase.Idle)
                return;

            if (oldKingdom == null && newKingdom == null)
                return;

            if (_pendingRegenKingdoms == null)
            {
                _pendingRegenKingdoms = new HashSet<Kingdom>();
                _pendingRegenSettlements = new HashSet<Settlement>();
            }

            if (oldKingdom != null) _pendingRegenKingdoms.Add(oldKingdom);
            if (newKingdom != null) _pendingRegenKingdoms.Add(newKingdom);

            // Track all fiefs owned by the clan
            foreach (var s in clan.Settlements)
            {
                if (s.IsTown || s.IsCastle)
                {
                    _pendingRegenSettlements.Add(s);
                    foreach (var village in s.BoundVillages)
                    {
                        if (village?.Settlement != null)
                            _pendingRegenSettlements.Add(village.Settlement);
                    }
                }
            }

            _regenRequested = true;
        }

        private void RegenerateForKingdoms(HashSet<Kingdom> kingdoms, HashSet<Settlement> changedSettlements)
        {
            if (Renderer == null || _calculator == null)
                return;

            ModLog.Log($"Regenerating borders for {kingdoms.Count} kingdoms, {changedSettlements?.Count ?? 0} changed settlements...");

            Renderer.ClearForKingdoms(kingdoms);
            _calculator.RebuildTerritoryGridAroundSettlements(changedSettlements);

            var edges = _calculator.FindBorderEdgesForKingdoms(kingdoms);
            if (edges.Count == 0)
            {
                ModLog.Log("No border edges found for affected kingdoms");
                return;
            }

            var segments = _calculator.ChainEdges(edges);

            // Detect junctions before processing segments
            _junctions = _calculator.FindJunctions(segments);
            _junctionKeys = _calculator.GetJunctionKeys(_junctions);

            _pendingSegments = segments;
            _pendingIndex = 0;
            _renderedCount = 0;
            _skippedCount = 0;
            _kingdomBuilders = new Dictionary<Kingdom, KingdomMeshBuilder>();
            _phase = BuildPhase.ProcessingSegments;

            ModLog.Log($"Queued {segments.Count} segments for incremental regeneration");
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
            var edges = _calculator.FindBorderEdges();
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

                var smoothed = _calculator.SmoothChaikin(segment.Points, iterations: cornerSmoothing);

                // Determine trim amount per endpoint:
                // - At a junction: trim by outerOffset to make room for the join
                // - Not at junction: trim by innerOffset (original behavior)
                // Note: pass-through borders chain straight through junctions and
                // never have endpoints there, so they are unaffected.
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