using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace KingdomBorders
{
    /// <summary>
    /// Tracks a rendered border entity so it can be selectively removed per-kingdom.
    /// </summary>
    public class BorderEntityEntry
    {
        public GameEntity Entity;
        public Kingdom Kingdom;
    }

    /// <summary>
    /// Collects strip geometry for a kingdom before creating the final mesh.
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

        public BorderRenderer(Scene scene)
        {
            _scene = scene;
        }

        /// <summary>
        /// Creates a single mesh entity containing all strips for a kingdom.
        /// Because all strips share the same color, overlapping at junctions is invisible.
        /// </summary>
        public GameEntity RenderKingdomStrips(KingdomMeshBuilder builder, float heightOffset)
        {
            if (builder.Strips.Count == 0)
                return null;

            Material mat = Material.GetFromResource("vertex_color_mat");
            if (mat == null)
            {
                ModLog.Log("FAIL: vertex_color_mat not found");
                return null;
            }

            bool showOnWater = MCMSettings.Instance?.ShowBordersOnWater ?? false;
            bool hideWater = !showOnWater;

            // Create a copy so we don't modify the shared resource material.
            Material borderMat = mat.CreateCopy();

            if (hideWater)
            {
                // Default mode: borders hidden on water.
                // - NoModifyDepthBuffer: prevents alpha bleed-through that makes
                //   the water surface transparent where borders overlap.
                // - NoDepthTest: renders borders without depth testing, which fixes
                //   minor clipping with trees and mountain geometry.
                borderMat.Flags |= MaterialFlags.NoModifyDepthBuffer | MaterialFlags.NoDepthTest;
            }
            // Experimental show-on-water mode: no flag changes.
            // Borders render on top of water but alpha bleed-through is present.

            Mesh mesh = Mesh.CreateMesh(editable: true);
            if (mesh == null)
            {
                ModLog.Log("FAIL: Mesh.CreateMesh returned null");
                return null;
            }
            mesh.SetMaterial(borderMat);

            UIntPtr lockHandle = mesh.LockEditDataWrite();
            int quadCount = 0;

            try
            {
                foreach (var (inner2D, outer2D) in builder.Strips)
                {
                    var (innerPoints, innerSkip) = SampleTerrainHeightsWithWater(inner2D, heightOffset, hideWater);
                    var (outerPoints, outerSkip) = SampleTerrainHeightsWithWater(outer2D, heightOffset, hideWater);

                    int count = Math.Min(innerPoints.Count, outerPoints.Count);
                    if (count < 2)
                        continue;

                    for (int i = 0; i < count - 1; i++)
                    {
                        // Skip quad if all four corners should be hidden
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
                        mesh.AddTriangle(innerA, outerA, outerB, uv0, uv1, uv2, builder.Color, lockHandle);
                        mesh.AddTriangle(innerA, outerB, innerB, uv0, uv2, uv3, builder.Color, lockHandle);

                        // Back faces
                        mesh.AddTriangle(outerB, outerA, innerA, uv2, uv1, uv0, builder.Color, lockHandle);
                        mesh.AddTriangle(innerB, outerB, innerA, uv3, uv2, uv0, builder.Color, lockHandle);

                        quadCount++;
                    }
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
                Kingdom = builder.Kingdom
            });

            return entity;
        }

        /// <summary>
        /// Updates the alpha of all border entities based on camera distance.
        /// Zoomed out (far) = more visible, zoomed in (close) = more transparent.
        /// </summary>
        public void UpdateAlphaForCameraDistance(float cameraDistance)
        {
            float minVisible = MCMSettings.Instance?.FadeStartHeight ?? 40;
            float maxVisible = MCMSettings.Instance?.FullOpacityHeight ?? 200;
            const float maxAlpha = 0.7f;

            // Ensure maxVisible is always above minVisible
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

            foreach (var entry in _entries)
            {
                if (entry.Entity != null)
                {
                    entry.Entity.SetAlpha(alpha);
                }
            }
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

        /// <summary>
        /// Removes all rendered border entities from the scene.
        /// </summary>
        public void ClearAll()
        {
            foreach (var entry in _entries)
            {
                entry.Entity?.Remove(0);
            }
            _entries.Clear();
        }

        public int EntityCount => _entries.Count;

        /// <summary>
        /// Returns true if the given point is over any water surface
        /// (sea, lake, river, or coastal water).
        /// </summary>
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

            // Land face invalid — check sea-region face for water terrain types.
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

        /// <summary>
        /// Samples terrain heights and determines which points should be hidden
        /// based on the water hiding setting.
        /// </summary>
        private (List<Vec3> points, List<bool> shouldHide) SampleTerrainHeightsWithWater(
            List<Vec2> points2D, float heightOffset, bool hideWater)
        {
            var points3D = new List<Vec3>(points2D.Count);
            var hideFlags = new List<bool>(points2D.Count);

            var mapSceneWrapper = Campaign.Current.MapSceneWrapper;

            // If hiding is off, skip water queries entirely
            if (!hideWater)
            {
                foreach (var p in points2D)
                {
                    float height = 0f;
                    var campaignPos = new CampaignVec2(p, false);
                    mapSceneWrapper.GetHeightAtPoint(in campaignPos, ref height);
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