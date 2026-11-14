using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace KingdomBorders
{
    /// <summary>
    /// Terrain-draped kingdom tint. Corner heights/water flags are cached once per session.
    /// Each rebuild re-snapshots cell colours, diffs them row-by-row against the previous build and
    /// rebuilds only the chunks whose colours changed; untouched chunks keep their entity.
    /// </summary>
    public sealed class TerritoryTintRenderer
    {
        // One cell row per chunk: ~6 ms of native mesh calls per chunk measured at res 150.
        private const int ChunkRows = 1;

        private readonly Scene _scene;
        private BorderCalculator _calc;
        private Material _mat;
        private int _res;
        private int _chunkCount;

        // Session cache (terrain never changes). Rebuilt only when grid resolution changes.
        private int _cachedRes = -1;
        private float[] _cornerHeights;   // (res+1)^2
        private bool[] _cellWater;        // res^2
        private int[] _cornerIds;         // (ChunkRows+1)*(res+1) scratch
        private int _scanRow;

        // Per-build
        private uint[] _cellColor;        // res^2, 0 = not tinted
        private uint[] _cellColorPrev;    // previous build, for the row diff
        private bool[] _chunkDirty;
        private GameEntity[] _chunkEntities;
        private bool _forceAll;           // no valid entities (first build / after ClearAll): rebuild everything
        private bool _colorsReady;
        private int _chunkIndex;
        private int _builtThisPass;
        private float _lastAlpha = -1f;

        public TerritoryTintRenderer(Scene scene) => _scene = scene;

        public void Begin(BorderCalculator calc)
        {
            _calc = calc;
            _res = calc.GridResolution;
            _chunkCount = (_res + ChunkRows - 1) / ChunkRows;
            _colorsReady = false;
            _chunkIndex = 0;
            _builtThisPass = 0;

            if (_cachedRes != _res)
            {
                RemoveAllEntities();
                _cachedRes = _res;
                _cornerHeights = new float[(_res + 1) * (_res + 1)];
                _cellWater = new bool[_res * _res];
                _cornerIds = new int[(ChunkRows + 1) * (_res + 1)];
                _cellColor = new uint[_res * _res];
                _cellColorPrev = new uint[_res * _res];
                _chunkDirty = new bool[_chunkCount];
                _chunkEntities = new GameEntity[_chunkCount];
                _forceAll = true;
                _scanRow = 0;
            }
            else
            {
                _scanRow = _res + 1;
            }
        }

        /// <summary>Returns true when finished.</summary>
        public bool Continue(int rowsPerTick)
        {
            if (_calc == null) return true;

            if (_scanRow <= _res)
            {
                int end = Math.Min(_scanRow + rowsPerTick, _res + 1);
                for (; _scanRow < end; _scanRow++) ScanRow(_scanRow);
                return false;
            }

            if (!_colorsReady)
            {
                FillCellColorsAndMarkDirty();
                _colorsReady = true;
                return false;
            }

            // One dirty chunk per tick; clean chunks keep their entity.
            while (_chunkIndex < _chunkCount)
            {
                int ci = _chunkIndex++;
                if (!_chunkDirty[ci]) continue;
                _chunkDirty[ci] = false;
                BuildChunk(ci);
                _builtThisPass++;
                return false;
            }

            ModLog.Log($"Tint: rebuilt {_builtThisPass} of {_chunkCount} chunks");
            _forceAll = false;
            _calc = null;
            return true;
        }

        // --- Phase 1: session cache ---

        private void ScanRow(int y)
        {
            Vec2 half = _calc.CellSize * 0.5f;
            int stride = _res + 1;

            if (y < _res)
            {
                for (int x = 0; x < _res; x++)
                    _cellWater[y * _res + x] = BorderRenderer.IsWaterPoint(_calc.GridToWorld(x, y));
            }

            for (int cx = 0; cx <= _res; cx++)
            {
                Vec2 p = _calc.GridToWorld(cx, y) - half;
                float h = 0f;
                CampaignVec2 cp = new CampaignVec2(p, false);
                Campaign.Current.MapSceneWrapper.GetHeightAtPoint(in cp, ref h);

                if (CornerTouchesWater(cx, y))
                {
                    float w = _scene.GetWaterLevelAtPosition(p, false, true);
                    if (w > h) h = w;
                }

                _cornerHeights[y * stride + cx] = h;
            }
        }

        private bool CornerTouchesWater(int cx, int cy) =>
            IsWater(cx - 1, cy - 1) || IsWater(cx, cy - 1) || IsWater(cx - 1, cy) || IsWater(cx, cy);

        private bool IsWater(int x, int y) =>
            x >= 0 && y >= 0 && x < _res && y < _res && _cellWater[y * _res + x];

        // --- Phase 2: per-build ---

        private void FillCellColorsAndMarkDirty()
        {
            uint[] swap = _cellColorPrev;
            _cellColorPrev = _cellColor;
            _cellColor = swap;

            for (int y = 0; y < _res; y++)
            {
                bool rowChanged = _forceAll;
                for (int x = 0; x < _res; x++)
                {
                    Kingdom k = _calc.GetCellKingdom(x, y);
                    uint c = k == null ? 0u : BorderCalculator.GetKingdomColor(k);
                    int i = y * _res + x;
                    _cellColor[i] = c;
                    if (c != _cellColorPrev[i]) rowChanged = true;
                }
                if (rowChanged) MarkRowDirty(y);
            }

            for (int ci = 0; ci < _chunkCount; ci++)
            {
                if (!_chunkDirty[ci]) continue;
                _chunkEntities[ci]?.Remove(0);
                _chunkEntities[ci] = null;
            }
        }

        // Chunk ci spans cell rows [ci*R, (ci+1)*R) and its corners blend cell rows ci*R-1 .. (ci+1)*R,
        // so a changed cell row y dirties every chunk with ci*R-1 <= y <= (ci+1)*R.
        private void MarkRowDirty(int y)
        {
            int lo = Math.Max(0, (y + ChunkRows - 1) / ChunkRows - 1);
            int hi = Math.Min(_chunkCount - 1, (y + 1) / ChunkRows);
            for (int ci = lo; ci <= hi; ci++) _chunkDirty[ci] = true;
        }

        private uint CornerColor(int cx, int cy)
        {
            uint r = 0, g = 0, b = 0, n = 0;
            for (int dy = -1; dy <= 0; dy++)
            {
                for (int dx = -1; dx <= 0; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= _res || y >= _res) continue;
                    uint c = _cellColor[y * _res + x];
                    if (c == 0) continue;
                    r += (c >> 16) & 0xFF;
                    g += (c >> 8) & 0xFF;
                    b += c & 0xFF;
                    n++;
                }
            }
            return n == 0 ? 0u : 0xFF000000 | ((r / n) << 16) | ((g / n) << 8) | (b / n);
        }

        private bool ChunkHasTint(int y0, int y1)
        {
            for (int i = y0 * _res; i < y1 * _res; i++)
                if (_cellColor[i] != 0) return true;
            return false;
        }

        private void BuildChunk(int ci)
        {
            int y0 = ci * ChunkRows;
            int y1 = Math.Min(y0 + ChunkRows, _res);
            if (!ChunkHasTint(y0, y1)) return;

            _mat ??= BorderRenderer.BuildMaterial(hideWater: true);
            if (_mat == null) return;

            Mesh mesh = Mesh.CreateMesh(editable: true);
            if (mesh == null) return;
            mesh.SetMaterial(_mat);

            float offset = MCMSettings.Instance?.HeightOffset ?? 0.55f;
            int stride = _res + 1;
            Vec2 half = _calc.CellSize * 0.5f;
            Vec2 uv = Vec2.Zero;
            int[] ids = _cornerIds;

            UIntPtr h = mesh.LockEditDataWrite();
            try
            {
                for (int cy = y0; cy <= y1; cy++)
                {
                    for (int cx = 0; cx <= _res; cx++)
                    {
                        int local = (cy - y0) * stride + cx;
                        uint c = CornerColor(cx, cy);
                        if (c == 0) { ids[local] = -1; continue; }

                        Vec2 p = _calc.GridToWorld(cx, cy) - half;
                        Vec3 v = new Vec3(p.x, p.y, _cornerHeights[cy * stride + cx] + offset);
                        ids[local] = mesh.AddFaceCorner(v, Vec3.Up, uv, c, h);
                    }
                }

                for (int y = y0; y < y1; y++)
                {
                    int ly = y - y0;
                    for (int x = 0; x < _res; x++)
                    {
                        if (_cellColor[y * _res + x] == 0) continue;
                        int a = ids[ly * stride + x];
                        int b = ids[ly * stride + x + 1];
                        int c = ids[(ly + 1) * stride + x + 1];
                        int d = ids[(ly + 1) * stride + x];
                        mesh.AddFace(a, b, c, h);
                        mesh.AddFace(a, c, d, h);
                        mesh.AddFace(c, b, a, h);
                        mesh.AddFace(d, c, a, h);
                    }
                }
            }
            finally
            {
                mesh.UnlockEditDataWrite(h);
            }

            mesh.RecomputeBoundingBox();

            GameEntity e = GameEntity.CreateEmpty(_scene, isModifiableFromEditor: false);
            if (e == null) return;
            MatrixFrame f = MatrixFrame.Identity;
            e.SetGlobalFrame(in f);
            e.AddMesh(mesh);
            e.SetVisibilityExcludeParents(true);
            e.SetReadyToRender(true);
            e.SetAlpha(0f);
            _chunkEntities[ci] = e;
            _lastAlpha = -1f; // new chunk starts at alpha 0: re-apply to all next frame
        }

        // --- Runtime ---

        public void UpdateAlphaForCameraDistance(float cameraHeight, bool visible)
        {
            if (_chunkEntities == null) return;
            float alpha = 0f;
            if (visible)
            {
                float start = MCMSettings.Instance?.FadeStartHeight ?? 30f;
                float full = Math.Max(MCMSettings.Instance?.FullOpacityHeight ?? 300f, start + 1f);
                alpha = MBMath.ClampFloat((cameraHeight - start) / (full - start), 0f, 1f) * (MCMSettings.Instance?.TintOpacity ?? 0.15f);
            }
            if (alpha == _lastAlpha) return;
            _lastAlpha = alpha;
            for (int i = 0; i < _chunkEntities.Length; i++) _chunkEntities[i]?.SetAlpha(alpha);
        }

        public void ClearAll()
        {
            RemoveAllEntities();
            _calc = null;
            _colorsReady = false;
            _chunkIndex = 0;
            _forceAll = true;
        }

        private void RemoveAllEntities()
        {
            if (_chunkEntities == null) return;
            for (int i = 0; i < _chunkEntities.Length; i++)
            {
                _chunkEntities[i]?.Remove(0);
                _chunkEntities[i] = null;
            }
        }
    }
}