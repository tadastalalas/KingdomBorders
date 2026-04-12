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
        private bool _regenRequested;

        public BorderRenderer Renderer { get; private set; }
        public Scene MapScene { get; private set; }

        public override void RegisterEvents()
        {
            CampaignEvents.OnGameLoadFinishedEvent.AddNonSerializedListener(this, OnGameLoadFinished);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, OnSettlementOwnerChanged);
            CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(this, OnClanChangedKingdom);
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
        }

        public override void SyncData(IDataStore dataStore)
        {
        }

        private void OnGameLoadFinished()
        {
            BeginBuildBorders();
        }

        private void OnTick(float dt)
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
                        _pendingRegenKingdoms = null;

                        if (kingdoms != null && kingdoms.Count > 0)
                        {
                            RegenerateForKingdoms(kingdoms);
                        }
                    }
                    break;
            }
        }

        private void OnSettlementOwnerChanged(Settlement settlement, bool openToClaim,
            Hero newOwner, Hero oldOwner, Hero capturerHero,
            ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            if (!_isInitialized || _phase != BuildPhase.Idle)
                return;

            if (!settlement.IsTown && !settlement.IsCastle)
                return;

            Kingdom oldKingdom = oldOwner?.Clan?.Kingdom;
            Kingdom newKingdom = newOwner?.Clan?.Kingdom;

            if (oldKingdom == newKingdom)
                return;

            ModLog.Log($"Settlement owner changed: {settlement.Name} from {oldKingdom?.Name} to {newKingdom?.Name}");

            if (_pendingRegenKingdoms == null)
                _pendingRegenKingdoms = new HashSet<Kingdom>();

            if (oldKingdom != null) _pendingRegenKingdoms.Add(oldKingdom);
            if (newKingdom != null) _pendingRegenKingdoms.Add(newKingdom);
            _regenRequested = true;
        }

        private void OnClanChangedKingdom(Clan clan, Kingdom oldKingdom, Kingdom newKingdom,
            ChangeKingdomAction.ChangeKingdomActionDetail detail, bool showNotification)
        {
            if (!_isInitialized || _phase != BuildPhase.Idle)
                return;

            if (oldKingdom == newKingdom)
                return;

            bool hasFiefs = clan.Settlements.Any(s => s.IsTown || s.IsCastle);
            if (!hasFiefs)
                return;

            ModLog.Log($"Clan changed kingdom: {clan.Name} from {oldKingdom?.Name} to {newKingdom?.Name}");

            if (_pendingRegenKingdoms == null)
                _pendingRegenKingdoms = new HashSet<Kingdom>();

            if (oldKingdom != null) _pendingRegenKingdoms.Add(oldKingdom);
            if (newKingdom != null) _pendingRegenKingdoms.Add(newKingdom);
            _regenRequested = true;
        }

        private void RegenerateForKingdoms(HashSet<Kingdom> kingdoms)
        {
            if (Renderer == null || _calculator == null)
                return;

            ModLog.Log($"Regenerating borders for {kingdoms.Count} kingdoms...");

            Renderer.ClearForKingdoms(kingdoms);
            _calculator.RebuildTerritoryGridForKingdoms(kingdoms);

            var edges = _calculator.FindBorderEdgesForKingdoms(kingdoms);
            if (edges.Count == 0)
            {
                ModLog.Log("No border edges found for affected kingdoms");
                return;
            }

            var segments = _calculator.ChainEdges(edges);

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

                var smoothed = _calculator.SmoothChaikin(segment.Points, iterations: 2);

                var (leftKingdom, rightKingdom) = _calculator.DetermineKingdomSides(
                    smoothed, segment.KingdomA, segment.KingdomB);

                float innerOffset = 0.15f;
                float outerOffset = 1.2f;
                var leftLineInner = BorderCalculator.OffsetPolyline(smoothed, innerOffset);
                var leftLineOuter = BorderCalculator.OffsetPolyline(smoothed, outerOffset);
                var rightLineInner = BorderCalculator.OffsetPolyline(smoothed, -innerOffset);
                var rightLineOuter = BorderCalculator.OffsetPolyline(smoothed, -outerOffset);

                GetBuilder(leftKingdom).Strips.Add((leftLineInner, leftLineOuter));
                GetBuilder(rightKingdom).Strips.Add((rightLineInner, rightLineOuter));

                _renderedCount += 2;
            }

            if (_pendingIndex >= _pendingSegments.Count)
            {
                _pendingSegments = null;

                _pendingFlush = new List<KingdomMeshBuilder>(_kingdomBuilders.Values);
                _flushIndex = 0;
                _kingdomBuilders = null;
                _phase = BuildPhase.FlushingMeshes;

                ModLog.Log($"Processed {_renderedCount} strips, skipped {_skippedCount} segments. Flushing {_pendingFlush.Count} kingdom meshes...");
            }
        }

        /// <summary>
        /// Flushes kingdom meshes incrementally, limited by strip count per tick
        /// rather than kingdom count — prevents large kingdoms from causing spikes.
        /// </summary>
        private void FlushKingdomMeshesIncremental()
        {
            int stripsThisTick = 0;

            while (_flushIndex < _pendingFlush.Count)
            {
                var builder = _pendingFlush[_flushIndex];

                // Check if this kingdom would exceed the budget
                if (stripsThisTick > 0 && stripsThisTick + builder.Strips.Count > MaxFlushStripsPerTick)
                    break; // Wait for next tick

                _flushIndex++;
                stripsThisTick += builder.Strips.Count;

                var entity = Renderer.RenderKingdomStrips(builder, heightOffset: 0.5f);
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
            _pendingSegments = null;
            _pendingIndex = 0;
            _calculator = null;
            _kingdomBuilders = null;
            _pendingFlush = null;
            _pendingRegenKingdoms = null;
            _regenRequested = false;
            MapScene = null;
        }
    }
}