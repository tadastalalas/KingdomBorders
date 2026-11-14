using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace KingdomBorders
{
    public class BorderEntityEntry
    {
        public GameEntity Entity;
        public Kingdom Kingdom;

        public long IdentityKey;

        // Set while queued in a chunked clear pass. Lets ContinueClearing() purge everything
        // that's actually been removed from the scene in one sweep once the queue drains.
        internal bool PendingRemoval;
        
        internal Kingdom Other;
        internal bool IsWar;
        internal bool IsHalo;
        internal CampaignTime GlowUntil;
        
        internal float LastAlpha = -1f;
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
        public List<(List<Vec2> inner, List<Vec2> outer, Kingdom other)> Strips = [];
    }
    
    internal enum StripFlushResult { Skipped, InProgress, Created, Failed }

    public class BorderRenderer
    {
        private readonly Scene _scene;
        private readonly List<BorderEntityEntry> _entries = new List<BorderEntityEntry>();
        private List<BorderEntityEntry> _pendingRemoval;
        private int _removalIndex;

        // Cached materials — one per hideWater flag value. The flag is stable across
        // a build (MCM changes trigger a full rebuild) and the material itself is
        // engine-owned, so we reuse the two instances for every strip of every kingdom.
        private Material _matHideWater;
        private Material _matShowWater;
        
        private float _time;
        // Set by the behavior before a partial flush; new strips of these kingdoms get a 1-day halo.
        internal HashSet<Kingdom> GlowKingdoms;
        
        // Resumable flush state for the strip currently being built (see ContinueStrip).
        private readonly List<Vec3> _jobInner3D = [];
        private readonly List<Vec3> _jobOuter3D = [];
        private readonly List<bool> _jobInnerSkip = [];
        private readonly List<bool> _jobOuterSkip = [];
        private List<Vec3> _jobOuterDraw;   // _jobOuter3D, or a widened copy for the player kingdom
        private List<Vec3> _jobHalo3D;      // widened outer edge for the glow halo; null when not glowing
        private int _jobCursor;             // next vertex pair to sample
        private int _jobQuad;               // next quad (point index) to emit
        private float _jobDist;             // dash distance along the inner edge up to _jobQuad
        private int _jobCreated;            // chunk entities created for this strip
        private long _jobKey;
        private bool _jobActive;
        private bool _jobPrepared;

        // Mesh.AddTriangle cost grows quadratically with a mesh's triangle count (measured: 231 quads
        // = 12 ms, 1042 quads = 250 ms). Capping quads per mesh makes the total linear; every chunk is
        // its own entity sharing the strip's identity key.
        private const int MaxQuadsPerMesh = 64;
        private const double SlowChunkLogMs = 5.0;

        // Native-time split, reset/logged by the behavior's PERF output.
        internal double DiagHeightMs, DiagWaterMs, DiagMeshMs;
        

        public BorderRenderer(Scene scene)
        {
            _scene = scene;
        }
        
        /// <summary>
        /// Resumable flush of one strip: samples terrain (vertex budget), then emits chunk meshes of
        /// ≤ MaxQuadsPerMesh quads (quad budget). Returns InProgress whenever a budget runs out — call
        /// again next tick with the same builder/stripIndex. Strips whose content key is in skipKeys
        /// are unchanged and still in the scene, so they cost nothing.
        /// </summary>
        internal StripFlushResult ContinueStrip(KingdomMeshBuilder builder, int stripIndex, float heightOffset,
            HashSet<long> skipKeys, ref int vertexBudget, ref int quadBudget)
        {
            (List<Vec2> inner2D, List<Vec2> outer2D, Kingdom other) = builder.Strips[stripIndex];
            int count = Math.Min(inner2D.Count, outer2D.Count);

            if (!_jobActive)
            {
                long key = ComputeIdentityKey(builder.Kingdom, other, inner2D, outer2D);
                if (skipKeys != null && skipKeys.Contains(key))
                    return StripFlushResult.Skipped;
                if (count < 2)
                    return StripFlushResult.Failed;

                _jobKey = key;
                _jobCursor = 0;
                _jobQuad = 0;
                _jobDist = 0f;
                _jobCreated = 0;
                _jobOuterDraw = null;
                _jobHalo3D = null;
                _jobInner3D.Clear(); _jobOuter3D.Clear();
                _jobInnerSkip.Clear(); _jobOuterSkip.Clear();
                _jobPrepared = false;
                _jobActive = true;
            }

            bool hideWater = !(MCMSettings.Instance?.ShowBordersOnWater ?? false);

            // Phase 1: terrain sampling (~3 µs per vertex).
            while (_jobCursor < count && vertexBudget > 0)
            {
                SampleVertex(inner2D[_jobCursor], heightOffset, hideWater, _jobInner3D, _jobInnerSkip);
                SampleVertex(outer2D[_jobCursor], heightOffset, hideWater, _jobOuter3D, _jobOuterSkip);
                _jobCursor++;
                vertexBudget -= 2;
            }
            if (_jobCursor < count)
                return StripFlushResult.InProgress;

            Material mat = GetOrCreateMaterial(hideWater);
            if (mat == null)
            {
                _jobActive = false;
                return StripFlushResult.Failed;
            }

            // Phase 2: per-strip setup.
            MCMSettings s = MCMSettings.Instance;
            Kingdom playerKingdom = Clan.PlayerClan?.Kingdom;
            bool isPlayer = (s?.EmphasizePlayerKingdom ?? true) && playerKingdom != null && builder.Kingdom == playerKingdom;
            bool glow = (s?.HighlightRecentChanges ?? true) && GlowKingdoms != null && GlowKingdoms.Contains(builder.Kingdom);
            if (!_jobPrepared)
            {
                _jobOuterDraw = isPlayer ? Widen(_jobInner3D, _jobOuter3D, count, 1.6f) : _jobOuter3D;
                _jobHalo3D = glow ? Widen(_jobInner3D, _jobOuterDraw, count, 1.8f) : null;
                _jobPrepared = true;
            }
            uint color = isPlayer ? Lighten(builder.Color, 0.25f) : builder.Color;
            uint haloColor = _jobHalo3D != null ? Lighten(builder.Color, 0.6f) : 0u;
            int style = s?.BorderStyle?.SelectedIndex ?? 0;
            float width = s?.BorderWidth ?? 1.05f;

            // Phase 3: chunk meshes until the quad budget is spent.
            while (_jobQuad < count - 1 && quadBudget > 0)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int quads = BuildChunk(builder.Kingdom, other, color, mat, inner2D, count, style, width, haloColor);
                double ms = ElapsedMs(t0);
                DiagMeshMs += ms;
                if (ms > SlowChunkLogMs)
                    ModLog.Log($"  SLOW chunk ({builder.Kingdom?.Name}, {quads} quads, halo={haloColor != 0u}): {ms:F1}ms");
                quadBudget -= haloColor != 0u ? quads * 2 : quads;
            }
            if (_jobQuad < count - 1)
                return StripFlushResult.InProgress;

            _jobActive = false;
            return _jobCreated > 0 ? StripFlushResult.Created : StripFlushResult.Failed;
        }

        /// <summary>
        /// Emits up to MaxQuadsPerMesh visible quads starting at _jobQuad into one new mesh/entity
        /// (plus a halo entity when haloColor != 0). Advances _jobQuad/_jobDist. Returns quads emitted;
        /// 0 when the remaining range was entirely water-hidden/dashed away (no engine calls made).
        /// </summary>
        private int BuildChunk(Kingdom kingdom, Kingdom other, uint color, Material mat, List<Vec2> inner2D, int count,
            int style, float borderWidth, uint haloColor)
        {
            // 1 = dashed (3w on / 2w off), 2 = dotted (1w on / 1w off), measured along the inner edge.
            float dashOn = style == 1 ? borderWidth * 3f : borderWidth;
            float period = style == 1 ? borderWidth * 5f : borderWidth * 2f;

            Mesh mesh = null, halo = null;
            UIntPtr lockMesh = UIntPtr.Zero, lockHalo = UIntPtr.Zero;
            int emitted = 0;
            int i = _jobQuad;
            float dist = _jobDist;

            try
            {
                for (; i < count - 1 && emitted < MaxQuadsPerMesh; i++)
                {
                    float segLen = inner2D[i].Distance(inner2D[i + 1]);
                    float mid = dist + segLen * 0.5f;
                    dist += segLen;

                    if (style != 0 && mid % period >= dashOn)
                        continue;
                    if (_jobInnerSkip[i] && _jobOuterSkip[i] && _jobInnerSkip[i + 1] && _jobOuterSkip[i + 1])
                        continue;

                    if (mesh == null)
                    {
                        mesh = Mesh.CreateMesh(editable: true);
                        if (mesh == null)
                        {
                            ModLog.Log("FAIL: Mesh.CreateMesh returned null");
                            i = count - 1; // abort the strip so the caller can't spin on it
                            break;
                        }
                        mesh.SetMaterial(mat);
                        lockMesh = mesh.LockEditDataWrite();

                        if (haloColor != 0u)
                        {
                            halo = Mesh.CreateMesh(editable: true);
                            if (halo != null)
                            {
                                halo.SetMaterial(mat);
                                lockHalo = halo.LockEditDataWrite();
                            }
                        }
                    }

                    float uvY0 = (float)i / (count - 1);
                    float uvY1 = (float)(i + 1) / (count - 1);
                    AddQuad(mesh, lockMesh, _jobInner3D[i], _jobOuterDraw[i], _jobInner3D[i + 1], _jobOuterDraw[i + 1], uvY0, uvY1, color);
                    if (halo != null)
                        AddQuad(halo, lockHalo, _jobInner3D[i], _jobHalo3D[i], _jobInner3D[i + 1], _jobHalo3D[i + 1], uvY0, uvY1, haloColor);
                    emitted++;
                }
            }
            finally
            {
                if (mesh != null) mesh.UnlockEditDataWrite(lockMesh);
                if (halo != null) halo.UnlockEditDataWrite(lockHalo);
            }

            _jobQuad = i;
            _jobDist = dist;
            if (mesh == null || emitted == 0)
                return 0;

            mesh.ComputeNormals();
            mesh.RecomputeBoundingBox();
            GameEntity entity = CreateEntity(mesh);
            if (entity != null)
            {
                _entries.Add(new BorderEntityEntry
                {
                    Entity = entity, Kingdom = kingdom, Other = other, IsWar = IsAtWar(kingdom, other), IdentityKey = _jobKey
                });
                _jobCreated++;
            }

            if (halo != null)
            {
                halo.ComputeNormals();
                halo.RecomputeBoundingBox();
                GameEntity haloEntity = CreateEntity(halo);
                if (haloEntity != null)
                    _entries.Add(new BorderEntityEntry
                    {
                        Entity = haloEntity, Kingdom = kingdom, Other = other, IsHalo = true,
                        GlowUntil = CampaignTime.DaysFromNow(1f), IdentityKey = _jobKey
                    });
            }
            return emitted;
        }

        private static void AddQuad(Mesh mesh, UIntPtr lockHandle, Vec3 innerA, Vec3 outerA, Vec3 innerB, Vec3 outerB,
            float uvY0, float uvY1, uint color)
        {
            Vec2 uv0 = new Vec2(0f, uvY0), uv1 = new Vec2(1f, uvY0), uv2 = new Vec2(1f, uvY1), uv3 = new Vec2(0f, uvY1);
            mesh.AddTriangle(innerA, outerA, outerB, uv0, uv1, uv2, color, lockHandle);
            mesh.AddTriangle(innerA, outerB, innerB, uv0, uv2, uv3, color, lockHandle);
            mesh.AddTriangle(outerB, outerA, innerA, uv2, uv1, uv0, color, lockHandle);
            mesh.AddTriangle(innerB, outerB, innerA, uv3, uv2, uv0, color, lockHandle);
        }

        private static double ElapsedMs(long t0) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private GameEntity CreateEntity(Mesh mesh)
        {
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
            return entity;
        }

        private static List<Vec3> Widen(List<Vec3> inner, List<Vec3> outer, int count, float k)
        {
            List<Vec3> result = new List<Vec3>(count);
            for (int i = 0; i < count; i++)
                result.Add(inner[i] + (outer[i] - inner[i]) * k);
            return result;
        }

        private static uint Lighten(uint c, float t)
        {
            uint r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
            r += (uint)((255 - r) * t);
            g += (uint)((255 - g) * t);
            b += (uint)((255 - b) * t);
            return 0xFF000000 | (r << 16) | (g << 8) | b;
        }

        private static bool IsAtWar(Kingdom a, Kingdom b) =>
            a != null && b != null && a != b && (MCMSettings.Instance?.HighlightWarBorders ?? true) && FactionManager.IsAtWarAgainstFaction(a, b);

        public void RefreshWarFlags()
        {
            for (int i = 0; i < _entries.Count; i++)
                _entries[i].IsWar = IsAtWar(_entries[i].Kingdom, _entries[i].Other);
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

        internal static Material BuildMaterial(bool hideWater)
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
        /// Content hash of a strip: owner, neighbour and every quantized vertex of both edges. Two strips
        /// with equal keys are geometrically identical, so the existing entity can stay in the scene.
        /// </summary>
        internal static long ComputeIdentityKey(Kingdom kingdom, Kingdom other, List<Vec2> inner, List<Vec2> outer)
        {
            unchecked
            {
                const ulong prime = 1099511628211UL;
                ulong h = 14695981039346656037UL;
                h = (h ^ (uint)(kingdom?.StringId.GetHashCode() ?? 0)) * prime;
                h = (h ^ (uint)(other?.StringId.GetHashCode() ?? 0)) * prime;

                h = (h ^ (uint)inner.Count) * prime;
                for (int i = 0; i < inner.Count; i++)
                {
                    h = (h ^ (uint)Quantize(inner[i].x)) * prime;
                    h = (h ^ (uint)Quantize(inner[i].y)) * prime;
                }
                h = (h ^ (uint)outer.Count) * prime;
                for (int i = 0; i < outer.Count; i++)
                {
                    h = (h ^ (uint)Quantize(outer[i].x)) * prime;
                    h = (h ^ (uint)Quantize(outer[i].y)) * prime;
                }
                return (long)h;
            }
        }

        private static int Quantize(float v)
        {
            return (int)Math.Round(v * 100f);
        }

        public void UpdateAlphaForCameraDistance(float cameraDistance, bool isVisible, float dt)
        {
            _time += dt;
            float alpha = 0f;

            if (isVisible)
            {
                float minVisible = MCMSettings.Instance?.FadeStartHeight ?? 40;
                float maxVisible = MCMSettings.Instance?.FullOpacityHeight ?? 200;
                const float maxAlpha = 0.7f;
                if (maxVisible <= minVisible) maxVisible = minVisible + 1f;

                if (cameraDistance <= minVisible) alpha = 0f;
                else if (cameraDistance >= maxVisible) alpha = maxAlpha;
                else alpha = ((cameraDistance - minVisible) / (maxVisible - minVisible)) * maxAlpha;
            }

            Kingdom playerKingdom = Clan.PlayerClan?.Kingdom;
            float dimmed = playerKingdom != null && (MCMSettings.Instance?.EmphasizePlayerKingdom ?? true) ? alpha * 0.55f : alpha;
            float pulse = 0.65f + 0.35f * (float)Math.Sin(_time * 4f); // 0.3..1.0, ~1.6 s period

            for (int i = 0; i < _entries.Count; i++)
            {
                BorderEntityEntry entry = _entries[i];
                GameEntity entity = entry.Entity;
                if (entity == null) continue;

                if (entry.IsHalo)
                {
                    if (entry.GlowUntil.IsPast)
                    {
                        if (entry.PendingRemoval) { entity.SetAlpha(0f); continue; }
                        entity.Remove(0);
                        _entries.RemoveAt(i);
                        i--;
                        continue;
                    }
                    float haloAlpha = alpha * (float)entry.GlowUntil.RemainingDaysFromNow * pulse;
                    if (Math.Abs(haloAlpha - entry.LastAlpha) > 0.002f)
                    {
                        entity.SetAlpha(haloAlpha);
                        entry.LastAlpha = haloAlpha;
                    }
                    continue;
                }

                float a = entry.Kingdom == playerKingdom ? alpha : dimmed;
                if (entry.IsWar) a *= pulse;
                if (Math.Abs(a - entry.LastAlpha) > 0.002f)
                {
                    entity.SetAlpha(a);
                    entry.LastAlpha = a;
                }
            }
        }

        /// <summary>
        /// Diff cull for a partial rebuild. Old entries of the affected kingdoms whose content key still
        /// appears among the freshly generated strips are kept (keys returned so the flush skips them);
        /// everything else of those kingdoms is stale and queued for ContinueClearing().
        /// </summary>
        public HashSet<long> BeginClearDiff(HashSet<Kingdom> kingdoms, List<KingdomMeshBuilder> builders)
        {
            HashSet<long> newKeys = new HashSet<long>();
            for (int b = 0; b < builders.Count; b++)
            {
                KingdomMeshBuilder builder = builders[b];
                for (int i = 0; i < builder.Strips.Count; i++)
                {
                    (List<Vec2> inner, List<Vec2> outer, Kingdom other) = builder.Strips[i];
                    newKeys.Add(ComputeIdentityKey(builder.Kingdom, other, inner, outer));
                }
            }

            HashSet<long> preserved = new HashSet<long>();
            List<BorderEntityEntry> toRemove = new List<BorderEntityEntry>();
            for (int i = 0; i < _entries.Count; i++)
            {
                BorderEntityEntry entry = _entries[i];
                if (!kingdoms.Contains(entry.Kingdom))
                    continue;
                if (newKeys.Contains(entry.IdentityKey))
                    preserved.Add(entry.IdentityKey);
                else
                {
                    entry.PendingRemoval = true;
                    toRemove.Add(entry);
                }
            }

            ModLog.Log($"Diff cull: {toRemove.Count} stale strips queued for removal, {preserved.Count} unchanged kept, " +
                       $"~{newKeys.Count - preserved.Count} to create (kingdoms={kingdoms.Count})");

            _pendingRemoval = toRemove;
            _removalIndex = 0;
            return preserved;
        }

        /// <summary>
        /// Removes up to maxPerTick queued entities from the scene (the actual native
        /// GameEntity.Remove() call). Spreads that engine-interop cost across multiple ticks
        /// instead of paying for every removal in one frame. Returns true once fully drained.
        /// </summary>
        public bool ContinueClearing(int maxPerTick)
        {
            if (_pendingRemoval == null)
                return true;

            int end = Math.Min(_removalIndex + maxPerTick, _pendingRemoval.Count);
            for (int i = _removalIndex; i < end; i++)
            {
                _pendingRemoval[i].Entity?.Remove(0);
            }
            _removalIndex = end;

            if (_removalIndex < _pendingRemoval.Count)
                return false;

            _entries.RemoveAll(e => e.PendingRemoval);
            _pendingRemoval = null;
            _removalIndex = 0;
            return true;
        }

        public void ClearAll()
        {
            foreach (var entry in _entries) entry.Entity?.Remove(0);
            _entries.Clear();
            _pendingRemoval = null;
            _removalIndex = 0;
            _jobActive = false;
        }

        public int EntityCount => _entries.Count;

        internal static bool IsWaterPoint(Vec2 p)
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

        private void SampleVertex(Vec2 p, float heightOffset, bool hideWater, List<Vec3> points3D, List<bool> hideFlags)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            float height = 0f;
            CampaignVec2 campaignPos = new CampaignVec2(p, false);
            Campaign.Current.MapSceneWrapper.GetHeightAtPoint(in campaignPos, ref height);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            bool water = IsWaterPoint(p);
            DiagHeightMs += (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            DiagWaterMs += ElapsedMs(t1);

            if (hideWater)
            {
                points3D.Add(new Vec3(p.x, p.y, height + heightOffset));
                hideFlags.Add(water);
                return;
            }

            if (water)
            {
                float waterLevel = _scene.GetWaterLevelAtPosition(p, false, true);
                if (waterLevel > height) height = waterLevel;
            }
            points3D.Add(new Vec3(p.x, p.y, height + heightOffset));
            hideFlags.Add(false);
        }
    }
}