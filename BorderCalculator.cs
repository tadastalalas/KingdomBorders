using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace KingdomBorders
{
    public class BorderEdge
    {
        public Vec2 Start;
        public Vec2 End;
        public Kingdom KingdomA;
        public Kingdom KingdomB;
    }

    public class BorderSegment
    {
        public List<Vec2> Points = new List<Vec2>();
        public Kingdom KingdomA;
        public Kingdom KingdomB;
    }

    /// <summary>
    /// Stores information about a segment endpoint arriving at a junction,
    /// including which side each kingdom is on and the actual strip endpoint positions.
    /// </summary>
    public class JunctionArm
    {
        public BorderSegment Segment;
        public bool IsHead; // true = segment's first point is at junction, false = last point
        public Kingdom LeftKingdom;
        public Kingdom RightKingdom;

        // Actual strip endpoint positions at the junction end, filled during segment processing.
        // "Left" strip = the strip on the left side of the smoothed polyline (positive offset).
        public Vec2 LeftInnerPt;
        public Vec2 LeftOuterPt;
        public Vec2 RightInnerPt;
        public Vec2 RightOuterPt;

        // Direction the segment travels AWAY from the junction (outward direction).
        public Vec2 OutwardDir;
    }

    /// <summary>
    /// A point where 2 or more border segments from different kingdom pairs terminate.
    /// A third border segment may pass straight through the junction without ending here.
    /// </summary>
    public class JunctionInfo
    {
        public Vec2 Position;
        public List<JunctionArm> Arms = new List<JunctionArm>();
    }

    /// <summary>
    /// Pre-cached settlement data to avoid repeated property lookups during grid building.
    /// </summary>
    internal struct SettlementEntry
    {
        public float X;
        public float Y;
        public Kingdom Kingdom;
    }

    public class BorderCalculator
    {
        private readonly int _gridResolution;
        private Vec2 _mapMin;
        private Vec2 _mapMax;
        private Kingdom[,] _territoryGrid;

        // Pre-cached settlement data for fast grid building
        private SettlementEntry[] _settlementCache;

        // Spatial lookup grid for settlements — limits comparisons per cell
        private const int SpatialBucketRes = 20;
        private List<int>[,] _spatialBuckets;
        private float _bucketCellW;
        private float _bucketCellH;

        // Incremental grid building state
        private int _gridBuildRow;
        private bool _gridBuildComplete;

        public BorderCalculator(int resolution = 150)
        {
            _gridResolution = resolution;
        }

        public void CalculateMapBounds()
        {
            var allSettlements = Settlement.All
                .Where(s => s.IsTown || s.IsCastle || s.IsVillage)
                .ToList();

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            foreach (var s in allSettlements)
            {
                Vec2 pos = s.Position.ToVec2();
                if (pos.x < minX) minX = pos.x;
                if (pos.x > maxX) maxX = pos.x;
                if (pos.y < minY) minY = pos.y;
                if (pos.y > maxY) maxY = pos.y;
            }

            float padding = 30f;
            _mapMin = new Vec2(minX - padding, minY - padding);
            _mapMax = new Vec2(maxX + padding, maxY + padding);

            ModLog.Log($"Map bounds: ({_mapMin.x:F2}, {_mapMin.y:F2}) to ({_mapMax.x:F2}, {_mapMax.y:F2})");
        }

        /// <summary>
        /// Builds a flat cache of settlement positions and kingdoms, and a coarse spatial
        /// lookup grid so each territory cell only needs to compare against nearby settlements.
        /// </summary>
        private void BuildSettlementCache()
        {
            var fiefs = Settlement.All
                .Where(s => s.IsTown || s.IsCastle || s.IsVillage)
                .ToList();

            _settlementCache = new SettlementEntry[fiefs.Count];

            for (int i = 0; i < fiefs.Count; i++)
            {
                var s = fiefs[i];
                Vec2 pos = s.Position.ToVec2();
                Kingdom kingdom;
                if (s.IsVillage)
                    kingdom = s.Village.Bound?.OwnerClan?.Kingdom;
                else
                    kingdom = s.OwnerClan?.Kingdom;

                _settlementCache[i] = new SettlementEntry
                {
                    X = pos.x,
                    Y = pos.y,
                    Kingdom = kingdom
                };
            }

            // Build spatial buckets
            float mapW = _mapMax.x - _mapMin.x;
            float mapH = _mapMax.y - _mapMin.y;
            _bucketCellW = mapW / SpatialBucketRes;
            _bucketCellH = mapH / SpatialBucketRes;

            _spatialBuckets = new List<int>[SpatialBucketRes, SpatialBucketRes];
            for (int bx = 0; bx < SpatialBucketRes; bx++)
                for (int by = 0; by < SpatialBucketRes; by++)
                    _spatialBuckets[bx, by] = new List<int>();

            // Insert each settlement into its bucket and all neighboring buckets
            // so that edge cells can still find the nearest settlement across bucket boundaries
            for (int i = 0; i < _settlementCache.Length; i++)
            {
                int bx = (int)((_settlementCache[i].X - _mapMin.x) / _bucketCellW);
                int by = (int)((_settlementCache[i].Y - _mapMin.y) / _bucketCellH);
                bx = Math.Max(0, Math.Min(SpatialBucketRes - 1, bx));
                by = Math.Max(0, Math.Min(SpatialBucketRes - 1, by));

                // Add to a 3x3 neighborhood so border cells always find nearby settlements
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int nx = bx + dx;
                        int ny = by + dy;
                        if (nx >= 0 && nx < SpatialBucketRes && ny >= 0 && ny < SpatialBucketRes)
                            _spatialBuckets[nx, ny].Add(i);
                    }
                }
            }

            ModLog.Log($"Settlement cache: {_settlementCache.Length} entries, {SpatialBucketRes}x{SpatialBucketRes} spatial grid");
        }

        /// <summary>
        /// Finds the nearest settlement kingdom for a world position using spatial buckets
        /// and squared distances (no sqrt).
        /// </summary>
        private Kingdom FindNearestKingdom(float worldX, float worldY)
        {
            int bx = (int)((worldX - _mapMin.x) / _bucketCellW);
            int by = (int)((worldY - _mapMin.y) / _bucketCellH);
            bx = Math.Max(0, Math.Min(SpatialBucketRes - 1, bx));
            by = Math.Max(0, Math.Min(SpatialBucketRes - 1, by));

            var candidates = _spatialBuckets[bx, by];
            float bestDistSq = float.MaxValue;
            Kingdom bestKingdom = null;

            for (int i = 0; i < candidates.Count; i++)
            {
                ref var entry = ref _settlementCache[candidates[i]];
                float dx = worldX - entry.X;
                float dy = worldY - entry.Y;
                float distSq = dx * dx + dy * dy;

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestKingdom = entry.Kingdom;
                }
            }

            // Fallback: if bucket was empty (shouldn't happen with 3x3 spread), brute force
            if (bestKingdom == null && _settlementCache.Length > 0)
            {
                for (int i = 0; i < _settlementCache.Length; i++)
                {
                    ref var entry = ref _settlementCache[i];
                    float dx = worldX - entry.X;
                    float dy = worldY - entry.Y;
                    float distSq = dx * dx + dy * dy;

                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestKingdom = entry.Kingdom;
                    }
                }
            }

            return bestKingdom;
        }

        /// <summary>
        /// Prepares for incremental grid building. Call BuildTerritoryGridIncremental() each tick.
        /// </summary>
        public void BeginBuildTerritoryGrid()
        {
            _territoryGrid = new Kingdom[_gridResolution, _gridResolution];
            BuildSettlementCache();
            _gridBuildRow = 0;
            _gridBuildComplete = false;
        }

        /// <summary>
        /// Builds a limited number of grid rows per call. Returns true when complete.
        /// </summary>
        public bool BuildTerritoryGridIncremental(int rowsPerTick = 25)
        {
            if (_gridBuildComplete)
                return true;

            int endRow = Math.Min(_gridBuildRow + rowsPerTick, _gridResolution);
            float mapW = _mapMax.x - _mapMin.x;
            float mapH = _mapMax.y - _mapMin.y;
            float invResX = 1f / (_gridResolution - 1);
            float invResY = 1f / (_gridResolution - 1);

            for (int x = _gridBuildRow; x < endRow; x++)
            {
                float worldX = _mapMin.x + (x * invResX) * mapW;

                for (int y = 0; y < _gridResolution; y++)
                {
                    float worldY = _mapMin.y + (y * invResY) * mapH;
                    _territoryGrid[x, y] = FindNearestKingdom(worldX, worldY);
                }
            }

            _gridBuildRow = endRow;

            if (_gridBuildRow >= _gridResolution)
            {
                _gridBuildComplete = true;
                _spatialBuckets = null; // Free memory
                ModLog.Log("Territory grid build complete");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Rebuilds the entire grid. Refreshes the settlement cache first to pick up
        /// ownership changes, then uses the same fast spatial lookup.
        /// </summary>
        public bool RebuildTerritoryGridForKingdoms(HashSet<Kingdom> affectedKingdoms)
        {
            if (_territoryGrid == null)
                return false;

            // Rebuild cache to pick up new ownership
            BuildSettlementCache();

            bool anyChanged = false;
            float mapW = _mapMax.x - _mapMin.x;
            float mapH = _mapMax.y - _mapMin.y;
            float invResX = 1f / (_gridResolution - 1);
            float invResY = 1f / (_gridResolution - 1);

            for (int x = 0; x < _gridResolution; x++)
            {
                float worldX = _mapMin.x + (x * invResX) * mapW;

                for (int y = 0; y < _gridResolution; y++)
                {
                    float worldY = _mapMin.y + (y * invResY) * mapH;
                    Kingdom newKingdom = FindNearestKingdom(worldX, worldY);

                    if (_territoryGrid[x, y] != newKingdom)
                    {
                        _territoryGrid[x, y] = newKingdom;
                        anyChanged = true;
                    }
                }
            }

            _spatialBuckets = null; // Free memory
            return anyChanged;
        }

        /// <summary>
        /// Rebuilds only the grid cells within a region around the changed settlements.
        /// Finds the max distance to the nearest neighboring settlement to determine
        /// how far the ownership change could ripple, then only re-evaluates that area.
        /// </summary>
        public bool RebuildTerritoryGridAroundSettlements(HashSet<Settlement> changedSettlements)
        {
            if (_territoryGrid == null || changedSettlements == null || changedSettlements.Count == 0)
                return false;

            // Rebuild the settlement cache to pick up new ownership
            BuildSettlementCache();

            // Compute bounding box of changed settlements
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            foreach (var s in changedSettlements)
            {
                Vec2 pos = s.Position.ToVec2();
                if (pos.x < minX) minX = pos.x;
                if (pos.x > maxX) maxX = pos.x;
                if (pos.y < minY) minY = pos.y;
                if (pos.y > maxY) maxY = pos.y;
            }

            // Expand by the max distance to the nearest neighbor of any changed settlement.
            // This is the furthest a Voronoi boundary could shift.
            float maxNeighborDist = 0f;
            foreach (var s in changedSettlements)
            {
                Vec2 pos = s.Position.ToVec2();
                float nearestDist = float.MaxValue;

                for (int i = 0; i < _settlementCache.Length; i++)
                {
                    ref var entry = ref _settlementCache[i];
                    float dx = pos.x - entry.X;
                    float dy = pos.y - entry.Y;
                    float distSq = dx * dx + dy * dy;

                    // Skip self (distance ~0)
                    if (distSq > 0.1f && distSq < nearestDist)
                        nearestDist = distSq;
                }

                if (nearestDist < float.MaxValue)
                {
                    float dist = (float)Math.Sqrt(nearestDist);
                    if (dist > maxNeighborDist)
                        maxNeighborDist = dist;
                }
            }

            // Expand bounding box by neighbor distance + margin
            float expand = maxNeighborDist + 10f;
            minX -= expand;
            maxX += expand;
            minY -= expand;
            maxY += expand;

            // Convert to grid coordinates
            float mapW = _mapMax.x - _mapMin.x;
            float mapH = _mapMax.y - _mapMin.y;
            int gridMinX = (int)(((minX - _mapMin.x) / mapW) * (_gridResolution - 1));
            int gridMaxX = (int)(((maxX - _mapMin.x) / mapW) * (_gridResolution - 1));
            int gridMinY = (int)(((minY - _mapMin.y) / mapH) * (_gridResolution - 1));
            int gridMaxY = (int)(((maxY - _mapMin.y) / mapH) * (_gridResolution - 1));

            // Clamp to grid bounds
            gridMinX = Math.Max(0, gridMinX);
            gridMaxX = Math.Min(_gridResolution - 1, gridMaxX);
            gridMinY = Math.Max(0, gridMinY);
            gridMaxY = Math.Min(_gridResolution - 1, gridMaxY);

            int cellsUpdated = 0;
            bool anyChanged = false;
            float invResX = 1f / (_gridResolution - 1);
            float invResY = 1f / (_gridResolution - 1);

            for (int x = gridMinX; x <= gridMaxX; x++)
            {
                float worldX = _mapMin.x + (x * invResX) * mapW;

                for (int y = gridMinY; y <= gridMaxY; y++)
                {
                    float worldY = _mapMin.y + (y * invResY) * mapH;
                    Kingdom newKingdom = FindNearestKingdom(worldX, worldY);

                    if (_territoryGrid[x, y] != newKingdom)
                    {
                        _territoryGrid[x, y] = newKingdom;
                        anyChanged = true;
                    }
                    cellsUpdated++;
                }
            }

            _spatialBuckets = null; // Free memory

            int totalCells = _gridResolution * _gridResolution;
            ModLog.Log($"Partial grid rebuild: {cellsUpdated}/{totalCells} cells ({100f * cellsUpdated / totalCells:F1}%)");

            return anyChanged;
        }

        public List<BorderEdge> FindBorderEdges()
        {
            var edges = new List<BorderEdge>();

            for (int x = 0; x < _gridResolution - 1; x++)
            {
                for (int y = 0; y < _gridResolution - 1; y++)
                {
                    Kingdom current = _territoryGrid[x, y];

                    Kingdom right = _territoryGrid[x + 1, y];
                    if (current != right && (current != null || right != null))
                    {
                        edges.Add(new BorderEdge
                        {
                            Start = GridToWorld(x + 1, y),
                            End = GridToWorld(x + 1, y + 1),
                            KingdomA = current,
                            KingdomB = right
                        });
                    }

                    Kingdom top = _territoryGrid[x, y + 1];
                    if (current != top && (current != null || top != null))
                    {
                        edges.Add(new BorderEdge
                        {
                            Start = GridToWorld(x, y + 1),
                            End = GridToWorld(x + 1, y + 1),
                            KingdomA = current,
                            KingdomB = top
                        });
                    }
                }
            }

            ModLog.Log($"Found {edges.Count} border edges");
            return edges;
        }

        public List<BorderEdge> FindBorderEdgesForKingdoms(HashSet<Kingdom> kingdoms)
        {
            var edges = new List<BorderEdge>();

            for (int x = 0; x < _gridResolution - 1; x++)
            {
                for (int y = 0; y < _gridResolution - 1; y++)
                {
                    Kingdom current = _territoryGrid[x, y];

                    Kingdom right = _territoryGrid[x + 1, y];
                    if (current != right && (current != null || right != null))
                    {
                        if (kingdoms.Contains(current) || kingdoms.Contains(right))
                        {
                            edges.Add(new BorderEdge
                            {
                                Start = GridToWorld(x + 1, y),
                                End = GridToWorld(x + 1, y + 1),
                                KingdomA = current,
                                KingdomB = right
                            });
                        }
                    }

                    Kingdom top = _territoryGrid[x, y + 1];
                    if (current != top && (current != null || top != null))
                    {
                        if (kingdoms.Contains(current) || kingdoms.Contains(top))
                        {
                            edges.Add(new BorderEdge
                            {
                                Start = GridToWorld(x, y + 1),
                                End = GridToWorld(x + 1, y + 1),
                                KingdomA = current,
                                KingdomB = top
                            });
                        }
                    }
                }
            }

            ModLog.Log($"Found {edges.Count} border edges for {kingdoms.Count} affected kingdoms");
            return edges;
        }

        /// <summary>
        /// Chains edges into segments using spatial hashing for fast endpoint lookup.
        /// </summary>
        public List<BorderSegment> ChainEdges(List<BorderEdge> edges)
        {
            var pointToEdges = new Dictionary<long, List<BorderEdge>>();

            foreach (var edge in edges)
            {
                long startKey = HashPoint(edge.Start);
                long endKey = HashPoint(edge.End);

                if (!pointToEdges.ContainsKey(startKey))
                    pointToEdges[startKey] = new List<BorderEdge>();
                pointToEdges[startKey].Add(edge);

                if (!pointToEdges.ContainsKey(endKey))
                    pointToEdges[endKey] = new List<BorderEdge>();
                pointToEdges[endKey].Add(edge);
            }

            var segments = new List<BorderSegment>();
            var used = new HashSet<BorderEdge>();

            foreach (var seed in edges)
            {
                if (used.Contains(seed))
                    continue;

                used.Add(seed);

                var segment = new BorderSegment
                {
                    KingdomA = seed.KingdomA,
                    KingdomB = seed.KingdomB
                };
                segment.Points.Add(seed.Start);
                segment.Points.Add(seed.End);

                ExtendChain(segment, false, pointToEdges, used);
                ExtendChain(segment, true, pointToEdges, used);

                segments.Add(segment);
            }

            ModLog.Log($"Chained into {segments.Count} segments");
            return segments;
        }

        private void ExtendChain(BorderSegment segment, bool fromHead,
            Dictionary<long, List<BorderEdge>> pointToEdges, HashSet<BorderEdge> used)
        {
            bool extended = true;
            while (extended)
            {
                extended = false;
                Vec2 tip = fromHead ? segment.Points[0] : segment.Points[segment.Points.Count - 1];
                long tipKey = HashPoint(tip);

                if (!pointToEdges.TryGetValue(tipKey, out var candidates))
                    break;

                foreach (var edge in candidates)
                {
                    if (used.Contains(edge))
                        continue;
                    if (!SameKingdomPair(edge, segment))
                        continue;

                    if (ApproxEqual(edge.Start, tip))
                    {
                        if (fromHead)
                            segment.Points.Insert(0, edge.End);
                        else
                            segment.Points.Add(edge.End);
                        used.Add(edge);
                        extended = true;
                        break;
                    }
                    else if (ApproxEqual(edge.End, tip))
                    {
                        if (fromHead)
                            segment.Points.Insert(0, edge.Start);
                        else
                            segment.Points.Add(edge.Start);
                        used.Add(edge);
                        extended = true;
                        break;
                    }
                }
            }
        }

        private long HashPoint(Vec2 p, float cellSize = 0.05f)
        {
            int qx = (int)Math.Round(p.x / cellSize);
            int qy = (int)Math.Round(p.y / cellSize);
            return ((long)qx << 32) | (uint)qy;
        }

        /// <summary>
        /// Chaikin's corner-cutting algorithm for smoothing polylines.
        /// </summary>
        public List<Vec2> SmoothChaikin(List<Vec2> points, int iterations = 2)
        {
            if (points.Count < 3)
                return new List<Vec2>(points);

            var result = new List<Vec2>(points);

            for (int iter = 0; iter < iterations; iter++)
            {
                var smoothed = new List<Vec2>(result.Count * 2);

                smoothed.Add(result[0]);

                for (int i = 0; i < result.Count - 1; i++)
                {
                    Vec2 p0 = result[i];
                    Vec2 p1 = result[i + 1];

                    smoothed.Add(p0 * 0.75f + p1 * 0.25f);
                    smoothed.Add(p0 * 0.25f + p1 * 0.75f);
                }

                smoothed.Add(result[result.Count - 1]);

                result = smoothed;
            }

            return result;
        }

        /// <summary>
        /// Determines which kingdom is on the left vs right side of the polyline.
        /// </summary>
        public (Kingdom left, Kingdom right) DetermineKingdomSides(
            List<Vec2> linePoints, Kingdom kingdomA, Kingdom kingdomB)
        {
            if (linePoints.Count < 2)
                return (kingdomA, kingdomB);

            int midIndex = linePoints.Count / 2;
            Vec2 p0 = linePoints[Math.Max(0, midIndex - 1)];
            Vec2 p1 = linePoints[Math.Min(linePoints.Count - 1, midIndex + 1)];

            Vec2 dir = (p1 - p0).Normalized();
            Vec2 leftPerpendicular = new Vec2(-dir.y, dir.x);

            Vec2 midPoint = linePoints[midIndex];
            Vec2 samplePoint = midPoint + leftPerpendicular * 5f;

            Kingdom leftKingdom = GetKingdomAtPosition(samplePoint);

            if (leftKingdom == kingdomA)
                return (kingdomA, kingdomB);
            else if (leftKingdom == kingdomB)
                return (kingdomB, kingdomA);
            else
                return (kingdomA, kingdomB);
        }

        public Kingdom GetKingdomAtPosition(Vec2 position)
        {
            int x = (int)(((position.x - _mapMin.x) / (_mapMax.x - _mapMin.x)) * (_gridResolution - 1));
            int y = (int)(((position.y - _mapMin.y) / (_mapMax.y - _mapMin.y)) * (_gridResolution - 1));

            x = Math.Max(0, Math.Min(_gridResolution - 1, x));
            y = Math.Max(0, Math.Min(_gridResolution - 1, y));

            return _territoryGrid[x, y];
        }

        public static uint GetKingdomColor(Kingdom kingdom)
        {
            if (kingdom == null)
                return 0x80808080;

            uint color = kingdom.PrimaryBannerColor;
            return color | 0xFF000000;
        }

        public static List<Vec2> OffsetPolyline(List<Vec2> points, float offset)
        {
            if (points.Count < 2)
                return new List<Vec2>(points);

            var result = new List<Vec2>(points.Count);

            for (int i = 0; i < points.Count; i++)
            {
                Vec2 perpendicular;

                if (i == 0)
                {
                    Vec2 dir = (points[1] - points[0]).Normalized();
                    perpendicular = new Vec2(-dir.y, dir.x);
                }
                else if (i == points.Count - 1)
                {
                    Vec2 dir = (points[i] - points[i - 1]).Normalized();
                    perpendicular = new Vec2(-dir.y, dir.x);
                }
                else
                {
                    Vec2 dir1 = (points[i] - points[i - 1]).Normalized();
                    Vec2 dir2 = (points[i + 1] - points[i]).Normalized();
                    Vec2 avgDir = (dir1 + dir2).Normalized();
                    perpendicular = new Vec2(-avgDir.y, avgDir.x);
                }

                result.Add(points[i] + perpendicular * offset);
            }

            return result;
        }

        public static List<Vec2> TrimPolyline(List<Vec2> points, float trimDistance)
        {
            if (points.Count < 2 || trimDistance <= 0f)
                return new List<Vec2>(points);

            var result = new List<Vec2>(points);

            // Trim from start
            float remaining = trimDistance;
            while (result.Count >= 2 && remaining > 0f)
            {
                Vec2 a = result[0];
                Vec2 b = result[1];
                float segLen = a.Distance(b);

                if (segLen <= remaining)
                {
                    remaining -= segLen;
                    result.RemoveAt(0);
                }
                else
                {
                    Vec2 dir = (b - a).Normalized();
                    result[0] = a + dir * remaining;
                    remaining = 0f;
                }
            }

            // Trim from end
            remaining = trimDistance;
            while (result.Count >= 2 && remaining > 0f)
            {
                int last = result.Count - 1;
                Vec2 a = result[last];
                Vec2 b = result[last - 1];
                float segLen = a.Distance(b);

                if (segLen <= remaining)
                {
                    remaining -= segLen;
                    result.RemoveAt(last);
                }
                else
                {
                    Vec2 dir = (b - a).Normalized();
                    result[last] = a + dir * remaining;
                    remaining = 0f;
                }
            }

            return result;
        }

        /// <summary>
        /// Trims a polyline by independent amounts at each end.
        /// </summary>
        public static List<Vec2> TrimPolylineEnds(List<Vec2> points, float headTrim, float tailTrim)
        {
            if (points.Count < 2)
                return new List<Vec2>(points);

            var result = new List<Vec2>(points);

            // Trim from start
            if (headTrim > 0f)
            {
                float remaining = headTrim;
                while (result.Count >= 2 && remaining > 0f)
                {
                    Vec2 a = result[0];
                    Vec2 b = result[1];
                    float segLen = a.Distance(b);

                    if (segLen <= remaining)
                    {
                        remaining -= segLen;
                        result.RemoveAt(0);
                    }
                    else
                    {
                        Vec2 dir = (b - a).Normalized();
                        result[0] = a + dir * remaining;
                        remaining = 0f;
                    }
                }
            }

            // Trim from end
            if (tailTrim > 0f)
            {
                float remaining = tailTrim;
                while (result.Count >= 2 && remaining > 0f)
                {
                    int last = result.Count - 1;
                    Vec2 a = result[last];
                    Vec2 b = result[last - 1];
                    float segLen = a.Distance(b);

                    if (segLen <= remaining)
                    {
                        remaining -= segLen;
                        result.RemoveAt(last);
                    }
                    else
                    {
                        Vec2 dir = (b - a).Normalized();
                        result[last] = a + dir * remaining;
                        remaining = 0f;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Detects junctions where 2 or more border segments from different kingdom pairs
        /// terminate at the same point. The pass-through border (if any) chains straight
        /// through and doesn't have an endpoint here — only the turning borders do.
        /// </summary>
        public List<JunctionInfo> FindJunctions(List<BorderSegment> segments)
        {
            // Group segment endpoints by quantized position
            var endpointMap = new Dictionary<long, List<(BorderSegment segment, bool isHead)>>();

            foreach (var seg in segments)
            {
                if (seg.Points.Count < 2)
                    continue;

                Vec2 head = seg.Points[0];
                Vec2 tail = seg.Points[seg.Points.Count - 1];

                long headKey = HashPoint(head);
                long tailKey = HashPoint(tail);

                if (!endpointMap.ContainsKey(headKey))
                    endpointMap[headKey] = new List<(BorderSegment, bool)>();
                endpointMap[headKey].Add((seg, true));

                if (!endpointMap.ContainsKey(tailKey))
                    endpointMap[tailKey] = new List<(BorderSegment, bool)>();
                endpointMap[tailKey].Add((seg, false));
            }

            var junctions = new List<JunctionInfo>();

            foreach (var kvp in endpointMap)
            {
                // A junction needs at least 2 segment endpoints from different kingdom pairs.
                // The pass-through border doesn't end here — it chains straight through.
                if (kvp.Value.Count < 2)
                    continue;

                // Verify at least 2 different kingdom pairs meet here
                var pairs = new HashSet<long>();
                foreach (var (seg, _) in kvp.Value)
                {
                    // Create a canonical pair key from the two kingdom hash codes
                    int hA = seg.KingdomA?.GetHashCode() ?? 0;
                    int hB = seg.KingdomB?.GetHashCode() ?? 0;
                    long pairKey = hA < hB
                        ? ((long)hA << 32) | (uint)hB
                        : ((long)hB << 32) | (uint)hA;
                    pairs.Add(pairKey);
                }

                if (pairs.Count < 2)
                    continue;

                // Compute average position of all endpoints at this junction
                Vec2 avgPos = Vec2.Zero;
                foreach (var (seg, isHead) in kvp.Value)
                {
                    Vec2 pt = isHead ? seg.Points[0] : seg.Points[seg.Points.Count - 1];
                    avgPos += pt;
                }
                avgPos *= (1f / kvp.Value.Count);

                var junction = new JunctionInfo { Position = avgPos };

                foreach (var (seg, isHead) in kvp.Value)
                {
                    junction.Arms.Add(new JunctionArm
                    {
                        Segment = seg,
                        IsHead = isHead,
                        LeftKingdom = null,
                        RightKingdom = null
                    });
                }

                junctions.Add(junction);
            }

            ModLog.Log($"Found {junctions.Count} junctions ({junctions.Sum(j => j.Arms.Count)} turning arms)");
            return junctions;
        }

        /// <summary>
        /// Generates a smooth bevel/miter join strip connecting two strip endpoints at a junction.
        /// Uses the actual inner→outer perpendicular of each strip endpoint to correctly
        /// determine which side is concave vs convex, independent of the segment's centerline
        /// direction. This ensures both kingdoms at a junction get correct rounded corners.
        /// </summary>
        public static (List<Vec2> inner, List<Vec2> outer) GenerateJunctionJoin(
            Vec2 ptA_inner, Vec2 ptA_outer, Vec2 dirA,
            Vec2 ptB_inner, Vec2 ptB_outer, Vec2 dirB,
            int curveSegments)
        {
            curveSegments = Math.Max(2, curveSegments);

            // Determine which side is concave vs convex by checking whether the outer
            // points are farther from or closer to the midpoint between the two strip centers.
            // This works correctly regardless of which side of the border centerline the
            // kingdom's strip is on.
            Vec2 midA = (ptA_inner + ptA_outer) * 0.5f;
            Vec2 midB = (ptB_inner + ptB_outer) * 0.5f;
            Vec2 junctionMid = (midA + midB) * 0.5f;

            // The "outer" points of the strip face AWAY from the border centerline.
            // At a convex (outside) turn, outer points are farther from the junction center.
            // At a concave (inside) turn, outer points are closer to the junction center.
            float outerDistA = (ptA_outer - junctionMid).LengthSquared;
            float innerDistA = (ptA_inner - junctionMid).LengthSquared;

            // If outer is farther from junction center → this strip is on the convex (outside)
            // of the turn → outer gets the Bezier curve, inner gets the miter.
            // If outer is closer → this strip is on the concave (inside) → swap roles.
            bool outerIsConvex = outerDistA >= innerDistA;

            Vec2 concaveA, concaveB, convexA, convexB;

            if (outerIsConvex)
            {
                // Normal case: inner=concave (miter), outer=convex (curve)
                concaveA = ptA_inner;
                concaveB = ptB_inner;
                convexA = ptA_outer;
                convexB = ptB_outer;
            }
            else
            {
                // Flipped case: outer=concave (miter), inner=convex (curve)
                concaveA = ptA_outer;
                concaveB = ptB_outer;
                convexA = ptA_inner;
                convexB = ptB_inner;
            }

            // --- Concave side: miter intersection ---
            Vec2 miterPt;
            if (!TryLineIntersection(concaveA, dirA, concaveB, dirB, out miterPt))
            {
                miterPt = (concaveA + concaveB) * 0.5f;
            }

            // Clamp miter to avoid extremely long spikes on near-parallel segments
            float miterDistA = (miterPt - concaveA).Length;
            float miterDistB = (miterPt - concaveB).Length;
            float stripWidth = (ptA_outer - ptA_inner).Length;
            float maxMiter = stripWidth * 3f;

            if (miterDistA > maxMiter || miterDistB > maxMiter)
            {
                miterPt = (concaveA + concaveB) * 0.5f;
            }

            var concavePts = new List<Vec2>(3) { concaveA, miterPt, concaveB };

            // --- Convex side: smooth rounded curve ---
            Vec2 controlPt;
            if (!TryLineIntersection(convexA, dirA, convexB, dirB, out controlPt))
            {
                controlPt = (convexA + convexB) * 0.5f;
            }

            float ctrlDistA = (controlPt - convexA).Length;
            float ctrlDistB = (controlPt - convexB).Length;
            if (ctrlDistA > maxMiter || ctrlDistB > maxMiter)
            {
                controlPt = (convexA + convexB) * 0.5f;
            }

            var convexPts = new List<Vec2>(curveSegments + 1);
            for (int i = 0; i <= curveSegments; i++)
            {
                float t = (float)i / curveSegments;
                float oneMinusT = 1f - t;
                Vec2 pt = convexA * (oneMinusT * oneMinusT)
                         + controlPt * (2f * oneMinusT * t)
                         + convexB * (t * t);
                convexPts.Add(pt);
            }

            // Resample concave side to match convex point count for the mesh renderer
            var concaveResampled = ResamplePolyline(concavePts, convexPts.Count);

            // Return in correct inner/outer order
            if (outerIsConvex)
                return (concaveResampled, convexPts);   // inner=concave, outer=convex
            else
                return (convexPts, concaveResampled);   // inner=convex, outer=concave
        }

        /// <summary>
        /// Attempts to find the intersection point of two lines defined by point+direction.
        /// Returns false if the lines are nearly parallel.
        /// The directions point AWAY from the junction (outward along the segment).
        /// We negate them so lines extend TOWARD each other for intersection.
        /// </summary>
        private static bool TryLineIntersection(Vec2 p1, Vec2 d1, Vec2 p2, Vec2 d2, out Vec2 intersection)
        {
            float dx1 = -d1.x, dy1 = -d1.y;
            float dx2 = -d2.x, dy2 = -d2.y;

            float denom = dx1 * dy2 - dy1 * dx2;

            if (Math.Abs(denom) < 1e-6f)
            {
                intersection = Vec2.Zero;
                return false;
            }

            float diffX = p2.x - p1.x;
            float diffY = p2.y - p1.y;
            float t = (diffX * dy2 - diffY * dx2) / denom;

            intersection = new Vec2(p1.x + dx1 * t, p1.y + dy1 * t);
            return true;
        }

        /// <summary>
        /// Resamples a polyline to have exactly targetCount evenly-spaced points.
        /// </summary>
        private static List<Vec2> ResamplePolyline(List<Vec2> points, int targetCount)
        {
            if (points.Count == 0 || targetCount <= 0)
                return new List<Vec2>();

            if (targetCount == 1)
                return new List<Vec2> { points[points.Count / 2] };

            float totalLength = 0f;
            var cumLengths = new List<float>(points.Count) { 0f };
            for (int i = 1; i < points.Count; i++)
            {
                totalLength += points[i].Distance(points[i - 1]);
                cumLengths.Add(totalLength);
            }

            if (totalLength < 1e-6f)
            {
                var result = new List<Vec2>(targetCount);
                for (int i = 0; i < targetCount; i++)
                    result.Add(points[0]);
                return result;
            }

            var resampled = new List<Vec2>(targetCount);
            for (int i = 0; i < targetCount; i++)
            {
                float targetDist = (float)i / (targetCount - 1) * totalLength;

                int seg = 0;
                for (int j = 1; j < cumLengths.Count; j++)
                {
                    if (cumLengths[j] >= targetDist)
                    {
                        seg = j - 1;
                        break;
                    }
                    seg = j - 1;
                }

                float segLen = cumLengths[seg + 1] - cumLengths[seg];
                float t = segLen > 1e-6f ? (targetDist - cumLengths[seg]) / segLen : 0f;
                t = Math.Max(0f, Math.Min(1f, t));

                resampled.Add(points[seg] * (1f - t) + points[seg + 1] * t);
            }

            return resampled;
        }

        /// <summary>
        /// Checks if a segment endpoint (head or tail) is at any known junction.
        /// Returns the JunctionArm if found, null otherwise.
        /// </summary>
        public JunctionArm FindArmAtJunction(BorderSegment segment, bool checkHead,
            List<JunctionInfo> junctions, HashSet<long> junctionKeys)
        {
            if (junctions == null || junctionKeys == null)
                return null;

            Vec2 pt = checkHead ? segment.Points[0] : segment.Points[segment.Points.Count - 1];
            if (!junctionKeys.Contains(HashPoint(pt)))
                return null;

            foreach (var junction in junctions)
            {
                foreach (var arm in junction.Arms)
                {
                    if (arm.Segment == segment && arm.IsHead == checkHead)
                        return arm;
                }
            }
            return null;
        }

        /// <summary>
        /// Checks if a segment endpoint (head or tail) is at any known junction.
        /// </summary>
        public bool IsEndpointAtJunction(BorderSegment segment, bool checkHead,
            HashSet<long> junctionKeys)
        {
            if (junctionKeys == null)
                return false;

            Vec2 pt = checkHead ? segment.Points[0] : segment.Points[segment.Points.Count - 1];
            return junctionKeys.Contains(HashPoint(pt));
        }

        /// <summary>
        /// Returns a set of hash keys for all junction positions, for fast lookup.
        /// </summary>
        public HashSet<long> GetJunctionKeys(List<JunctionInfo> junctions)
        {
            var keys = new HashSet<long>();
            foreach (var j in junctions)
            {
                keys.Add(HashPoint(j.Position));
            }
            return keys;
        }

        private Vec2 GridToWorld(int x, int y)
        {
            float worldX = _mapMin.x + (x / (float)(_gridResolution - 1)) * (_mapMax.x - _mapMin.x);
            float worldY = _mapMin.y + (y / (float)(_gridResolution - 1)) * (_mapMax.y - _mapMin.y);
            return new Vec2(worldX, worldY);
        }

        private bool ApproxEqual(Vec2 a, Vec2 b, float threshold = 0.1f)
        {
            return a.Distance(b) < threshold;
        }

        private bool SameKingdomPair(BorderEdge edge, BorderSegment segment)
        {
            return (edge.KingdomA == segment.KingdomA && edge.KingdomB == segment.KingdomB) ||
                    (edge.KingdomA == segment.KingdomB && edge.KingdomB == segment.KingdomA);
        }
    }
}