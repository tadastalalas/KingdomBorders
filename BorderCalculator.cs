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
        /// Uses 2 iterations to reduce output point count.
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