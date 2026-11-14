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
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace KingdomBorders
{
    internal enum BuildPhase
    {
        Idle,
        BuildingGrid,
        FindingEdges,
        ClearingEntities,
        ProcessingSegments,
        FlushingMeshes,
        BuildingTint
    }

    public class BorderRendererBehavior : CampaignBehaviorBase
    {
        private bool _isInitialized;
        private BorderCalculator _calculator;

        // Deferred build: set by BeginBuildBorders(), consumed by ApplicationTick()
        // once the map screen is actually active.
        private bool _buildRequested;

        // State machine
        private BuildPhase _phase = BuildPhase.Idle;

        // === Phase 0 diagnostics: per-rebuild timing ===
        // Drives one Stopwatch reused across all phases of a single rebuild.
        // Totals are logged when the rebuild transitions back to Idle.
        private readonly System.Diagnostics.Stopwatch _diagSw = new System.Diagnostics.Stopwatch();
        private double _diagGridMs, _diagEdgeChainMs, _diagCullMs, _diagSegmentMs, _diagFlushMs, _diagTintMs;
        private double _diagGridMax, _diagEdgeChainMax, _diagCullMax, _diagSegmentMax, _diagFlushMax, _diagTintMax;
        private double _diagWorstFrameMs;
        private int _diagFrames;
        private bool _diagLogPending;
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
        private int _flushStripIndex; // cursor into _pendingFlush[_flushIndex].Strips
        private int _flushCreated;
        private int _flushPoints;
        private int _flushFailed;
        private const int MaxRemovalsPerTick = 40; // tune against the new "cull=" line in the PERF log
        
        // Sampling measured at ~3 µs/vertex → ~1.2 ms per tick. Mesh creation is the real cost
        // and is limited separately to one strip per tick in FlushKingdomMeshesIncremental.
        private const int MaxSampleVerticesPerTick = 400;
        // Quads emitted per tick across chunk meshes; ~15 µs per quad at 64-quad meshes → ~3 ms.
        private const int MaxQuadsPerTick = 192;

        // Queued regeneration — accumulated while builds may be in progress
        private HashSet<Settlement> _pendingRegenSettlements;

        // Explicitly tracked kingdoms that must be rebuilt (captured directly from events,
        // since after the event fires the settlement/clan references no longer point at the
        // old kingdom and we'd otherwise lose that side of the border).
        private HashSet<Kingdom> _pendingRegenKingdoms;

        private bool _regenRequested;
        private bool _fullRebuildRequested;
        private bool _labelInfoDirty;

        // Track MCM settings to detect changes and trigger rebuild
        private bool _lastShowOnWater;
        private float _lastBorderWidth;
        private float _lastBorderGap;
        private float _lastHeightOffset;
        private int _lastCornerSmoothing;
        private int _lastGridResolution;

        // Junction data for the current build
        private List<JunctionInfo> _junctions;
        private HashSet<long> _junctionKeys;

        // === Built kingdoms ===
        // Kingdoms whose strips are in the scene. null = nothing built yet, so a partial
        // regeneration falls back to a full rebuild. The strip geometry itself is not kept:
        // the scene entities and their content keys (BorderRenderer) are all a partial rebuild needs.
        private HashSet<Kingdom> _cachedKingdoms;

        // Kingdoms whose borders need regeneration in the current partial build.
        // null means full rebuild (all kingdoms processed).
        private HashSet<Kingdom> _affectedKingdoms;

        // Identity keys of strips whose geometry is unchanged (content-hash diff); the flush skips re-creating them.
        private HashSet<long> _preservedStripKeys;

        public BorderRenderer Renderer { get; private set; }

        public Scene MapScene { get; private set; }

        private const int TintRowsPerTick = 4;
        private Kingdom _lastPlayerKingdom;
        private int _lastBorderStyle;
        private bool _lastEmphasizePlayer;
        private bool _lastOnlyPlayer;
        private bool _lastShowTint;
        private bool _lastWarHighlight;

        public TerritoryTintRenderer Tint { get; private set; }

        // 0 = off, 1 = borders, 2 = borders + tint, 3 = tint only
        public int ViewMode { get; private set; } = 1;
        public bool IsVisible => ViewMode == 1 || ViewMode == 2;
        public bool IsTintVisible => ViewMode >= 2;
        
        public KingdomLabelRenderer Labels { get; private set; }
        public bool AreLabelsVisible => ViewMode > 0 && (MCMSettings.Instance?.ShowKingdomLabels ?? true);

        public void CycleViewMode()
        {
            ViewMode = (ViewMode + 1) % ((MCMSettings.Instance?.ShowTerritoryTint ?? true) ? 4 : 2);
            string text = ViewMode switch
            {
                0 => new TextObject("{=KB_ModeOff}Borders: off").ToString(),
                1 => new TextObject("{=KB_ModeOn}Borders: on").ToString(),
                2 => new TextObject("{=KB_ModeTint}Borders: on + territory tint").ToString(),
                _ => new TextObject("{=KB_ModeTintOnly}Territory tint only").ToString()
            };
            if ((MCMSettings.Instance?.EnableDebugLogging ?? false) && MapScene != null)
                text += $" (camera z {MapScene.LastFinalRenderCameraPosition.z:F0})";
            InformationManager.DisplayMessage(new InformationMessage(text));
        }

        public override void RegisterEvents()
        {
            CampaignEvents.OnGameLoadFinishedEvent.AddNonSerializedListener(this, OnGameLoadFinished);
            CampaignEvents.OnNewGameCreatedEvent.AddNonSerializedListener(this, OnNewGameCreated);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, OnSettlementOwnerChanged);
            CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(this, OnClanChangedKingdom);
            CampaignEvents.WarDeclared.AddNonSerializedListener(this, OnWarDeclared);
            CampaignEvents.MakePeace.AddNonSerializedListener(this, OnMakePeace);

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
        
        private void OnWarDeclared(IFaction a, IFaction b, DeclareWarAction.DeclareWarDetail detail) => Renderer?.RefreshWarFlags();

        private void OnMakePeace(IFaction a, IFaction b, MakePeaceAction.MakePeaceDetail detail) => Renderer?.RefreshWarFlags();

        public void ApplicationTick()
        {
            if (_buildRequested)
            {
                if (MapScreen.Instance == null)
                    return;
                _buildRequested = false;
                StartBuildPipeline();
            }

            long frameStart = System.Diagnostics.Stopwatch.GetTimestamp();

            switch (_phase)
            {
                case BuildPhase.BuildingGrid:
                    _diagSw.Restart();
                    if (_calculator.BuildTerritoryGridIncremental(GridRowsPerTick))
                        _phase = BuildPhase.FindingEdges;
                    DiagEnd(ref _diagGridMs, ref _diagGridMax);
                    break;

                case BuildPhase.FindingEdges:
                    _diagSw.Restart();
                    FindEdgesAndChain();
                    DiagEnd(ref _diagEdgeChainMs, ref _diagEdgeChainMax);
                    if (_phase == BuildPhase.Idle)
                        DiagLogIfRebuildComplete();
                    break;

                case BuildPhase.ClearingEntities:
                    _diagSw.Restart();
                    if (Renderer.ContinueClearing(MaxRemovalsPerTick))
                        _phase = BuildPhase.FlushingMeshes;
                    DiagEnd(ref _diagCullMs, ref _diagCullMax);
                    break;

                case BuildPhase.ProcessingSegments:
                    _diagSw.Restart();
                    ProcessPendingSegments();
                    DiagEnd(ref _diagSegmentMs, ref _diagSegmentMax);
                    break;

                case BuildPhase.FlushingMeshes:
                    _diagSw.Restart();
                    FlushKingdomMeshesIncremental();
                    DiagEnd(ref _diagFlushMs, ref _diagFlushMax);
                    if (_phase == BuildPhase.Idle)
                        DiagLogIfRebuildComplete();
                    break;

                case BuildPhase.BuildingTint:
                    _diagSw.Restart();
                    bool tintDone = Tint.Continue(TintRowsPerTick);
                    DiagEnd(ref _diagTintMs, ref _diagTintMax);
                    if (tintDone)
                    {
                        _phase = BuildPhase.Idle;
                        DiagLogIfRebuildComplete();
                    }
                    break;

                case BuildPhase.Idle:
                    ProcessQueuedRegeneration();
                    break;
            }

            // Whole-tick time of this behavior while a rebuild is active — this is the number the player "feels".
            if (_diagInRebuild || _diagLogPending)
            {
                double frameMs = (System.Diagnostics.Stopwatch.GetTimestamp() - frameStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _diagFrames++;
                if (frameMs > _diagWorstFrameMs) _diagWorstFrameMs = frameMs;
            }

            if (_diagLogPending)
                DiagEmit();
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
                CheckPlayerKingdomChanged();
                if (_labelInfoDirty)
                {
                    _labelInfoDirty = false;
                    Labels?.RefreshInfo();
                }
            }
        }

        private void CheckMCMSettingsChanged()
        {
            var settings = MCMSettings.Instance;
            if (settings == null)
                return;

            if (settings.HighlightWarBorders != _lastWarHighlight)
            {
                _lastWarHighlight = settings.HighlightWarBorders;
                Renderer?.RefreshWarFlags();
            }

            if (settings.ShowTerritoryTint != _lastShowTint)
            {
                _lastShowTint = settings.ShowTerritoryTint;
                if (_lastShowTint) _phase = BeginTintOrIdle();
                else { Tint?.ClearAll(); if (ViewMode >= 2) ViewMode = 1; }
            }

            if (settings.ShowBordersOnWater != _lastShowOnWater ||
                Math.Abs(settings.BorderWidth - _lastBorderWidth) > 0.001f ||
                Math.Abs(settings.BorderGap - _lastBorderGap) > 0.001f ||
                Math.Abs(settings.HeightOffset - _lastHeightOffset) > 0.001f ||
                settings.CornerSmoothing != _lastCornerSmoothing ||
                settings.GridResolution != _lastGridResolution ||
                (settings.BorderStyle?.SelectedIndex ?? 0) != _lastBorderStyle ||
                settings.EmphasizePlayerKingdom != _lastEmphasizePlayer ||
                settings.OnlyPlayerBorders != _lastOnlyPlayer)
            {
                ModLog.Log("MCM settings changed — triggering full rebuild");
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
            _lastGridResolution = settings.GridResolution;
            _lastBorderStyle = settings.BorderStyle?.SelectedIndex ?? 0;
            _lastEmphasizePlayer = settings.EmphasizePlayerKingdom;
            _lastOnlyPlayer = settings.OnlyPlayerBorders;
            _lastShowTint = settings.ShowTerritoryTint;
            _lastWarHighlight = settings.HighlightWarBorders;
            _lastPlayerKingdom = Clan.PlayerClan?.Kingdom;
        }
        
        // Joining/leaving a kingdom with no fiefs doesn't move the grid, so the partial path
        // would skip; baked player emphasis / player-only filter need a real rebuild.
        private void CheckPlayerKingdomChanged()
        {
            Kingdom current = Clan.PlayerClan?.Kingdom;
            if (current == _lastPlayerKingdom) return;
            _lastPlayerKingdom = current;
            MCMSettings s = MCMSettings.Instance;
            if ((s?.OnlyPlayerBorders ?? false) || (s?.EmphasizePlayerKingdom ?? true))
                FullRebuild();
        }

        private BuildPhase BeginTintOrIdle()
        {
            if (!(MCMSettings.Instance?.ShowTerritoryTint ?? true) || Tint == null || _calculator == null)
                return BuildPhase.Idle;
            Tint.Begin(_calculator);
            return BuildPhase.BuildingTint;
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
            Renderer.GlowKingdoms = null;
            Tint?.ClearAll();
            _cachedKingdoms = null;
            _affectedKingdoms = null; // null = full rebuild, process all kingdoms
            _preservedStripKeys = null;
            _calculator = new BorderCalculator(resolution: MCMSettings.Instance?.GridResolution ?? 150);
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
            _labelInfoDirty = true; // fief counts changed even when the grid (and so the labels) does not
        }

        private void OnClanChangedKingdom(Clan clan, Kingdom oldKingdom, Kingdom newKingdom,
    ChangeKingdomAction.ChangeKingdomActionDetail detail, bool showNotification)
        {
            if (!_isInitialized)
                return;
            
            _labelInfoDirty = true;

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

            if (_cachedKingdoms == null)
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
            DiagEnd(ref _diagGridMs, ref _diagGridMax);

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

            HashSet<Kingdom> glow = null;
            if (changedSettlements != null)
            {
                foreach (var s in changedSettlements)
                {
                    Kingdom kingdom = s.IsVillage ? s.Village?.Bound?.OwnerClan?.Kingdom : s.OwnerClan?.Kingdom;
                    if (kingdom == null) continue;
                    directlyAffected.Add(kingdom);
                    glow ??= [];
                    glow.Add(kingdom); // current owner = gaining side
                }
            }
            Renderer.GlowKingdoms = glow;

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
                // The directly affected kingdoms have no cells left (e.g. a kingdom's last fief went to a
                // kingdomless clan). That says nothing about the rest of the map, so rebuild all of it
                // instead of clearing it; a map that really has no border ends in FindEdgesAndChain.
                ModLog.Log("No border edges for the affected kingdoms — falling back to full rebuild");
                FullRebuild();
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

            DiagEnd(ref _diagEdgeChainMs, ref _diagEdgeChainMax);

            ModLog.Log($"Affected kingdoms: {_affectedKingdoms.Count} " +
                       $"(directly: {directlyAffected.Count}, expanded with neighbors: {_affectedKingdoms.Count - directlyAffected.Count})");
            foreach (Kingdom k in _affectedKingdoms)
                ModLog.Log($"  - {k.Name}");

            foreach (Kingdom k in _affectedKingdoms)
                _cachedKingdoms.Remove(k);

            ModLog.Log($"Queuing {segments.Count} border segments...");

            _pendingSegments = segments;
            _pendingIndex = 0;
            _renderedCount = 0;
            _skippedCount = 0;
            _kingdomBuilders = new Dictionary<Kingdom, KingdomMeshBuilder>();
            // Old strips are not removed yet — the content-hash diff at the end of ProcessPendingSegments
            // decides which are stale once the new geometry exists.
            _phase = BuildPhase.ProcessingSegments;
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
                Tint = new TerritoryTintRenderer(MapScene);
                Labels = new KingdomLabelRenderer();

                var kingdoms = Kingdom.All;
                ModLog.Log($"Active kingdoms: {kingdoms.Count}");
                foreach (var k in kingdoms)
                {
                    uint col = BorderCalculator.GetKingdomColor(k);
                    ModLog.Log($"  {k.Name}: color=0x{col:X8}, fiefs={k.Fiefs.Count}");
                }

                SnapshotMCMSettings();

                _cachedKingdoms = null;
                _affectedKingdoms = null;
                _calculator = new BorderCalculator(resolution: MCMSettings.Instance?.GridResolution ?? 150);
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

                // No border to draw, but the grid is new: labels and tint still have to follow it.
                Labels?.Rebuild(_calculator);
                _phase = BeginTintOrIdle();
                if (_phase == BuildPhase.Idle)
                    DiagLogIfRebuildComplete();
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
            if (MCMSettings.Instance?.OnlyPlayerBorders ?? false)
            {
                Kingdom pk = Clan.PlayerClan?.Kingdom;
                if (pk != null && segment.KingdomA != pk && segment.KingdomB != pk && segment.ExclaveKingdom != pk)
                    return false;
            }
            
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

                // Both sides null can't occur (an edge needs differing cells). One null side means a kingdom
                // borders kingdomless territory (independent / rebel clan): draw that kingdom's strip only.
                if (segment.KingdomA == null && segment.KingdomB == null)
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
                if (leftKingdom != null && IsKingdomAffected(leftKingdom))
                {
                    GetBuilder(leftKingdom).Strips.Add((leftLineInner, leftLineOuter, rightKingdom));
                    _renderedCount++;
                }

                if (rightKingdom != null && IsKingdomAffected(rightKingdom))
                {
                    GetBuilder(rightKingdom).Strips.Add((rightLineInner, rightLineOuter, leftKingdom));
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
                GenerateJunctionJoins(innerOffset, outerOffset, cornerSmoothing);

                _pendingSegments = null;
                _junctions = null;
                _junctionKeys = null;

                _pendingFlush = new List<KingdomMeshBuilder>(_kingdomBuilders.Values);
                _flushIndex = 0;
                _flushStripIndex = 0;
                _flushCreated = 0;
                _flushPoints = 0;
                _flushFailed = 0;
                _kingdomBuilders = null;

                if (_affectedKingdoms != null)
                {
                    // Partial rebuild: keep every old strip whose geometry is unchanged, remove the rest.
                    _preservedStripKeys = Renderer.BeginClearDiff(_affectedKingdoms, _pendingFlush);
                    _phase = BuildPhase.ClearingEntities;
                }
                else
                {
                    _preservedStripKeys = null;
                    _phase = BuildPhase.FlushingMeshes;
                }

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
                GetBuilder(leftKingdom).Strips.Add((leftLineInner, leftLineOuter, rightKingdom));
                _renderedCount++;
            }
            if (rightKingdom != null && IsKingdomAffected(rightKingdom))
            {
                GetBuilder(rightKingdom).Strips.Add((rightLineInner, rightLineOuter, leftKingdom));
                _renderedCount++;
            }
        }

        /// <summary>
        /// For each junction, finds pairs of arms that share a kingdom and generates
        /// a smooth bevel/miter join connecting their strip endpoints.
        /// During partial rebuilds, only junctions touching an affected kingdom are processed.
        /// </summary>
        private void GenerateJunctionJoins(float innerOffset, float outerOffset, int cornerSmoothing)
        {
            if (_junctions == null || _junctions.Count == 0)
                return;

            int curveSegments = Math.Max(3, cornerSmoothing * 3);
            int joinsGenerated = 0;
            
            Kingdom playerKingdom = Clan.PlayerClan?.Kingdom;
            bool onlyPlayer = (MCMSettings.Instance?.OnlyPlayerBorders ?? false) && playerKingdom != null;

            foreach (var junction in _junctions)
            {
                if (junction.Arms.Count < 2)
                    continue;

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
                    
                    if (onlyPlayer && kingdom != playerKingdom)
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
                            GetBuilder(kingdom).Strips.Add((join.inner, join.outer, null));
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
                                GetBuilder(kingdom).Strips.Add((join.inner, join.outer, null));
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
            int vertexBudget = MaxSampleVerticesPerTick;
            int quadBudget = MaxQuadsPerTick;
            float heightOffset = MCMSettings.Instance?.HeightOffset ?? 0.55f;

            while (_flushIndex < _pendingFlush.Count)
            {
                KingdomMeshBuilder builder = _pendingFlush[_flushIndex];
                List<(List<Vec2> inner, List<Vec2> outer, Kingdom other)> strips = builder.Strips;

                while (_flushStripIndex < strips.Count)
                {
                    StripFlushResult r = Renderer.ContinueStrip(builder, _flushStripIndex, heightOffset, _preservedStripKeys,
                        ref vertexBudget, ref quadBudget);
                    if (r == StripFlushResult.InProgress)
                        return; // a budget ran out mid-strip — resume the same strip next tick

                    if (r == StripFlushResult.Created)
                    {
                        _flushCreated++;
                        _flushPoints += strips[_flushStripIndex].inner.Count + strips[_flushStripIndex].outer.Count;
                    }
                    else if (r == StripFlushResult.Failed)
                        _flushFailed++;
                    _flushStripIndex++;

                    if (vertexBudget <= 0 || quadBudget <= 0)
                        return;
                }

                if (_flushCreated > 0 || _flushFailed > 0)
                    ModLog.Log($"  {builder.Kingdom.Name}: {_flushCreated} created, {_flushFailed} empty (of {strips.Count} candidates, {_flushPoints} points)");

                _flushIndex++;
                _flushStripIndex = 0;
                _flushCreated = 0;
                _flushPoints = 0;
                _flushFailed = 0;
            }

            if (_flushIndex >= _pendingFlush.Count)
            {
                if (_cachedKingdoms == null)
                    _cachedKingdoms = new HashSet<Kingdom>();

                foreach (var builder in _pendingFlush)
                {
                    _cachedKingdoms.Add(builder.Kingdom);
                }

                ModLog.Log($"Total entities: {Renderer.EntityCount}");

                if (_affectedKingdoms != null)
                {
                    ModLog.Log($"=== Partial Rebuild Done ({_affectedKingdoms.Count} kingdoms regenerated, " +
                               $"{_cachedKingdoms.Count} total cached) ===");
                }
                else
                {
                    ModLog.Log("=== Kingdom Border Renderer Done ===");
                }

                _pendingFlush = null;
                _affectedKingdoms = null;
                _preservedStripKeys = null;

                Renderer.GlowKingdoms = null;
                Labels?.Rebuild(_calculator);
                _phase = BeginTintOrIdle();
            }
        }

        public void Cleanup()
        {
            Renderer?.ClearAll();
            Tint?.ClearAll();
            Labels?.Dispose();
            Labels = null;
            Tint = null;
            Renderer = null;
            MapScene = null;
            _calculator = null;
            _pendingSegments = null;
            _kingdomBuilders = null;
            _pendingFlush = null;
            _cachedKingdoms = null;
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
            _diagGridMs = _diagEdgeChainMs = _diagCullMs = _diagSegmentMs = _diagFlushMs = _diagTintMs = 0;
            _diagGridMax = _diagEdgeChainMax = _diagCullMax = _diagSegmentMax = _diagFlushMax = _diagTintMax = 0;
            _diagWorstFrameMs = 0;
            _diagFrames = 0;
            if (Renderer != null) Renderer.DiagHeightMs = Renderer.DiagWaterMs = Renderer.DiagMeshMs = 0;
            _diagInRebuild = true;
            _diagLogPending = false;
            if (MCMSettings.Instance?.EnableDebugLogging ?? false)
                InformationManager.DisplayMessage(new InformationMessage("[KB] Borders updating…"));
        }
        
        private void DiagEnd(ref double total, ref double max)
        {
            _diagSw.Stop();
            double ms = _diagSw.Elapsed.TotalMilliseconds;
            total += ms;
            if (ms > max) max = ms;
        }

        // Deferred to the end of ApplicationTick so the final frame's time is included in the worst-frame figure.
        private void DiagLogIfRebuildComplete()
        {
            if (!_diagInRebuild) return;
            _diagInRebuild = false;
            _diagLogPending = true;
        }
        
        private void DiagEmit()
        {
            _diagLogPending = false;
            double total = _diagGridMs + _diagEdgeChainMs + _diagCullMs + _diagSegmentMs + _diagFlushMs + _diagTintMs;
            ModLog.Log($"[PERF] totals: grid={_diagGridMs:F1} edges+chain={_diagEdgeChainMs:F1} cull={_diagCullMs:F1} " +
                       $"segments={_diagSegmentMs:F1} flush={_diagFlushMs:F1} tint={_diagTintMs:F1} total={total:F1}ms over {_diagFrames} frames");
            ModLog.Log($"[PERF] per-frame max: grid={_diagGridMax:F1} edges+chain={_diagEdgeChainMax:F1} cull={_diagCullMax:F1} " +
                       $"segments={_diagSegmentMax:F1} flush={_diagFlushMax:F1} tint={_diagTintMax:F1} WORST FRAME={_diagWorstFrameMs:F1}ms");
            if (Renderer != null)
                ModLog.Log($"[PERF] flush split: height={Renderer.DiagHeightMs:F1} water={Renderer.DiagWaterMs:F1} mesh={Renderer.DiagMeshMs:F1}ms");
            if (MCMSettings.Instance?.EnableDebugLogging ?? false)
                InformationManager.DisplayMessage(new InformationMessage($"[KB] Borders updated: {total:F0} ms / {_diagFrames} frames / worst {_diagWorstFrameMs:F1} ms"));
        }
    }
}