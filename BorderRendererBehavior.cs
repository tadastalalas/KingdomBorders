using System;
using System.Collections.Generic;
using System.Linq;
using SandBox;
using SandBox.View.Map;
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
        ClearingEntities,
        ProcessingSegments,
        FlushingMeshes
    }

    public class BorderRendererBehavior : CampaignBehaviorBase
    {
        private bool _isInitialized;
        private BorderCalculator _calculator;

        // ADD: MCM snapshot for fill toggle/opacity (no rebuild needed when these change)
        private bool _lastShowFill;
        private float _lastFillOpacity;

        // Deferred build: set by BeginBuildBorders(), consumed by ApplicationTick()
        // once the map screen is actually active.
        private bool _buildRequested;

        // State machine
        private BuildPhase _phase = BuildPhase.Idle;

        // === Phase 0 diagnostics: per-rebuild timing ===
        // Drives one Stopwatch reused across all phases of a single rebuild.
        // Totals are logged when the rebuild transitions back to Idle.
        private readonly System.Diagnostics.Stopwatch _diagSw = new System.Diagnostics.Stopwatch();
        private long _diagGridMs;
        private long _diagEdgeChainMs;
        private long _diagCullMs;
        private long _diagSegmentMs;
        private long _diagFlushMs;
        private bool _diagInRebuild;

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
        private const int MaxRemovalsPerTick = 40; // tune against the new "cull=" line in the PERF log
        
        // Budgeted by total sampled points (inner+outer vertices) rather than raw strip
        // count — cost is driven by native terrain/water sampling per vertex, so a few long
        // strips can cost far more than many short ones. Starting value; re-tune against the
        // corrected "flush=" number in KingdomBorders.log once you've applied the bug fix above.
        private const int MaxFlushPointsPerTick = 600;

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

        public bool IsVisible { get; private set; } = true;

        public void ToggleVisibility()
        {
            IsVisible = !IsVisible;
        }

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
            // Deferred build: wait until the map screen is active before starting
            // the pipeline. This avoids competing with the intro cinematic for
            // frame time, preventing audio-visual desync and input lag on game start.
            if (_buildRequested)
            {
                if (MapScreen.Instance == null)
                    return; // Map not ready yet — skip all work this frame

                _buildRequested = false;
                StartBuildPipeline();
            }

            switch (_phase)
            {
                case BuildPhase.BuildingGrid:
                    _diagSw.Restart();
                    if (_calculator.BuildTerritoryGridIncremental(GridRowsPerTick))
                    {
                        _phase = BuildPhase.FindingEdges;
                    }
                    _diagSw.Stop();
                    _diagGridMs += _diagSw.ElapsedMilliseconds;
                    break;

                case BuildPhase.FindingEdges:
                    _diagSw.Restart();
                    FindEdgesAndChain();
                    _diagSw.Stop();
                    _diagEdgeChainMs += _diagSw.ElapsedMilliseconds;
                    if (_phase == BuildPhase.Idle)
                        DiagLogIfRebuildComplete();
                    break;
                
                case BuildPhase.ClearingEntities:
                    _diagSw.Restart();
                    if (Renderer.ContinueClearing(MaxRemovalsPerTick))
                    {
                        _phase = BuildPhase.ProcessingSegments;
                    }
                    _diagSw.Stop();
                    _diagCullMs += _diagSw.ElapsedMilliseconds;
                    break;

                case BuildPhase.ProcessingSegments:
                    _diagSw.Restart();
                    ProcessPendingSegments();
                    _diagSw.Stop();
                    _diagSegmentMs += _diagSw.ElapsedMilliseconds;
                    break;

                case BuildPhase.FlushingMeshes:
                    _diagSw.Restart();
                    FlushKingdomMeshesIncremental();
                    _diagSw.Stop();
                    _diagFlushMs += _diagSw.ElapsedMilliseconds;
                    if (_phase == BuildPhase.Idle)
                        DiagLogIfRebuildComplete();
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

            DiagBeginRebuild();

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

        private void RegenerateForSettlements(HashSet<Settlement> changedSettlements, HashSet<Kingdom> explicitlyAffectedKingdoms)
        {
            if (Renderer == null || _calculator == null)
                return;

            DiagBeginRebuild();

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

            _diagSw.Restart();
            bool gridChanged;
            if (settlementCount > 0)
            {
                gridChanged = _calculator.RebuildTerritoryGridAroundSettlements(changedSettlements);
            }
            else
            {
                gridChanged = _calculator.RebuildTerritoryGridForKingdoms(explicitlyAffectedKingdoms);
            }
            _diagSw.Stop();
            _diagGridMs += _diagSw.ElapsedMilliseconds;

            if (!gridChanged)
            {
                ModLog.Log("Grid unchanged after partial rebuild — skipping regen");
                _preservedStripKeys = null;

                DiagLogIfRebuildComplete();

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

            // Safety net for the scoped edge search below: with zero directly-affected kingdoms
            // there's nothing to look for, and an empty result there would otherwise be
            // mistaken for "no borders exist anywhere on the map" a few lines down.
            if (directlyAffected.Count == 0)
            {
                ModLog.Log("No directly affected kingdoms resolved — skipping regen");
                _preservedStripKeys = null;

                DiagLogIfRebuildComplete();

                _phase = BuildPhase.Idle;
                return;
            }

                        _diagSw.Restart();

            // Pass 1: scoped to directly-affected kingdoms, just to discover which OTHER
            // kingdoms are adjacent to them.
            var initialEdges = _calculator.FindBorderEdgesForKingdoms(directlyAffected);
            var initialExclaveEdges = _calculator.FindExclaveBorderEdgesForKingdoms(directlyAffected);
            initialEdges.AddRange(initialExclaveEdges);

            if (initialEdges.Count == 0)
            {
                ModLog.Log("No border edges found after partial grid rebuild");
                Renderer.ClearAll();
                _cachedBuilders.Clear();
                _preservedStripKeys = null;

                _diagSw.Stop();
                _diagEdgeChainMs += _diagSw.ElapsedMilliseconds;
                DiagLogIfRebuildComplete();

                _phase = BuildPhase.Idle;
                return;
            }

            var initialSegments = _calculator.ChainEdges(initialEdges);

            _affectedKingdoms = new HashSet<Kingdom>(directlyAffected);
            foreach (var seg in initialSegments)
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
            foreach (var seg in initialSegments)
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

            // Pass 2: re-search scoped to the now-final _affectedKingdoms set. Required for
            // correct junction reconstruction — a junction where an affected kingdom meets two
            // OTHER kingdoms needs the segment between those two others as well, even though
            // neither side of it changed ownership, or that corner join ends up with a single
            // arm instead of two and gets silently skipped.
            var edges = _calculator.FindBorderEdgesForKingdoms(_affectedKingdoms);
            var exclaveEdges = _calculator.FindExclaveBorderEdgesForKingdoms(_affectedKingdoms);
            edges.AddRange(exclaveEdges);

            var segments = _calculator.ChainEdges(edges);

            _junctions = _calculator.FindJunctions(segments);
            _junctionKeys = _calculator.GetJunctionKeys(_junctions);

            _diagSw.Stop();
            _diagEdgeChainMs += _diagSw.ElapsedMilliseconds;

            ModLog.Log($"Affected kingdoms: {_affectedKingdoms.Count} " +
                        $"(directly: {directlyAffected.Count}, " +
                        $"expanded with neighbors: {_affectedKingdoms.Count - directlyAffected.Count})");
            foreach (var k in _affectedKingdoms)
                ModLog.Log($"  - {k.Name}");

            // --- AABB-based diff clear ---
            // Classification (which entries to remove) happens synchronously here — cheap
            // float-box comparisons. The actual native GameEntity.Remove() calls are deferred
            // to the budgeted ClearingEntities phase.
            _diagSw.Restart();
            if (_calculator.HasLastRebuildBounds)
            {
                float borderGap = MCMSettings.Instance?.BorderGap ?? 0.30f;
                float borderWidth = MCMSettings.Instance?.BorderWidth ?? 1.05f;
                float stripMargin = (borderGap / 2f) + borderWidth + 1f;

                float minX = _calculator.LastRebuildMinX - stripMargin;
                float minY = _calculator.LastRebuildMinY - stripMargin;
                float maxX = _calculator.LastRebuildMaxX + stripMargin;
                float maxY = _calculator.LastRebuildMaxY + stripMargin;

                _preservedStripKeys = Renderer.BeginClearForKingdomsInBounds(
                    _affectedKingdoms, minX, minY, maxX, maxY);
            }
            else
            {
                Renderer.BeginClearForKingdoms(_affectedKingdoms);
                _preservedStripKeys = null;
            }
            _diagSw.Stop();
            _diagCullMs += _diagSw.ElapsedMilliseconds;

            foreach (var k in _affectedKingdoms)
                _cachedBuilders.Remove(k);

            ModLog.Log($"Queuing {segments.Count} border segments " +
                       $"(preserved {_preservedStripKeys?.Count ?? 0} strips by AABB cull)...");

            _pendingSegments = segments;
            _pendingIndex = 0;
            _renderedCount = 0;
            _skippedCount = 0;
            _kingdomBuilders = new Dictionary<Kingdom, KingdomMeshBuilder>();
            _phase = BuildPhase.ClearingEntities;
        }

        private void BeginBuildBorders()
        {
            _isInitialized = true;
            _buildRequested = true;
            ModLog.Clear();
            ModLog.Log("=== Kingdom Border Renderer Starting (deferred until map screen) ===");
        }

        /// <summary>
        /// Actually starts the build pipeline. Called from ApplicationTick() once
        /// MapScreen.Instance is available, ensuring no work competes with the
        /// intro cinematic or initial loading screens.
        /// </summary>
        private void StartBuildPipeline()
        {
            try
            {
                ModLog.Log("Map screen active — starting border build pipeline");

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

                DiagLogIfRebuildComplete();

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
        ///
        /// Always draws BOTH sides of the loop (same as open segments). The earlier
        /// "exclave kingdom only" optimization was incorrect: after exclave detection
        /// rewrites the grid, the surrounding kingdom's main border traces only the
        /// outer extent of its territory and does NOT cover the exclave hole inside
        /// it — so the closed loop is the only place that boundary is drawn. Drawing
        /// just one side produced a single floating strip that visually overlapped
        /// nearby strips. Both sides → consistent double border everywhere.
        ///
        /// Partial-rebuild gating (IsKingdomAffected) is kept per side so we don't
        /// re-render strips for kingdoms outside the affected set.
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

            // Always draw both sides (subject to null and partial-rebuild gating).
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
            int pointsThisTick = 0;
            float heightOffset = MCMSettings.Instance?.HeightOffset ?? 0.55f;

            while (_flushIndex < _pendingFlush.Count)
            {
                var builder = _pendingFlush[_flushIndex];
                int builderPoints = CountBuilderPoints(builder);

                if (pointsThisTick > 0 && pointsThisTick + builderPoints > MaxFlushPointsPerTick)
                    break;

                _flushIndex++;
                pointsThisTick += builderPoints;

                int createdCount = Renderer.RenderKingdomStrips(builder, heightOffset, _preservedStripKeys);
                if (createdCount > 0)
                {
                    ModLog.Log($"  {builder.Kingdom.Name}: {createdCount} strips (of {builder.Strips.Count} candidates, {builderPoints} points)");
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

        private static int CountBuilderPoints(KingdomMeshBuilder builder)
        {
            int total = 0;
            for (int i = 0; i < builder.Strips.Count; i++)
            {
                total += builder.Strips[i].inner.Count + builder.Strips[i].outer.Count;
            }
            return total;
        }

        public void Cleanup()
        {
            Renderer?.ClearAll();
            Renderer = null;
            MapScene = null;
            _calculator = null;
            _pendingSegments = null;
            _kingdomBuilders = null;
            _pendingFlush = null;
            _cachedBuilders = null;
            _affectedKingdoms = null;
            _preservedStripKeys = null;
            _junctions = null;
            _junctionKeys = null;
            _pendingRegenSettlements = null;
            _pendingRegenKingdoms = null;
            _buildRequested = false;
            _isInitialized = false;
            _phase = BuildPhase.Idle;
        }

        /// <summary>
        /// Queues a full border rebuild. Called externally by console commands.
        /// </summary>
        public void ForceFullRebuild()
        {
            _fullRebuildRequested = true;
        }

        private void DiagBeginRebuild()
        {
            _diagGridMs = _diagEdgeChainMs = _diagCullMs = _diagSegmentMs = _diagFlushMs = 0;
            _diagInRebuild = true;
        }

        private void DiagLogIfRebuildComplete()
        {
            if (!_diagInRebuild) return;
            _diagInRebuild = false;
            long total = _diagGridMs + _diagEdgeChainMs + _diagCullMs + _diagSegmentMs + _diagFlushMs;
            ModLog.Log($"[PERF] Rebuild totals: grid={_diagGridMs}ms, edges+chain={_diagEdgeChainMs}ms, " +
                       $"cull={_diagCullMs}ms, segments={_diagSegmentMs}ms, flush={_diagFlushMs}ms, total={total}ms");
        }
    }
}