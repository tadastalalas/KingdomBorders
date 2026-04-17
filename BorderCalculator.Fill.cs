using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace KingdomBorders
{
    /// <summary>
    /// Territory fill helpers. Splits from BorderCalculator.cs to keep this file small.
    /// </summary>
    public partial class BorderCalculator
    {
        /// <summary>
        /// Grid resolution used for territory cell ownership.
        /// </summary>
        public int GridResolution => _gridResolution;

        /// <summary>
        /// World-space position of a grid corner. Cells own the quad whose four
        /// corners are (x,y), (x+1,y), (x,y+1), (x+1,y+1) — identical to the
        /// convention used by FindBorderEdges.
        /// </summary>
        public Vec2 GetGridCornerWorldPos(int gx, int gy)
        {
            return GridToWorld(gx, gy);
        }

        /// <summary>
        /// Groups every owned grid cell by its kingdom. When <paramref name="filter"/>
        /// is non-null, only kingdoms in the set are included (partial rebuild case).
        /// Cells with no owning kingdom are skipped.
        /// </summary>
        public Dictionary<Kingdom, List<(int gx, int gy)>> CollectKingdomCells(HashSet<Kingdom> filter)
        {
            var result = new Dictionary<Kingdom, List<(int, int)>>();

            if (_territoryGrid == null)
                return result;

            // The fill quad for cell (x,y) uses corners up to (x+1,y+1), so we iterate
            // up to _gridResolution - 1 in both axes (matching FindBorderEdges).
            for (int x = 0; x < _gridResolution - 1; x++)
            {
                for (int y = 0; y < _gridResolution - 1; y++)
                {
                    Kingdom k = _territoryGrid[x, y];
                    if (k == null)
                        continue;
                    if (filter != null && !filter.Contains(k))
                        continue;

                    if (!result.TryGetValue(k, out var list))
                    {
                        list = new List<(int, int)>();
                        result[k] = list;
                    }
                    list.Add((x, y));
                }
            }

            return result;
        }
    }
}