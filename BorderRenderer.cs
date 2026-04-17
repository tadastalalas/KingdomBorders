using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace KingdomBorders
{
    /// <summary>
    /// Tracks a rendered border entity so it can be selectively removed per-kingdom
    /// and matched against newly generated strips by identity for AABB-based diff rebuilding.
    /// </summary>
    public class BorderEntityEntry
    {
        public GameEntity Entity;
        public Kingdom Kingdom;

        // 2D world-space AABB of the strip (used by the diff logic).
        public float MinX;
        public float MinY;
        public float MaxX;
        public float MaxY;

        // Stable hash over kingdom + quantized endpoint positions so we can
        // match "same strip" across rebuilds and skip regeneration when the
        // geometry didn't actually change.
        public long IdentityKey;
    }

    /// <summary>
    /// Collects strip geometry for a kingdom before creating the final meshes.
    /// Each strip becomes its own GameEntity/Mesh so partial rebuilds can
    /// touch only the strips whose region actually changed.
    /// </summary>
    public class KingdomMeshBuilder
    {
        public Kingdom Kingdom;
        public uint Color;
        public List<(List<Vec2> inner, List<Vec2> outer)> Strips = new List<(List<Vec2>, List<Vec2>)>();
    }

    public class BorderRenderer
    {
        private readonly Scene _scene;
        private readonly List<BorderEntityEntry> _entries = new List<BorderEntityEntry>();

        // Cached materials — one per hideWater flag value. The flag is stable across
        // a build (MCM changes trigger a full rebuild) and the material itself is
        // engine-owned, so we reuse the two instances for every strip of every kingdom.
        private Material _matHideWater;
        private Material _matShowWater;

        public BorderRenderer(Scene scene)
        {
            _scene = scene;
        }

        /// <summary>
        /// Creates one mesh entity per strip in the builder and appends them to the entry list.
        /// Strips whose identity key is already present in <paramref name="skipKeys"/> are
        /// skipped because the equivalent entity is still alive in the scene (preserved by an
        /// earlier AABB cull). Returns the number of new entities actually created.
        /// </summary>
        public int RenderKingdomStrips(KingdomMeshBuilder builder, float heightOffset, HashSet<long> skipKeys = null)
        {
            if (builder.Strips.Count == 0)
                return 0;

            bool showOnWater = MCMSettings.Instance?.ShowBordersOnWater ?? false;
            bool hideWater = !showOnWater;

            Material mat = GetOrCreateMaterial(hideWater);
            if (mat == null)
                return 0;

            int created = 0;
            foreach (var (inner2D, outer2D) in builder.Strips)
            {
                if (CreateStripEntity(builder.Kingdom, builder.Color, mat, inner2D, outer2D, heightOffset, hideWater, skipKeys) != null)
                {
                    created++;
                }
            }

            return created;
        }

        /// <summary>
        /// Builds a single strip (one quad ribbon) as its own GameEntity,
        /// unless its identity key is in <paramref name="skipKeys"/> (preserved from before).
        /// </summary>
        private GameEntity CreateStripEntity(
            Kingdom kingdom,
            uint color,
            Material mat,
            List<Vec2> inner2D,
            List<Vec2> outer2D,
            float heightOffset,
            bool hideWater,
            HashSet<long> skipKeys)
        {
            // Identity-first: if this strip already exists as a preserved entity, skip
            // everything including terrain sampling. This is where the Step 2 win lives.
            long identityKey = ComputeIdentityKey(kingdom, inner2D, outer2D);
            if (skipKeys != null && skipKeys.Contains(identityKey))
            {
                return null;
            }

            var (innerPoints, innerSkip) = SampleTerrainHeightsWithWater(inner2D, heightOffset, hideWater);
            var (outerPoints, outerSkip) = SampleTerrainHeightsWithWater(outer2D, heightOffset, hideWater);

            int count = Math.Min(innerPoints.Count, outerPoints.Count);
            if (count < 2)
                return null;

            // Compute AABB from the input 2D polylines before touching the mesh lock
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            for (int i = 0; i < inner2D.Count; i++)
            {
                var p = inner2D[i];
                if (p.x < minX) minX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.x > maxX) maxX = p.x;
                if (p.y > maxY) maxY = p.y;
            }
            for (int i = 0; i < outer2D.Count; i++)
            {
                var p = outer2D[i];
                if (p.x < minX) minX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.x > maxX) maxX = p.x;
                if (p.y > maxY) maxY = p.y;
            }

            Mesh mesh = Mesh.CreateMesh(editable: true);
            if (mesh == null)
            {
                ModLog.Log("FAIL: Mesh.CreateMesh returned null");
                return null;
            }
            mesh.SetMaterial(mat);

            UIntPtr lockHandle = mesh.LockEditDataWrite();
            int quadCount = 0;

            try
            {
                for (int i = 0; i < count - 1; i++)
                {
                    if (innerSkip[i] && outerSkip[i] &&
                        innerSkip[i + 1] && outerSkip[i + 1])
                    {
                        continue;
                    }

                    Vec3 innerA = innerPoints[i];
                    Vec3 outerA = outerPoints[i];
                    Vec3 innerB = innerPoints[i + 1];
                    Vec3 outerB = outerPoints[i + 1];

                    float uvY0 = (float)i / (count - 1);
                    float uvY1 = (float)(i + 1) / (count - 1);

                    Vec2 uv0 = new Vec2(0f, uvY0);
                    Vec2 uv1 = new Vec2(1f, uvY0);
                    Vec2 uv2 = new Vec2(1f, uvY1);
                    Vec2 uv3 = new Vec2(0f, uvY1);

                    // Front faces
                    mesh.AddTriangle(innerA, outerA, outerB, uv0, uv1, uv2, color, lockHandle);
                    mesh.AddTriangle(innerA, outerB, innerB, uv0, uv2, uv3, color, lockHandle);

                    // Back faces
                    mesh.AddTriangle(outerB, outerA, innerA, uv2, uv1, uv0, color, lockHandle);
                    mesh.AddTriangle(innerB, outerB, innerA, uv3, uv2, uv0, color, lockHandle);

                    quadCount++;
                }
            }
            finally
            {
                mesh.UnlockEditDataWrite(lockHandle);
            }

            if (quadCount == 0)
                return null;

            mesh.ComputeNormals();
            mesh.RecomputeBoundingBox();

            GameEntity entity = GameEntity.CreateEmpty(_scene, isModifiableFromEditor: false);
            if (entity == null)
            {
                ModLog.Log("FAIL: Entity creation returned null");
                return null;
            }

            MatrixFrame identityFrame = MatrixFrame.Identity;
            entity.SetGlobalFrame(in identityFrame);
            entity.AddMesh(mesh);
            entity.SetVisibilityExcludeParents(true);
            entity.SetReadyToRender(true);

            _entries.Add(new BorderEntityEntry
            {
                Entity = entity,
                Kingdom = kingdom,
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY,
                IdentityKey = identityKey
            });

            return entity;
        }

        /// <summary>
        /// Returns the cached shared material for the given water-hiding flag,
        /// creating it on first use.
        /// </summary>
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
                ModLog.Log("FAIL: vertex_color_mat not found");
                return null;
            }

            Material borderMat = mat.CreateCopy();
            borderMat.Flags |= MaterialFlags.NoModifyDepthBuffer;

            if (hideWater)
            {
                borderMat.Flags |= MaterialFlags.NoDepthTest;
            }
            else
            {
                borderMat.Flags |= MaterialFlags.AlwaysDepthTest;
            }

            return borderMat;
        }

        /// <summary>
        /// Hashes kingdom identity together with quantized polyline endpoints so a rebuilt
        /// strip whose endpoints haven't moved produces the same key as its previous incarnation.
        /// </summary>
        private static long ComputeIdentityKey(Kingdom kingdom, List<Vec2> inner, List<Vec2> outer)
        {
            unchecked
            {
                const ulong fnvOffset = 14695981039346656037UL;
                const ulong fnvPrime = 1099511628211UL;
                ulong h = fnvOffset;

                int kid = kingdom != null ? kingdom.StringId.GetHashCode() : 0;
                h = (h ^ (uint)kid) * fnvPrime;

                if (inner.Count > 0)
                {
                    int last = inner.Count - 1;
                    h = (h ^ (uint)Quantize(inner[0].x)) * fnvPrime;
                    h = (h ^ (uint)Quantize(inner[0].y)) * fnvPrime;
                    h = (h ^ (uint)Quantize(inner[last].x)) * fnvPrime;
                    h = (h ^ (uint)Quantize(inner[last].y)) * fnvPrime;
                }
                if (outer.Count > 0)
                {
                    int last = outer.Count - 1;
                    h = (h ^ (uint)Quantize(outer[0].x)) * fnvPrime;
                    h = (h ^ (uint)Quantize(outer[0].y)) * fnvPrime;
                    h = (h ^ (uint)Quantize(outer[last].x)) * fnvPrime;
                    h = (h ^ (uint)Quantize(outer[last].y)) * fnvPrime;
                }

                return (long)h;
            }
        }

        private static int Quantize(float v)
        {
            return (int)Math.Round(v * 100f);
        }

        /// <summary>
        /// Updates the alpha of all border entities based on camera distance.
        /// </summary>
        public void UpdateAlphaForCameraDistance(float cameraDistance)
        {
            float minVisible = MCMSettings.Instance?.FadeStartHeight ?? 40;
            float maxVisible = MCMSettings.Instance?.FullOpacityHeight ?? 200;
            const float maxAlpha = 0.7f;

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

            for (int i = 0; i < _entries.Count; i++)
            {
                var entity = _entries[i].Entity;
                if (entity != null)
                    entity.SetAlpha(alpha);
            }
        }

        /// <summary>
        /// Removes all entries belonging to any of the specified kingdoms whose strip AABB
        /// intersects the given world-space bounding box. Entries OUTSIDE the box are left
        /// in the scene and their identity keys are returned so the caller can skip
        /// re-rendering the equivalent new strips.
        /// </summary>
        public HashSet<long> ClearForKingdomsInBounds(HashSet<Kingdom> kingdoms,
            float minX, float minY, float maxX, float maxY)
        {
            var preservedKeys = new HashSet<long>();
            int removed = 0;

            _entries.RemoveAll(entry =>
            {
                if (!kingdoms.Contains(entry.Kingdom))
                    return false;

                bool disjoint = entry.MaxX < minX || entry.MinX > maxX ||
                                entry.MaxY < minY || entry.MinY > maxY;

                if (!disjoint)
                {
                    entry.Entity?.Remove(0);
                    removed++;
                    return true;
                }

                preservedKeys.Add(entry.IdentityKey);
                return false;
            });

            ModLog.Log($"AABB cull: removed {removed} intersecting, preserved {preservedKeys.Count} outside " +
                       $"(kingdoms={kingdoms.Count}, box=({minX:F1},{minY:F1})-({maxX:F1},{maxY:F1}))");

            return preservedKeys;
        }

        /// <summary>
        /// Removes all entries that involve any of the specified kingdoms.
        /// </summary>
        public void ClearForKingdoms(HashSet<Kingdom> kingdoms)
        {
            int removed = _entries.RemoveAll(entry =>
            {
                if (kingdoms.Contains(entry.Kingdom))
                {
                    entry.Entity?.Remove(0);
                    return true;
                }
                return false;
            });

            ModLog.Log($"Cleared {removed} border entities for {kingdoms.Count} kingdoms");
        }

        public void ClearAll()
        {
            foreach (var entry in _entries)
            {
                entry.Entity?.Remove(0);
            }
            _entries.Clear();
        }

        public int EntityCount => _entries.Count;

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

        private (List<Vec3> points, List<bool> shouldHide) SampleTerrainHeightsWithWater(
            List<Vec2> points2D, float heightOffset, bool hideWater)
        {
            var points3D = new List<Vec3>(points2D.Count);
            var hideFlags = new List<bool>(points2D.Count);

            var mapSceneWrapper = Campaign.Current.MapSceneWrapper;

            if (!hideWater)
            {
                foreach (var p in points2D)
                {
                    float height = 0f;
                    var campaignPos = new CampaignVec2(p, false);
                    mapSceneWrapper.GetHeightAtPoint(in campaignPos, ref height);

                    if (IsWaterPoint(p))
                    {
                        float waterLevel = _scene.GetWaterLevelAtPosition(p, false, true);
                        if (waterLevel > height)
                        {
                            height = waterLevel;
                        }
                    }

                    points3D.Add(new Vec3(p.x, p.y, height + heightOffset));
                    hideFlags.Add(false);
                }
                return (points3D, hideFlags);
            }

            foreach (var p in points2D)
            {
                float height = 0f;
                var campaignPos = new CampaignVec2(p, false);
                mapSceneWrapper.GetHeightAtPoint(in campaignPos, ref height);
                points3D.Add(new Vec3(p.x, p.y, height + heightOffset));
                hideFlags.Add(IsWaterPoint(p));
            }

            return (points3D, hideFlags);
        }
    }
}