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

        // Explicitly tracked kingdoms that must be rebuilt (captured directly from events,
        // since after the event fires the settlement/clan references no longer point at the
        // old kingdom and we'd otherwise lose that side of the border).
        private HashSet<Kingdom> _pendingRegenKingdoms;

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

        // === Cached border structure ===
        // Persists completed strip geometry per kingdom so partial regeneration
        // only rebuilds affected kingdoms, leaving the rest untouched.
        private Dictionary<Kingdom, KingdomMeshBuilder> _cachedBuilders;

        // Kingdoms whose borders need regeneration in the current partial build.
        // null means full rebuild (all kingdoms processed).
        private HashSet<Kingdom> _affectedKingdoms;

        // Identity keys of strip entities that survived the AABB cull and are still
        // in the scene. The flush phase passes this to the renderer so new strips
        // matching one of these keys are not re-created (the existing entity stays).
        private HashSet<long> _preservedStripKeys;

        public BorderRenderer Renderer { get; private set; }
        public Scene MapScene { get; private set; }

        public override void RegisterEvents()
        {
            CampaignEvents.OnGameLoadFinishedEvent.AddNonSerializedListener(this, OnGameLoadFinished);
            CampaignEvents.OnNewGameCreatedEvent.AddNonSerializedListener(this, OnNewGameCreated);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, OnSettlementOwnerChanged);
            CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(this, OnClanChangedKingdom);

            // KingdomCreatedEvent / KingdomDestroyedEvent are intentionally NOT subscribed:
            //  - KingdomDestroyed fires AFTER every fief has already been transferred via
            //    ChangeOwnerOfSettlementAction, so OnSettlementOwnerChanged has already
            //    handled all border changes by the time this would fire.
            //  - KingdomCreated is handled via OnClanChangedKingdom(detail=CreateKingdom),
            //    which gives us the founder clan + both old/new kingdoms for a surgical
            //    partial rebuild instead of a full-map rebuild.
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
            _cachedBuilders = null;
            _affectedKingdoms = null; // null = full rebuild, process all kingdoms
            _preservedStripKeys = null;
            _calculator = new BorderCalculator(resolution: 150);
            _calculator.CalculateMapBounds();
            _calculator.BeginBuildTerritoryGrid();
            _phase = BuildPhase.BuildingGrid;
        }

        /// <summary>
        /// Adds a kingdom to the pending regen set if non-null. Called from event handlers
        /// to explicitly capture both the old and new kingdom sides of a border change,
        /// since after the event fires the settlement/clan references no longer point
        /// at the old kingdom.
        /// </summary>
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

            // Verified against BLSource ChangeKingdomAction.ApplyInternal:
            // ByLeaveFaction only fires during LeaveKingdom, transferring the fief from the
            // leaving clan to the ruler clan of the SAME kingdom. Territory doesn't change,
            // so any regen is pure waste.
            if (detail == ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.ByLeaveFaction)
                return;

            var oldKingdom = oldOwner?.Clan?.Kingdom;
            var newKingdom = newOwner?.Clan?.Kingdom;

            // Skip if neither side belongs to a kingdom — no visible borders affected
            if (oldKingdom == null && newKingdom == null)
                return;

            // Skip same-kingdom transfers (ByKingDecision after siege, gifts within a realm,
            // same-kingdom barters, etc.). The fief stays inside the same kingdom's territory
            // so no border line moves.
            if (oldKingdom != null && newKingdom != null && oldKingdom == newKingdom)
                return;

            // Skip villages — their bound town/castle changing hands already covers them,
            // and we add bound villages explicitly below when tracking a town/castle.
            // Verified against BLSource: ChangeOwnerOfSettlementAction.ApplyInternal fires
            // a single OnSettlementOwnerChanged for the target settlement only — bound
            // villages get SetVisualAsDirty() but no event, so we must re-add them here.
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

            // Capture BOTH sides explicitly. The old kingdom may have lost its only
            // adjacency with the new kingdom, so segment-neighbor expansion in
            // RegenerateForSettlements might not rediscover it.
            QueueAffectedKingdom(oldKingdom);
            QueueAffectedKingdom(newKingdom);

            _regenRequested = true;
        }

        private void OnClanChangedKingdom(Clan clan, Kingdom oldKingdom, Kingdom newKingdom,
    ChangeKingdomAction.ChangeKingdomActionDetail detail, bool showNotification)
        {
            if (!_isInitialized)
                return;

            // --- Filter out events that don't affect borders ---
            // Verified against BLSource ChangeKingdomAction.ApplyInternal:

            // Mercenary clans never own fiefs, so joining/leaving as mercenary changes no borders.
            if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.JoinAsMercenary ||
                detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveAsMercenary)
            {
                return;
            }

            // LeaveKingdom: game loops clan.Settlements and calls
            // ChangeOwnerOfSettlementAction.ApplyByLeaveFaction(kingdom.Leader, fief) for each.
            // Ownership moves within the same kingdom (to the ruler's clan), so borders don't
            // change. We also explicitly filter ByLeaveFaction in OnSettlementOwnerChanged.
            if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveKingdom)
                return;

            // LeaveByClanDestruction: fiefs are transferred earlier by DestroyClanAction via
            // ChangeOwnerOfSettlementAction.ApplyByDestroyClan, so OnSettlementOwnerChanged
            // already covered each fief before this event fires.
            if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveByClanDestruction)
                return;

            // LeaveByKingdomDestruction: fiefs are transferred earlier via DestroyClanAction
            // on every clan of the dying kingdom (see DestroyKingdomAction.ApplyInternal).
            // OnSettlementOwnerChanged already handled every affected fief.
            if (detail == ChangeKingdomAction.ChangeKingdomActionDetail.LeaveByKingdomDestruction)
                return;

            // --- Remaining cases that DO affect borders and have NO ChangeOwnerOfSettlementAction ---
            // JoinKingdom:           clan with fiefs joins another kingdom (fiefs move with them).
            // JoinKingdomByDefection: same as JoinKingdom.
            // LeaveWithRebellion:    clan keeps fiefs but becomes independent (Kingdom=null).
            // CreateKingdom:         founding clan's fiefs become territory of the new kingdom.

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

            // Capture BOTH kingdoms from the event parameters. At this point clan.Settlements
            // all report the NEW kingdom (for joins / CreateKingdom) or null (for rebellion),
            // so oldKingdom has to come from the event.
            QueueAffectedKingdom(oldKingdom);
            QueueAffectedKingdom(newKingdom);

            _regenRequested = true;
            ModLog.Log($"Clan {clan.Name} changed kingdom ({detail}) with {fiefCount} fiefs — queued regen");
        }

        /// <summary>
        /// Identifies which kingdoms are affected by the changed settlements and rebuilds
        /// only the strips whose region actually overlaps the changed AABB. Strips of
        /// affected kingdoms that lie outside the AABB are left untouched in the scene,
        /// and their identity keys are passed into the flush phase so the equivalent new
        /// strips are not re-rendered.
        /// </summary>
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

            // If no grid cell actually flipped kingdom, no border line moved. Nothing to do.
            // This catches no-op events (e.g. ownership reshuffles that resolve identically
            // in Voronoi terms) without any scene mutation or segment processing.
            if (!gridChanged)
            {
                ModLog.Log("Grid unchanged after partial rebuild — skipping regen");
                _preservedStripKeys = null;
                _phase = BuildPhase.Idle;
                return;
            }

            // --- Identify directly affected kingdoms ---
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

            // --- AABB-based diff clear ---
            // If we have partial rebuild bounds from the calculator, only nuke the strips
            // of affected kingdoms whose AABB intersects the changed region. Everything
            // outside stays in the scene untouched (terrain already sampled, mesh already
            // built) and the flush phase skips re-rendering them via _preservedStripKeys.
            if (_calculator.HasLastRebuildBounds)
            {
                float borderGap = MCMSettings.Instance?.BorderGap ?? 0.30f;
                float borderWidth = MCMSettings.Instance?.BorderWidth ?? 1.05f;

                // Expand the grid-rebuild AABB by the maximum strip offset so strip AABBs
                // near the edge are classified correctly.
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
                // No partial AABB (e.g. kingdoms-only queue fell back to full grid rebuild).
                // Fall back to whole-kingdom clear — identical to pre-Step-2 behavior.
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

        /// <summary>
        /// Returns true if the given segment should be processed in the current build.
        /// During a partial rebuild, only segments touching at least one affected kingdom
        /// are processed. During a full rebuild (_affectedKingdoms is null), all are processed.
        /// </summary>
        private bool ShouldProcessSegment(BorderSegment segment)
        {
            if (_affectedKingdoms == null)
                return true; // Full rebuild — process everything

            // Closed-loop (exclave) segments: the surrounding kingdom can legitimately be null
            // when the exclave borders kingdomless territory. Trust ExclaveKingdom in that case
            // so we don't silently drop the exclave's own border during partial rebuilds.
            if (segment.IsClosedLoop && segment.ExclaveKingdom != null)
            {
                return _affectedKingdoms.Contains(segment.ExclaveKingdom);
            }

            // Process if either side of the segment belongs to an affected kingdom
            bool aAffected = segment.KingdomA != null && _affectedKingdoms.Contains(segment.KingdomA);
            bool bAffected = segment.KingdomB != null && _affectedKingdoms.Contains(segment.KingdomB);

            return aAffected || bAffected;
        }

        /// <summary>
        /// Returns true if the given kingdom should have its strip geometry rebuilt.
        /// During a full rebuild, all kingdoms are rebuilt. During a partial rebuild,
        /// only affected kingdoms get new strips — unaffected kingdoms retain their
        /// existing cached mesh entities in the scene.
        /// </summary>
        private bool IsKingdomAffected(Kingdom kingdom)
        {
            if (_affectedKingdoms == null)
                return true; // Full rebuild — all kingdoms are affected

            return _affectedKingdoms.Contains(kingdom);
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

                // Closed-loop exclaves may legitimately have KingdomB == null (surrounded by
                // kingdomless territory). Don't hard-reject them on that alone.
                bool isExclave = segment.IsClosedLoop && segment.ExclaveKingdom != null;
                if (!isExclave && (segment.KingdomA == null || segment.KingdomB == null))
                {
                    _skippedCount++;
                    continue;
                }

                // Skip segments that don't involve any affected kingdom (partial rebuild only)
                if (!ShouldProcessSegment(segment))
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

                // === Open segment processing ===
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

                // Only add strips for kingdoms that are being rebuilt.
                // Unaffected kingdoms already have their mesh entities in the scene.
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
                // Only draw the exclave kingdom's border strip, and only if it's affected
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
                // Fallback: draw both sides (shouldn't normally happen for exclaves)
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

        /// <summary>
        /// For each junction, finds pairs of arms that share a kingdom and generates
        /// a smooth bevel/miter join connecting their strip endpoints.
        /// During partial rebuilds, only junctions involving affected kingdoms AND
        /// positioned inside the rebuild AABB are processed.
        /// </summary>
        private void GenerateJunctionJoins(float innerOffset, float outerOffset, int cornerSmoothing)
        {
            if (_junctions == null || _junctions.Count == 0)
                return;

            int curveSegments = Math.Max(3, cornerSmoothing * 3);
            int joinsGenerated = 0;

            // Precompute AABB bounds for outside-junction culling during partial rebuilds.
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

                // AABB cull: junctions outside the changed region have preserved join entities
                // in the scene; regenerating them is pure CPU waste even with skipKeys.
                if (useAABB)
                {
                    Vec2 p = junction.Position;
                    if (p.x < aabbMinX || p.x > aabbMaxX || p.y < aabbMinY || p.y > aabbMaxY)
                        continue;
                }

                // During partial rebuild, skip junctions that don't touch any affected kingdom
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
                    ModLog.Log("=== Kingdom Border Renderer Done ===");
                }

                _pendingFlush = null;
                _affectedKingdoms = null;
                _preservedStripKeys = null;
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
            _cachedBuilders = null;
            _affectedKingdoms = null;
            _preservedStripKeys = null;
            _pendingRegenSettlements = null;
            _pendingRegenKingdoms = null;
            _isInitialized = false;
            _phase = BuildPhase.Idle;
        }
    }
}