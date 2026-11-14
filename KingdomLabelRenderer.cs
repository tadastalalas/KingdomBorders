using System.Collections.Generic;
using System.Reflection;
using SandBox.View.Map;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ScreenSystem;
using TaleWorlds.TwoDimension;

namespace KingdomBorders
{
    /// <summary>One screen-space label per kingdom, placed on the kingdom's own cell nearest its territory centroid.</summary>
    public sealed class KingdomLabelRenderer
    {
        // MapCameraView.Instance is private static.
        private static readonly PropertyInfo CameraViewInstance =
            typeof(MapCameraView).GetProperty("Instance", BindingFlags.NonPublic | BindingFlags.Static);

        private sealed class Acc
        {
            public float SumX, SumY;
            public int Count, BestX, BestY;
            public float BestD2 = float.MaxValue;
        }

        private readonly KingdomLabelsVM _vm = new KingdomLabelsVM();
        private readonly Dictionary<Kingdom, Acc> _acc = [];
        private GauntletLayer _layer;
        private GauntletMovieIdentifier _movie;
        private MapCameraView _cameraView;
        private SpriteCategory _spriteCategory;
        private bool _loggedOnce;
        private float _alpha;
        private const float FadeSeconds = 0.4f;

        public KingdomLabelRenderer()
        {
            if (MapScreen.Instance == null) return;
            _layer = new GauntletLayer("KingdomBordersLabels", 100);
            SpriteCategory cat = UIResourceManager.GetSpriteCategory("ui_general");
            if (cat != null)
            {
                if (!cat.IsLoaded) { cat.Load(UIResourceManager.ResourceContext, UIResourceManager.ResourceDepot); _spriteCategory = cat; }
                else if (cat.IsPartiallyLoaded)
                    for (int i = 1; i <= cat.SpriteSheetCount; i++) cat.PartialLoadAtIndex(UIResourceManager.ResourceContext, UIResourceManager.ResourceDepot, i);
            }
            _movie = _layer.LoadMovie("KingdomLabels", _vm);
            _layer.InputRestrictions.SetInputRestrictions(false, InputUsageMask.Invalid);
            MapScreen.Instance.AddLayer(_layer);
            ModLog.Log("Labels: layer created");
        }

        public void Rebuild(BorderCalculator calc)
        {
            if (calc == null || _layer == null) return;
            int res = calc.GridResolution;

            _acc.Clear();
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    Kingdom k = calc.GetCellKingdom(x, y);
                    if (k == null) continue;
                    if (!_acc.TryGetValue(k, out Acc a)) _acc[k] = a = new Acc();
                    a.SumX += x; a.SumY += y; a.Count++;
                }
            }

            // Snap to the kingdom's own cell nearest its centroid so a crescent-shaped realm
            // doesn't get its label over a neighbour.
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    Kingdom k = calc.GetCellKingdom(x, y);
                    if (k == null || !_acc.TryGetValue(k, out Acc a)) continue;
                    float dx = x - a.SumX / a.Count, dy = y - a.SumY / a.Count;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < a.BestD2) { a.BestD2 = d2; a.BestX = x; a.BestY = y; }
                }
            }

            _vm.Labels.Clear();
            foreach (KeyValuePair<Kingdom, Acc> pair in _acc)
            {
                Kingdom k = pair.Key;
                Vec2 p = calc.GridToWorld(pair.Value.BestX, pair.Value.BestY);
                float h = 0f;
                CampaignVec2 cp = new CampaignVec2(p, false);
                Campaign.Current.MapSceneWrapper.GetHeightAtPoint(in cp, ref h);

                _vm.Labels.Add(new KingdomLabelVM
                {
                    Kingdom = k,
                    WorldPos = new Vec3(p.x, p.y, h + 1f),
                    Name = k.Name.ToString(),
                    Info = BuildInfo(k)
                });
            }
            ModLog.Log($"Labels: {_vm.Labels.Count} kingdoms");
        }
        
        public void RefreshInfo()
        {
            if (_layer == null) return;
            MBBindingList<KingdomLabelVM> labels = _vm.Labels;
            for (int i = 0; i < labels.Count; i++)
            {
                KingdomLabelVM l = labels[i];
                if (l.Kingdom != null) l.Info = BuildInfo(l.Kingdom);
            }
        }

        private static string BuildInfo(Kingdom k)
        {
            TextObject info = new TextObject("{=KB_LabelInfo}{FIEFS} fiefs · {CLANS} clans");
            info.SetTextVariable("FIEFS", k.Fiefs.Count);
            info.SetTextVariable("CLANS", k.Clans.Count);
            return info.ToString();
        }

        public void Tick(float cameraHeight, bool visible, float dt)
        {
            if (_layer == null) return;

            float target = visible && cameraHeight >= (MCMSettings.Instance?.LabelHeight ?? 250f) ? 1f : 0f;
            float step = dt / FadeSeconds;
            if (_alpha < target) _alpha = _alpha + step >= target ? target : _alpha + step;
            else if (_alpha > target) _alpha = _alpha - step <= target ? target : _alpha - step;
            float alpha = _alpha * _alpha * (3f - 2f * _alpha); // smoothstep
            
            bool debug = MCMSettings.Instance?.EnableDebugLogging ?? false;
            _cameraView ??= CameraViewInstance?.GetValue(null) as MapCameraView;
            Camera cam = _cameraView?.Camera;
            MBBindingList<KingdomLabelVM> labels = _vm.Labels;

            if (cam == null || alpha <= 0.01f)
            {
                if (debug && !_loggedOnce) { _loggedOnce = true; ModLog.Log($"Labels: hidden — cam={(cam != null)} alpha={alpha:F2} prop={(CameraViewInstance != null)} view={(_cameraView != null)}"); }
                for (int i = 0; i < labels.Count; i++) labels[i].IsVisible = false;
                return;
            }

            float halfW = ScreenManager.UsableArea.X * Screen.RealScreenResolutionWidth * 0.5f;
            float halfH = ScreenManager.UsableArea.Y * Screen.RealScreenResolutionHeight * 0.5f;
            float x = 0f, y = 0f, w = 0f;

            for (int i = 0; i < labels.Count; i++)
            {
                KingdomLabelVM l = labels[i];
                x = 0f; y = 0f; w = 0f;
                MBWindowManager.WorldToScreenInsideUsableArea(cam, l.WorldPos, ref x, ref y, ref w);
                l.IsVisible = debug || w > 0f;
                if (!l.IsVisible) continue;
                l.ScreenX = x - halfW;
                l.ScreenY = y - halfH;
                l.Alpha = alpha;
            }

            if (debug && !_loggedOnce && labels.Count > 0)
            {
                _loggedOnce = true;
                ModLog.Log($"Labels: count={labels.Count} last=({x:F0},{y:F0}) w={w:F2} half=({halfW:F0},{halfH:F0}) alpha={alpha:F2}");
            }
        }

        public void Dispose()
        {
            if (_layer == null) return;
            _layer.ReleaseMovie(_movie);
            MapScreen.Instance?.RemoveLayer(_layer);
            _spriteCategory?.Unload();
            _spriteCategory = null;
            _layer = null;
            _movie = null;
            _cameraView = null;
        }
    }
}