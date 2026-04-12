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

    public class BorderCalculator
    {
        private readonly int _gridResolution;
        private Vec2 _mapMin;
        private Vec2 _mapMax;
        private Kingdom[,] _territoryGrid;

        // Incremental grid building state
        private List<Settlement> _cachedFiefs;
        private int _gridBuildRow;
        private bool _gridBuildComplete;

        public BorderCalculator(int resolution = 150)
        {
            _gridResolution = resolution;
        }

        public void CalculateMapBounds()
        {
            // Include towns, castles, AND villages for more accurate bounds
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
        /// Prepares for incremental grid building. Call BuildTerritoryGridIncremental() each tick.
        /// </summary>
        public void BeginBuildTerritoryGrid()
        {
            _territoryGrid = new Kingdom[_gridResolution, _gridResolution];
            _cachedFiefs = Settlement.All
                .Where(s => s.IsTown || s.IsCastle || s.IsVillage)
                .ToList();
            _gridBuildRow = 0;
            _gridBuildComplete = false;

            ModLog.Log($"Building grid {_gridResolution}x{_gridResolution} from {_cachedFiefs.Count} fiefs (towns+castles+villages)");
        }

        /// <summary>
        /// Builds a limited number of grid rows per call. Returns true when complete.
        /// </summary>
        public bool BuildTerritoryGridIncremental(int rowsPerTick = 10)
        {
            if (_gridBuildComplete)
                return true;

            int endRow = Math.Min(_gridBuildRow + rowsPerTick, _gridResolution);

            for (int x = _gridBuildRow; x < endRow; x++)
            {
                for (int y = 0; y < _gridResolution; y++)
                {
                    Vec2 worldPos = GridToWorld(x, y);

                    Settlement nearest = null;
                    float nearestDist = float.MaxValue;

                    foreach (var fief in _cachedFiefs)
                    {
                        float dist = worldPos.Distance(fief.Position.ToVec2());
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearest = fief;
                        }
                    }

                    if (nearest != null)
                    {
                        if (nearest.IsVillage)
                            _territoryGrid[x, y] = nearest.Village.Bound?.OwnerClan?.Kingdom;
                        else
                            _territoryGrid[x, y] = nearest.OwnerClan?.Kingdom;
                    }
                }
            }

            _gridBuildRow = endRow;

            if (_gridBuildRow >= _gridResolution)
            {
                _gridBuildComplete = true;
                _cachedFiefs = null;
                ModLog.Log("Territory grid build complete");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Rebuilds the entire grid (used for kingdom regeneration where we need immediate results).
        /// </summary>
        public bool RebuildTerritoryGridForKingdoms(HashSet<Kingdom> affectedKingdoms)
        {
            if (_territoryGrid == null)
                return false;

            var fiefs = Settlement.All
                .Where(s => s.IsTown || s.IsCastle || s.IsVillage)
                .ToList();

            bool anyChanged = false;

            for (int x = 0; x < _gridResolution; x++)
            {
                for (int y = 0; y < _gridResolution; y++)
                {
                    Vec2 worldPos = GridToWorld(x, y);

                    Settlement nearest = null;
                    float nearestDist = float.MaxValue;

                    foreach (var fief in fiefs)
                    {
                        float dist = worldPos.Distance(fief.Position.ToVec2());
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearest = fief;
                        }
                    }

                    Kingdom newKingdom = null;
                    if (nearest != null)
                    {
                        if (nearest.IsVillage)
                            newKingdom = nearest.Village.Bound?.OwnerClan?.Kingdom;
                        else
                            newKingdom = nearest.OwnerClan?.Kingdom;
                    }

                    if (_territoryGrid[x, y] != newKingdom)
                    {
                        _territoryGrid[x, y] = newKingdom;
                        anyChanged = true;
                    }
                }
            }

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

                    // Check right neighbor
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

                    // Check top neighbor
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

        /// <summary>
        /// Finds border edges only for segments involving any of the specified kingdoms.
        /// </summary>
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
            // Build spatial lookup: endpoint -> list of edges touching that point
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

                // Extend tail
                ExtendChain(segment, false, pointToEdges, used);
                // Extend head
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
        public List<Vec2> SmoothChaikin(List<Vec2> points, int iterations = 3)
        {
            if (points.Count < 3)
                return new List<Vec2>(points);

            var result = new List<Vec2>(points);

            for (int iter = 0; iter < iterations; iter++)
            {
                var smoothed = new List<Vec2>();

                // Keep first point
                smoothed.Add(result[0]);

                for (int i = 0; i < result.Count - 1; i++)
                {
                    Vec2 p0 = result[i];
                    Vec2 p1 = result[i + 1];

                    Vec2 q = p0 * 0.75f + p1 * 0.25f;
                    Vec2 r = p0 * 0.25f + p1 * 0.75f;

                    smoothed.Add(q);
                    smoothed.Add(r);
                }

                // Keep last point
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

        /// <summary>
        /// Gets a display color for a kingdom using its primary banner color.
        /// </summary>
        public static uint GetKingdomColor(Kingdom kingdom)
        {
            if (kingdom == null)
                return 0x80808080; // Gray for unowned

            uint color = kingdom.PrimaryBannerColor;
            // Ensure full alpha
            return color | 0xFF000000;
        }

        /// <summary>
        /// Offsets a polyline by a perpendicular distance to produce a parallel line.
        /// Positive offset = left side, negative = right side.
        /// </summary>
        public static List<Vec2> OffsetPolyline(List<Vec2> points, float offset)
        {
            if (points.Count < 2)
                return new List<Vec2>(points);

            var result = new List<Vec2>();

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