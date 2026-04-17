using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace KingdomBorders
{
    /// <summary>
    /// Renders per-kingdom interior territory fills as a single GameEntity/Mesh
    /// per kingdom. Mirrors the material-flag conventions used by BorderRenderer
    /// so strips and fills interact predictably with water and depth.
    /// </summary>
    public class TerritoryFillRenderer
    {
        private readonly Scene _scene;
        private readonly Dictionary<Kingdom, GameEntity> _entities = new Dictionary<Kingdom, GameEntity>();

        // Shared materials — one per hideWater flag value, same pattern as BorderRenderer.
        private Material _matHideWater;
        private Material _matShowWater;

        public TerritoryFillRenderer(Scene scene)
        {
            _scene = scene;
        }

        public int EntityCount => _entities.Count;

        /// <summary>
        /// Builds or replaces the fill entity for a single kingdom.
        /// </summary>
        public void BuildKingdomFill(
            Kingdom kingdom,
            uint color,
            List<(int gx, int gy)> cells,
            System.Func<int, int, Vec2> cornerToWorld,
            float heightOffset,
            bool hideWater)
        {
            if (kingdom == null || cells == null || cells.Count == 0)
                return;

            // Replace any previous entity for this kingdom.
            ClearForKingdom(kingdom);

            Material mat = GetOrCreateMaterial(hideWater);
            if (mat == null)
                return;

            // Fill color uses the kingdom's banner color; alpha is applied at the
            // entity level each frame via SetAlpha, matching the border-strip flow.
            uint fillColor = color | 0xFF000000;

            Mesh mesh = Mesh.CreateMesh(editable: true);
            if (mesh == null)
            {
                ModLog.Log("FAIL: Mesh.CreateMesh returned null (fill)");
                return;
            }
            mesh.SetMaterial(mat);

            // Corner cache: avoids sampling the terrain 4x per shared grid vertex.
            var cornerCache = new Dictionary<long, (Vec3 pos, bool water)>(cells.Count * 2);

            UIntPtr lockHandle = mesh.LockEditDataWrite();
            int quadsEmitted = 0;

            try
            {
                var uv00 = new Vec2(0f, 0f);
                var uv10 = new Vec2(1f, 0f);
                var uv11 = new Vec2(1f, 1f);
                var uv01 = new Vec2(0f, 1f);

                for (int i = 0; i < cells.Count; i++)
                {
                    var (gx, gy) = cells[i];

                    var c00 = GetCorner(gx, gy, cornerCache, cornerToWorld, heightOffset, hideWater);
                    var c10 = GetCorner(gx + 1, gy, cornerCache, cornerToWorld, heightOffset, hideWater);
                    var c11 = GetCorner(gx + 1, gy + 1, cornerCache, cornerToWorld, heightOffset, hideWater);
                    var c01 = GetCorner(gx, gy + 1, cornerCache, cornerToWorld, heightOffset, hideWater);

                    // When hiding water: if every corner of the cell is water, skip the
                    // quad entirely — cells partially over water still render (matches
                    // how the border strips behave near coastlines).
                    if (hideWater && c00.water && c10.water && c11.water && c01.water)
                        continue;

                    // Front faces (CCW from +Z, i.e. from the campaign-map camera).
                    mesh.AddTriangle(c00.pos, c10.pos, c11.pos, uv00, uv10, uv11, fillColor, lockHandle);
                    mesh.AddTriangle(c00.pos, c11.pos, c01.pos, uv00, uv11, uv01, fillColor, lockHandle);

                    // Back faces — matches the BorderRenderer pattern so the mesh is
                    // visible regardless of culling when the camera tilts.
                    mesh.AddTriangle(c11.pos, c10.pos, c00.pos, uv11, uv10, uv00, fillColor, lockHandle);
                    mesh.AddTriangle(c01.pos, c11.pos, c00.pos, uv01, uv11, uv00, fillColor, lockHandle);

                    quadsEmitted++;
                }
            }
            finally
            {
                mesh.UnlockEditDataWrite(lockHandle);
            }

            if (quadsEmitted == 0)
                return;

            mesh.ComputeNormals();
            mesh.RecomputeBoundingBox();

            GameEntity entity = GameEntity.CreateEmpty(_scene, isModifiableFromEditor: false);
            if (entity == null)
            {
                ModLog.Log("FAIL: Entity creation returned null (fill)");
                return;
            }

            MatrixFrame identityFrame = MatrixFrame.Identity;
            entity.SetGlobalFrame(in identityFrame);
            entity.AddMesh(mesh);
            entity.SetVisibilityExcludeParents(true);
            entity.SetReadyToRender(true);

            _entities[kingdom] = entity;
        }

        /// <summary>
        /// Updates the alpha of all kingdom fill entities based on camera distance.
        /// Uses the same fade window as the border strips but a caller-supplied
        /// maximum alpha so fills stay subtler than the borders.
        /// </summary>
        public void UpdateAlphaForCameraDistance(float cameraDistance, float maxAlpha)
        {
            float minVisible = MCMSettings.Instance?.FadeStartHeight ?? 40;
            float maxVisible = MCMSettings.Instance?.FullOpacityHeight ?? 200;

            if (maxVisible <= minVisible)
                maxVisible = minVisible + 1f;

            float alpha;
            if (cameraDistance <= minVisible)
            {
                alpha = 0f;
            }
            else if (cameraDistance >= maxVisible)
            {
                alpha = maxAlpha;
            }
            else
            {
                float t = (cameraDistance - minVisible) / (maxVisible - minVisible);
                alpha = t * maxAlpha;
            }

            foreach (var kv in _entities)
            {
                kv.Value?.SetAlpha(alpha);
            }
        }

        public void ClearForKingdom(Kingdom kingdom)
        {
            if (kingdom == null)
                return;

            if (_entities.TryGetValue(kingdom, out var entity))
            {
                entity?.Remove(0);
                _entities.Remove(kingdom);
            }
        }

        public void ClearForKingdoms(HashSet<Kingdom> kingdoms)
        {
            if (kingdoms == null || kingdoms.Count == 0)
                return;

            var toRemove = new List<Kingdom>();
            foreach (var kv in _entities)
            {
                if (kingdoms.Contains(kv.Key))
                {
                    kv.Value?.Remove(0);
                    toRemove.Add(kv.Key);
                }
            }

            for (int i = 0; i < toRemove.Count; i++)
                _entities.Remove(toRemove[i]);
        }

        public void ClearAll()
        {
            foreach (var kv in _entities)
                kv.Value?.Remove(0);
            _entities.Clear();
        }

        // === Internal helpers ===

        private (Vec3 pos, bool water) GetCorner(
            int gx, int gy,
            Dictionary<long, (Vec3 pos, bool water)> cache,
            System.Func<int, int, Vec2> cornerToWorld,
            float heightOffset,
            bool hideWater)
        {
            long key = ((long)gx << 32) | (uint)gy;
            if (cache.TryGetValue(key, out var cached))
                return cached;

            Vec2 w = cornerToWorld(gx, gy);
            var mapSceneWrapper = Campaign.Current.MapSceneWrapper;

            float height = 0f;
            var campaignPos = new CampaignVec2(w, false);
            mapSceneWrapper.GetHeightAtPoint(in campaignPos, ref height);

            bool water = IsWaterPoint(w);

            // When borders are allowed over water, raise fill to the water surface so
            // the fill doesn't disappear under the sea. Matches BorderRenderer behavior.
            if (!hideWater && water)
            {
                float waterLevel = _scene.GetWaterLevelAtPosition(w, false, true);
                if (waterLevel > height)
                    height = waterLevel;
            }

            var result = (new Vec3(w.x, w.y, height + heightOffset), water);
            cache[key] = result;
            return result;
        }

        private bool IsWaterPoint(Vec2 p)
        {
            var mapSceneWrapper = Campaign.Current.MapSceneWrapper;

            var landPos = new CampaignVec2(p, true);
            PathFaceRecord landFace = mapSceneWrapper.GetFaceIndex(in landPos);

            if (landFace.IsValid())
            {
                TerrainType terrain = mapSceneWrapper.GetFaceTerrainType(landFace);
                return terrain == TerrainType.River ||
                       terrain == TerrainType.Lake ||
                       terrain == TerrainType.Water;
            }

            var seaPos = new CampaignVec2(p, false);
            PathFaceRecord seaFace = mapSceneWrapper.GetFaceIndex(in seaPos);

            if (seaFace.IsValid())
            {
                TerrainType seaTerrain = (TerrainType)seaFace.FaceGroupIndex;
                return seaTerrain == TerrainType.Water ||
                       seaTerrain == TerrainType.Lake ||
                       seaTerrain == TerrainType.CoastalSea ||
                       seaTerrain == TerrainType.OpenSea;
            }

            return false;
        }

        private Material GetOrCreateMaterial(bool hideWater)
        {
            if (hideWater)
            {
                if (_matHideWater == null)
                    _matHideWater = BuildMaterial(hideWater: true);
                return _matHideWater;
            }
            else
            {
                if (_matShowWater == null)
                    _matShowWater = BuildMaterial(hideWater: false);
                return _matShowWater;
            }
        }

        private static Material BuildMaterial(bool hideWater)
        {
            Material mat = Material.GetFromResource("vertex_color_mat");
            if (mat == null)
            {
                ModLog.Log("FAIL: vertex_color_mat not found (fill)");
                return null;
            }

            Material fillMat = mat.CreateCopy();
            fillMat.Flags |= MaterialFlags.NoModifyDepthBuffer;

            if (hideWater)
                fillMat.Flags |= MaterialFlags.NoDepthTest;
            else
                fillMat.Flags |= MaterialFlags.AlwaysDepthTest;

            return fillMat;
        }
    }
}