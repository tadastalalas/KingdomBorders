using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace KingdomBorders
{
    /// <summary>
    /// One filled mesh ready for the renderer. With the per-cell fill strategy:
    ///   - SmoothedPerimeter is the flat list of UNIQUE cell-corner vertices (in world space).
    ///   - Triangles is a flat triangle index list referencing SmoothedPerimeter.
    /// Field names are kept for compatibility with BorderRenderer.CreateFillEntity, which
    /// only treats SmoothedPerimeter as "the vertex array" and Triangles as "the indices".
    /// </summary>
    public class FillPolygon
    {
        public Kingdom Kingdom;
        public uint Color;
        public List<Vec2> SmoothedPerimeter; // = vertex array (cell corners, world space)
        public List<int> Triangles;          // index triples into SmoothedPerimeter
    }

    /// <summary>
    /// Blob = a connected set of grid cells belonging to one kingdom (mainland or exclave).
    /// </summary>
    public class KingdomBlob
    {
        public Kingdom Kingdom;
        public List<(int x, int y)> Cells;
    }

    /// <summary>
    /// Builds territory fills as 2 triangles per grid cell. Each triangle's vertices
    /// are sampled to terrain individually by the renderer, so the fill hugs mountains
    /// and valleys instead of cutting through them like the previous big-polygon approach.
    /// </summary>
    public static class TerritoryFillBuilder
    {
        public static List<FillPolygon> BuildFills(
            IList<KingdomBlob> blobs,
            Vec2 mapMin, Vec2 mapMax, int gridResolution,
            int chaikinIterations /* unused with per-cell fill, kept for API stability */)
        {
            var result = new List<FillPolygon>(blobs.Count);

            float invResX = 1f / (gridResolution - 1);
            float invResY = 1f / (gridResolution - 1);
            float mapW = mapMax.x - mapMin.x;
            float mapH = mapMax.y - mapMin.y;

            // Convert a cell-corner grid coordinate to world space.
            Vec2 GridToWorld(int gx, int gy)
            {
                float wx = mapMin.x + gx * invResX * mapW;
                float wy = mapMin.y + gy * invResY * mapH;
                return new Vec2(wx, wy);
            }

            foreach (var blob in blobs)
            {
                if (blob.Kingdom == null || blob.Cells.Count == 0)
                    continue;

                // Map (gx,gy) corner → vertex index, so adjacent cells share corners.
                // Sharing corners cuts vertex count ~4x and lets the terrain sampler
                // do less work.
                var cornerToIndex = new Dictionary<long, int>(blob.Cells.Count * 2);
                var verts = new List<Vec2>(blob.Cells.Count * 2);
                var tris = new List<int>(blob.Cells.Count * 6);

                int GetOrAddCorner(int gx, int gy)
                {
                    long key = ((long)gx << 32) | (uint)gy;
                    if (cornerToIndex.TryGetValue(key, out var idx))
                        return idx;
                    idx = verts.Count;
                    verts.Add(GridToWorld(gx, gy));
                    cornerToIndex[key] = idx;
                    return idx;
                }

                foreach (var (cx, cy) in blob.Cells)
                {
                    // Cell corners:
                    //   bl=(cx,cy)        br=(cx+1,cy)
                    //   tl=(cx,cy+1)      tr=(cx+1,cy+1)
                    int bl = GetOrAddCorner(cx,     cy);
                    int br = GetOrAddCorner(cx + 1, cy);
                    int tl = GetOrAddCorner(cx,     cy + 1);
                    int tr = GetOrAddCorner(cx + 1, cy + 1);

                    // Two triangles forming the quad. CCW winding (consistent so
                    // ComputeNormals + back-face triangle the renderer adds work right).
                    tris.Add(bl); tris.Add(br); tris.Add(tr);
                    tris.Add(bl); tris.Add(tr); tris.Add(tl);
                }

                if (verts.Count < 3 || tris.Count < 3)
                    continue;

                result.Add(new FillPolygon
                {
                    Kingdom = blob.Kingdom,
                    Color = BorderCalculator.GetKingdomColor(blob.Kingdom),
                    SmoothedPerimeter = verts,
                    Triangles = tris
                });
            }

            return result;
        }
    }
}