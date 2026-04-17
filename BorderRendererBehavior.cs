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
        FlushingMeshes,
        BuildingFill
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

        // Incremental fill build state
        private List<Kingdom> _pendingFillOrder;
        private Dictionary<Kingdom, List<(int gx, int gy)>> _pendingFillCells;
        private int _fillIndex;
        private const int FillKingdomsPerTick = 1;

        // Queued regeneration — accumulated while builds may be in progress
        private HashSet<Settlement> _pendingRegenSettlements;
        private HashSet<Kingdom> _pendingRegenKingdoms;

        private bool _regenRequested;
        private bool _fullRebuildRequested;

        // Fill-only rebuild request (e.g. triggered by MCM ShowTerritoryFill toggle).
        private bool _fillRebuildRequested;

        // Track MCM settings to detect changes and trigger rebuild
        private bool _lastShowOnWater;
        private float _lastBorderWidth;
        private float _lastBorderGap;
        private float _lastHeightOffset;
        private int _lastCornerSmoothing;
        private bool _lastShowTerritoryFill;

        // Junction data for the current build
        private List<JunctionInfo> _junctions;
        private HashSet<long> _junctionKeys;

        // Persists completed strip geometry per kingdom so partial regeneration
        // only rebuilds affected kingdoms, leaving the rest untouched.
        private Dictionary<Kingdom, KingdomMeshBuilder> _cachedBuilders;

        private HashSet<Kingdom> _affectedKingdoms;
        private HashSet<long> _preservedStripKeys;

        public BorderRenderer Renderer { get; private set; }
        public TerritoryFillRenderer FillRenderer { get; private set; }
        public Scene MapScene { get; private set; }

        public override void RegisterEvents()
        {
            CampaignEvents.OnGameLoadFinishedEvent.AddNonSerializedListener(this, OnGameLoadFinished);
            CampaignEvents.OnNewGameCreatedEvent.AddNonSerializedListener(this, OnNewGameCreated);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, OnSettlementOwnerChanged);
            CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(this, OnClanChangedKingdom);
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

                case BuildPhase.BuildingFill:
                    ProcessFillBuildIncremental();
                    break;

                case BuildPhase.Idle:
                    ProcessQueuedRegeneration();
                    break;
            }
        }

        private void ProcessQueuedRegeneration()
        {
            if (_fullRebuildRequested)
            {
                _fullRebuildRequested = false;
                _regenRequested = false;
                _fillRebuildRequested = false;
                _pendingRegenSettlements = null;
                _pendingRegenKingdoms = null;

                ModLog.Log("Processing queued full rebuild");
                FullRebuild();
                return;
            }

            if (_regenRequested)
            {
                _regenRequested = false;
                var settlements = _pendingRegenSettlements;
                var kingdoms = _pendingRegenKingdoms;
                _pendingRegenSettlements = null;
                _pendingRegenKingdoms = null;

                bool hasSettlements = settlements != null && settlements.Count > 0;
                bool hasKingdoms = kingdoms != null && kingdoms.Count > 0;

                if (hasSettlements || hasKingdoms)
                {
                    RegenerateForSettlements(settlements, kingdoms);
                }
                return;
            }

            if (_fillRebuildRequested)
            {
                _fillRebuildRequested = false;
                BeginFillOnlyRebuild();
                return;
            }

            if (_isInitialized)
            {
                CheckMCMSettingsChanged();
            }
        }

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
            bool showFill = settings.ShowTerritoryFill;

            bool borderRebuildNeeded =
                showOnWater != _lastShowOnWater ||
                Math.Abs(borderWidth - _lastBorderWidth) > 0.001f ||
                Math.Abs(borderGap - _lastBorderGap) > 0.001f ||
                Math.Abs(heightOffset - _lastHeightOffset) > 0.001f ||
                cornerSmoothing != _lastCornerSmoothing;

            if (borderRebuildNeeded)
            {
                ModLog.Log("MCM settings changed — triggering full rebuild");
                SnapshotMCMSettings();
                FullRebuild();
                return;
            }

            if (showFill != _lastShowTerritoryFill)
            {
                _lastShowTerritoryFill = showFill;
                if (showFill)
                {
                    ModLog.Log("MCM: ShowTerritoryFill enabled — building fills");
                    _fillRebuildRequested = true;
                }
                else
                {
                    ModLog.Log("MCM: ShowTerritoryFill disabled — clearing fills");
                    FillRenderer?.ClearAll();
                }
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
            _lastShowTerritoryFill = settings.ShowTerritoryFill;
        }

        private void FullRebuild()
        {
            if (Renderer == null)
                return;

            Renderer.ClearAll();
            FillRenderer?.ClearAll();
            _cachedBuilders = null;
            _affectedKingdoms = null;
            _preservedStripKeys = null;
            _calculator = new BorderCalculator(resolution: 150);
            _calculator.CalculateMapBounds();
            _calculator.BeginBuildTerritoryGrid();
            _phase = BuildPhase.BuildingGrid;
        }

        private void QueueAffectedKingdom(Kingdom kingdom)
        {
            if (kingdom == null)
                return;

            if (_pendingRegenKingdoms == null)
                _pendingRegenKingdoms = new HashSet<Kingdom>();

            _pendingRegenKingdoms.Add(kingdom);
        }

        private void OnSettlementOwnerChanged(Settlement settlement, bool openToClaim,
            Hero newOwner, Hero oldOwner, Hero capturerHero,
            ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            if (!_isInitialized)
                return;

            if (detail == ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.ByLeaveFaction)
                return;

            var oldKingdom = oldOwner?.Clan?.Kingdom;
            var newKingdom = newOwner?.Clan?.Kingdom;

            if (oldKingdom == null && newKingdom == null)
                return;

            if (oldKingdom != null && newKingdom != null && oldKingdom == newKingdom)
                return;

            if (settlement.IsVillage)
                return;

            if (_pendingRegenSettlements == null)
                _pendingRegenSettlements = new HashSet<Settlement>();

            _pendingRegenSettlements.Add(settlement);

            if (settlement.BoundVillages != null)
            {
                foreach (var village in settlement.BoundVillages)
                {
                    if (village?.Settlement != null)
                        _pendingRegenSettlements.Add(village.Settlement);
                }
            }

            QueueAffectedKingdom(oldKingdom);
            QueueAffectedKingdom(newKingdom);

            _regenRequested = true;
        }

        private void OnClanChangedKingdom(Clan clan, Kingdom oldKingdom, Kingdom newKingdom,
            ChangeKingdomAction.ChangeKingdomActionDetail detail, bool showNotification)
        {
            if (!_isInitialized)
                return;

            if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.JoinAsMercenary ||
                detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveAsMercenary)
            {
                return;
            }

            if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveKingdom)
                return;

            if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveByClanDestruction)
                return;

            if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveByKingdomDestruction)
                return;

            if (oldKingdom == null && newKingdom == null)
                return;

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

            int fiefCount = 0;
            foreach (var s in clan.Settlements)
            {
                if (s.IsTown || s.IsCastle)
                {
                    _pendingRegenSettlements.Add(s);
                    fiefCount++;
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

            QueueAffectedKingdom(oldKingdom);
            QueueAffectedKingdom(newKingdom);

            _regenRequested = true;
            ModLog.Log($"Clan {clan.Name} changed kingdom ({detail}) with {fiefCount} fiefs — queued regen");
        }

        private void RegenerateForSettlements(HashSet<Settlement> changedSettlements, HashSet<Kingdom> explicitlyAffectedKingdoms)
        {
            if (Renderer == null || _calculator == null)
                return;

            if (_cachedBuilders == null)
            {
                ModLog.Log("No cached builders — falling back to full rebuild");
                FullRebuild();
                return;
            }

            int settlementCount = changedSettlements?.Count ?? 0;
            int explicitKingdomCount = explicitlyAffectedKingdoms?.Count ?? 0;
            ModLog.Log($"Regenerating borders for {settlementCount} changed settlements " +
                       $"and {explicitKingdomCount} explicitly queued kingdoms...");

            bool gridChanged;
            if (settlementCount > 0)
            {
                gridChanged = _calculator.RebuildTerritoryGridAroundSettlements(changedSettlements);
            }
            else
            {
                gridChanged = _calculator.RebuildTerritoryGridForKingdoms(explicitlyAffectedKingdoms);
            }

            if (!gridChanged)
            {
                ModLog.Log("Grid unchanged after partial rebuild — skipping regen");
                _preservedStripKeys = null;
                _phase = BuildPhase.Idle;
                return;
            }

            var directlyAffected = new HashSet<Kingdom>();
            if (explicitlyAffectedKingdoms != null)
            {
                foreach (var k in explicitlyAffectedKingdoms)
                {
                    if (k != null)
                        directlyAffected.Add(k);
                }
            }

            if (changedSettlements != null)
            {
                foreach (var s in changedSettlements)
                {
                    Kingdom kingdom;
                    if (s.IsVillage)
                        kingdom = s.Village?.Bound?.OwnerClan?.Kingdom;
                    else
                        kingdom = s.OwnerClan?.Kingdom;

                    if (kingdom != null)
                        directlyAffected.Add(kingdom);
                }
            }

            var edges = _calculator.FindBorderEdges();
            var exclaveEdges = _calculator.FindExclaveBorderEdges();
            edges.AddRange(exclaveEdges);

            if (edges.Count == 0)
            {
                ModLog.Log("No border edges found after partial grid rebuild");
                Renderer.ClearAll();
                FillRenderer?.ClearAll();
                _cachedBuilders.Clear();
                _preservedStripKeys = null;
                _phase = BuildPhase.Idle;
                return;
            }

            var segments = _calculator.ChainEdges(edges);

            _affectedKingdoms = new HashSet<Kingdom>(directlyAffected);
            foreach (var seg in segments)
            {
                if (seg.KingdomA == null || seg.KingdomB == null)
                    continue;

                bool aAffected = directlyAffected.Contains(seg.KingdomA);
                bool bAffected = directlyAffected.Contains(seg.KingdomB);

                if (aAffected && seg.KingdomB != null)
                    _affectedKingdoms.Add(seg.KingdomB);
                if (bAffected && seg.KingdomA != null)
                    _affectedKingdoms.Add(seg.KingdomA);
            }

            var kingdomsInSegments = new HashSet<Kingdom>();
            foreach (var seg in segments)
            {
                if (seg.KingdomA != null) kingdomsInSegments.Add(seg.KingdomA);
                if (seg.KingdomB != null) kingdomsInSegments.Add(seg.KingdomB);
            }
            foreach (var cachedKingdom in new List<Kingdom>(_cachedBuilders.Keys))
            {
                if (!kingdomsInSegments.Contains(cachedKingdom) && directlyAffected.Contains(cachedKingdom))
                {
                    _affectedKingdoms.Add(cachedKingdom);
                }
            }

            ModLog.Log($"Affected kingdoms: {_affectedKingdoms.Count} " +
                        $"(directly: {directlyAffected.Count}, " +
                        $"expanded with neighbors: {_affectedKingdoms.Count - directlyAffected.Count})");
            foreach (var k in _affectedKingdoms)
                ModLog.Log($"  - {k.Name}");

            if (_calculator.HasLastRebuildBounds)
            {
                float borderGap = MCMSettings.Instance?.BorderGap ?? 0.30f;
                float borderWidth = MCMSettings.Instance?.BorderWidth ?? 1.05f;

                float stripMargin = (borderGap / 2f) + borderWidth + 1f;

                float minX = _calculator.LastRebuildMinX - stripMargin;
                float minY = _calculator.LastRebuildMinY - stripMargin;
                float maxX = _calculator.LastRebuildMaxX + stripMargin;
                float maxY = _calculator.LastRebuildMaxY + stripMargin;

                _preservedStripKeys = Renderer.ClearForKingdomsInBounds(
                    _affectedKingdoms, minX, minY, maxX, maxY);
            }
            else
            {
                Renderer.ClearForKingdoms(_affectedKingdoms);
                _preservedStripKeys = null;
            }

            foreach (var k in _affectedKingdoms)
                _cachedBuilders.Remove(k);

            _junctions = _calculator.FindJunctions(segments);
            _junctionKeys = _calculator.GetJunctionKeys(_junctions);

            ModLog.Log($"Queuing {segments.Count} border segments " +
                       $"(preserved {_preservedStripKeys?.Count ?? 0} strips by AABB cull)...");

            _pendingSegments = segments;
            _pendingIndex = 0;
            _renderedCount = 0;
            _skippedCount = 0;
            _kingdomBuilders = new Dictionary<Kingdom, KingdomMeshBuilder>();
            _phase = BuildPhase.ProcessingSegments;
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
                FillRenderer = new TerritoryFillRenderer(MapScene);

                var kingdoms = Kingdom.All;
                ModLog.Log($"Active kingdoms: {kingdoms.Count}");
                foreach (var k in kingdoms)
                {
                    uint col = BorderCalculator.GetKingdomColor(k);
                    ModLog.Log($"  {k.Name}: color=0x{col:X8}, fiefs={k.Fiefs.Count}");
                }

                SnapshotMCMSettings();

                _cachedBuilders = null;
                _affectedKingdoms = null;
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

            var exclaveEdges = _calculator.FindExclaveBorderEdges();
            edges.AddRange(exclaveEdges);

            if (edges.Count == 0)
            {
                ModLog.Log("No border edges found — nothing to render");
                // Still kick off fill — map with a single kingdom has no borders but has fill.
                BeginFillBuild(_affectedKingdoms);
                return;
            }

            var segments = _calculator.ChainEdges(edges);

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

        private bool ShouldProcessSegment(BorderSegment segment)
        {
            if (_affectedKingdoms == null)
                return true;

            if (segment.IsClosedLoop && segment.ExclaveKingdom != null)
            {
                return _affectedKingdoms.Contains(segment.ExclaveKingdom);
            }

            bool aAffected = segment.KingdomA != null && _affectedKingdoms.Contains(segment.KingdomA);
            bool bAffected = segment.KingdomB != null && _affectedKingdoms.Contains(segment.KingdomB);

            return aAffected || bAffected;
        }

        private bool IsKingdomAffected(Kingdom kingdom)
        {
            if (_affectedKingdoms == null)
                return true;

            return _affectedKingdoms.Contains(kingdom);
        }

        private void ProcessPendingSegments()
        {
            int processed = 0;

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

                bool isExclave = segment.IsClosedLoop && segment.ExclaveKingdom != null;
                if (!isExclave && (segment.KingdomA == null || segment.KingdomB == null))
                {
                    _skippedCount++;
                    continue;
                }

                if (!ShouldProcessSegment(segment))
                {
                    _skippedCount++;
                    continue;
                }

                if (segment.IsClosedLoop)
                {
                    ProcessClosedLoopSegment(segment, innerOffset, outerOffset, cornerSmoothing);
                    continue;
                }

                var smoothed = _calculator.SmoothChaikin(segment.Points, iterations: cornerSmoothing);

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

                if (IsKingdomAffected(leftKingdom))
                {
                    GetBuilder(leftKingdom).Strips.Add((leftLineInner, leftLineOuter));
                    _renderedCount++;
                }

                if (IsKingdomAffected(rightKingdom))
                {
                    GetBuilder(rightKingdom).Strips.Add((rightLineInner, rightLineOuter));
                    _renderedCount++;
                }

                if (headArm != null)
                {
                    headArm.LeftKingdom = leftKingdom;
                    headArm.RightKingdom = rightKingdom;
                    headArm.LeftInnerPt = leftLineInner[0];
                    headArm.LeftOuterPt = leftLineOuter[0];
                    headArm.RightInnerPt = rightLineInner[0];
                    headArm.RightOuterPt = rightLineOuter[0];
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
                    int lastSmoothed = smoothed.Count - 1;
                    Vec2 tailDir = (smoothed[lastSmoothed] - smoothed[lastSmoothed - 1]).Normalized();
                    tailArm.OutwardDir = tailDir;
                }
            }

            if (_pendingIndex >= _pendingSegments.Count)
            {
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

        private void ProcessClosedLoopSegment(BorderSegment segment, float innerOffset, float outerOffset, int cornerSmoothing)
        {
            var smoothed = _calculator.SmoothChaikinClosed(segment.Points, iterations: cornerSmoothing);

            if (smoothed.Count < 3)
            {
                _skippedCount++;
                return;
            }

            var (leftKingdom, rightKingdom) = _calculator.DetermineKingdomSides(
                smoothed, segment.KingdomA, segment.KingdomB);

            var leftLineInner = BorderCalculator.OffsetPolylineClosed(smoothed, innerOffset);
            var leftLineOuter = BorderCalculator.OffsetPolylineClosed(smoothed, outerOffset);
            var rightLineInner = BorderCalculator.OffsetPolylineClosed(smoothed, -innerOffset);
            var rightLineOuter = BorderCalculator.OffsetPolylineClosed(smoothed, -outerOffset);

            leftLineInner.Add(leftLineInner[0]);
            leftLineOuter.Add(leftLineOuter[0]);
            rightLineInner.Add(rightLineInner[0]);
            rightLineOuter.Add(rightLineOuter[0]);

            Kingdom exclaveKingdom = segment.ExclaveKingdom;

            if (exclaveKingdom != null)
            {
                if (leftKingdom == exclaveKingdom && IsKingdomAffected(leftKingdom))
                {
                    GetBuilder(leftKingdom).Strips.Add((leftLineInner, leftLineOuter));
                    _renderedCount++;
                }
                else if (rightKingdom == exclaveKingdom && IsKingdomAffected(rightKingdom))
                {
                    GetBuilder(rightKingdom).Strips.Add((rightLineInner, rightLineOuter));
                    _renderedCount++;
                }
            }
            else
            {
                if (leftKingdom != null && IsKingdomAffected(leftKingdom))
                {
                    GetBuilder(leftKingdom).Strips.Add((leftLineInner, leftLineOuter));
                    _renderedCount++;
                }
                if (rightKingdom != null && IsKingdomAffected(rightKingdom))
                {
                    GetBuilder(rightKingdom).Strips.Add((rightLineInner, rightLineOuter));
                    _renderedCount++;
                }
            }
        }

        private void GenerateJunctionJoins(float innerOffset, float outerOffset, int cornerSmoothing)
        {
            if (_junctions == null || _junctions.Count == 0)
                return;

            int curveSegments = Math.Max(3, cornerSmoothing * 3);
            int joinsGenerated = 0;

            bool useAABB = _affectedKingdoms != null && _calculator.HasLastRebuildBounds;
            float aabbMinX = 0f, aabbMinY = 0f, aabbMaxX = 0f, aabbMaxY = 0f;
            if (useAABB)
            {
                float margin = outerOffset + 1f;
                aabbMinX = _calculator.LastRebuildMinX - margin;
                aabbMinY = _calculator.LastRebuildMinY - margin;
                aabbMaxX = _calculator.LastRebuildMaxX + margin;
                aabbMaxY = _calculator.LastRebuildMaxY + margin;
            }

            foreach (var junction in _junctions)
            {
                if (junction.Arms.Count < 2)
                    continue;

                if (useAABB)
                {
                    Vec2 p = junction.Position;
                    if (p.x < aabbMinX || p.x > aabbMaxX || p.y < aabbMinY || p.y > aabbMaxY)
                        continue;
                }

                if (_affectedKingdoms != null)
                {
                    bool junctionAffected = false;
                    foreach (var arm in junction.Arms)
                    {
                        if ((arm.LeftKingdom != null && _affectedKingdoms.Contains(arm.LeftKingdom)) ||
                            (arm.RightKingdom != null && _affectedKingdoms.Contains(arm.RightKingdom)))
                        {
                            junctionAffected = true;
                            break;
                        }
                    }
                    if (!junctionAffected)
                        continue;
                }

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

                    if (!IsKingdomAffected(kingdom))
                        continue;

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

                int createdCount = Renderer.RenderKingdomStrips(builder, heightOffset, _preservedStripKeys);
                if (createdCount > 0)
                {
                    ModLog.Log($"  {builder.Kingdom.Name}: {createdCount} strips (of {builder.Strips.Count} candidates)");
                }
            }

            if (_flushIndex >= _pendingFlush.Count)
            {
                if (_cachedBuilders == null)
                    _cachedBuilders = new Dictionary<Kingdom, KingdomMeshBuilder>();

                foreach (var builder in _pendingFlush)
                {
                    _cachedBuilders[builder.Kingdom] = builder;
                }

                ModLog.Log($"Total entities: {Renderer.EntityCount}");

                if (_affectedKingdoms != null)
                {
                    ModLog.Log($"=== Partial Rebuild Done ({_affectedKingdoms.Count} kingdoms regenerated, " +
                               $"{_cachedBuilders.Count} total cached) ===");
                }
                else
                {
                    ModLog.Log("=== Kingdom Border Renderer Done (strips) ===");
                }

                _pendingFlush = null;

                // Borders done — transition to the fill phase for the same kingdom set.
                BeginFillBuild(_affectedKingdoms);
            }
        }

        /// <summary>
        /// Prepares the fill build queue for the specified kingdoms (null = all).
        /// Respects the ShowTerritoryFill MCM toggle. Transitions to Idle if no fill
        /// work is needed, or to BuildPhase.BuildingFill otherwise.
        /// </summary>
        private void BeginFillBuild(HashSet<Kingdom> kingdomFilter)
        {
            var settings = MCMSettings.Instance;
            bool showFill = settings?.ShowTerritoryFill ?? true;

            if (FillRenderer == null || !showFill)
            {
                // Fill disabled or not ready — finalize straight to Idle.
                FinalizeRebuild();
                return;
            }

            var cellsByKingdom = _calculator.CollectKingdomCells(kingdomFilter);

            if (cellsByKingdom.Count == 0)
            {
                // Nothing to build — clear any stale fills for affected kingdoms.
                if (kingdomFilter != null)
                    FillRenderer.ClearForKingdoms(kingdomFilter);
                FinalizeRebuild();
                return;
            }

            _pendingFillCells = cellsByKingdom;
            _pendingFillOrder = new List<Kingdom>(cellsByKingdom.Keys);
            _fillIndex = 0;
            _phase = BuildPhase.BuildingFill;

            int totalCells = 0;
            foreach (var kv in cellsByKingdom)
                totalCells += kv.Value.Count;
            ModLog.Log($"Fill build: {_pendingFillOrder.Count} kingdoms, {totalCells} cells");
        }

        /// <summary>
        /// Fill-only rebuild triggered by MCM (ShowTerritoryFill toggled on).
        /// Walks all kingdoms without touching the border pipeline.
        /// </summary>
        private void BeginFillOnlyRebuild()
        {
            if (FillRenderer == null || _calculator == null)
                return;

            FillRenderer.ClearAll();
            BeginFillBuild(null);
        }

        private void ProcessFillBuildIncremental()
        {
            if (FillRenderer == null || _pendingFillOrder == null)
            {
                FinalizeRebuild();
                return;
            }

            float heightOffset = MCMSettings.Instance?.HeightOffset ?? 0.55f;
            // Sit the fill just below the border strips to avoid z-fighting at edges.
            float fillHeightOffset = Math.Max(0.05f, heightOffset - 0.08f);
            bool showOnWater = MCMSettings.Instance?.ShowBordersOnWater ?? true;
            bool hideWater = !showOnWater;

            int processedThisTick = 0;

            while (_fillIndex < _pendingFillOrder.Count && processedThisTick < FillKingdomsPerTick)
            {
                var kingdom = _pendingFillOrder[_fillIndex];
                _fillIndex++;
                processedThisTick++;

                if (!_pendingFillCells.TryGetValue(kingdom, out var cells) || cells.Count == 0)
                    continue;

                uint color = BorderCalculator.GetKingdomColor(kingdom);

                // cornerToWorld is a thin closure over the calculator — avoids copying
                // map bounds into the renderer.
                System.Func<int, int, Vec2> cornerToWorld = _calculator.GetGridCornerWorldPos;

                FillRenderer.BuildKingdomFill(
                    kingdom,
                    color,
                    cells,
                    cornerToWorld,
                    fillHeightOffset,
                    hideWater);
            }

            if (_fillIndex >= _pendingFillOrder.Count)
            {
                ModLog.Log($"Fill build complete: {FillRenderer.EntityCount} kingdom fill entities");
                _pendingFillOrder = null;
                _pendingFillCells = null;
                FinalizeRebuild();
            }
        }

        private void FinalizeRebuild()
        {
            _affectedKingdoms = null;
            _preservedStripKeys = null;
            _phase = BuildPhase.Idle;
        }

        public void Cleanup()
        {
            if (Renderer != null)
            {
                ModLog.Log("Cleaning up border entities...");
                Renderer.ClearAll();
                Renderer = null;
            }
            if (FillRenderer != null)
            {
                FillRenderer.ClearAll();
                FillRenderer = null;
            }
            _cachedBuilders = null;
            _affectedKingdoms = null;
            _preservedStripKeys = null;
            _pendingRegenSettlements = null;
            _pendingRegenKingdoms = null;
            _pendingFillCells = null;
            _pendingFillOrder = null;
            _isInitialized = false;
            _phase = BuildPhase.Idle;
        }
    }
}