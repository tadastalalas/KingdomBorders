using System;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.MountAndBlade;

namespace KingdomBorders
{
    public class SubModule : MBSubModuleBase
    {
        private BorderRendererBehavior _borderBehavior;

        // Cached parsed hotkey — re-parsed only when the MCM string changes.
        // Avoids any per-frame string allocation.
        private string _lastHotkeyString = null;
        private InputKey _parsedKey = InputKey.Invalid;
        private bool _needCtrl;
        private bool _needAlt;
        private bool _needShift;
        
        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            // Assembly passed explicitly: the no-argument PatchAll() takes it from the call stack, and in an
            // optimized (Release) build on .NET Framework that is the loader's assembly, so no patch is applied.
            new Harmony("KingdomBorders").PatchAll(typeof(SubModule).Assembly);
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            if (gameStarterObject is CampaignGameStarter campaignStarter)
            {
                _borderBehavior = new BorderRendererBehavior();
                campaignStarter.AddBehavior(_borderBehavior);
            }
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);

            if (_borderBehavior == null)
                return;

            _borderBehavior.ApplicationTick();

            // --- Hotkey polling ---
            PollToggleHotkey();

            // --- Alpha update: pass IsVisible so renderer knows whether to force 0 ---
            if (_borderBehavior.Renderer != null && _borderBehavior.MapScene != null)
            {
                float cameraHeight = _borderBehavior.MapScene.LastFinalRenderCameraPosition.z;
                Patches.TownNameplatePatch.Update(cameraHeight);
                _borderBehavior.Renderer.UpdateAlphaForCameraDistance(cameraHeight, _borderBehavior.IsVisible, dt);
                _borderBehavior.Tint?.UpdateAlphaForCameraDistance(cameraHeight, _borderBehavior.IsTintVisible);
                _borderBehavior.Labels?.Tick(cameraHeight, _borderBehavior.AreLabelsVisible, dt);
            }
        }

        private void PollToggleHotkey()
        {
            // Re-parse only when the MCM string has changed — zero allocations on steady frames
            string hotkeyStr = MCMSettings.Instance?.ToggleBordersHotkey;
            if (hotkeyStr != _lastHotkeyString)
            {
                ParseHotkey(hotkeyStr);
                _lastHotkeyString = hotkeyStr;
            }

            if (_parsedKey == InputKey.Invalid)
                return;

            // Check modifiers using individual key states — the correct Bannerlord API
            if (_needCtrl && !Input.IsKeyDown(InputKey.LeftControl) && !Input.IsKeyDown(InputKey.RightControl)) return;
            if (_needAlt && !Input.IsKeyDown(InputKey.LeftAlt) && !Input.IsKeyDown(InputKey.RightAlt)) return;
            if (_needShift && !Input.IsKeyDown(InputKey.LeftShift) && !Input.IsKeyDown(InputKey.RightShift)) return;

            if (Input.IsKeyPressed(_parsedKey))
                _borderBehavior.CycleViewMode();
        }

        /// <summary>
        /// Parses a hotkey string like "Alt+B", "Ctrl+Shift+F5", or "B" into
        /// modifier flags and an InputKey. Sets _parsedKey = InputKey.Invalid on any failure.
        /// Case-insensitive. Silently disables the hotkey if the string is blank or unrecognised.
        /// </summary>
        private void ParseHotkey(string hotkeyStr)
        {
            _parsedKey = InputKey.Invalid;
            _needCtrl = _needAlt = _needShift = false;

            if (string.IsNullOrWhiteSpace(hotkeyStr))
                return;

            // Use new[] { '+' } — single-char Split overload does not exist in .NET Framework 4.7.2
            string[] parts = hotkeyStr.Split(new[] { '+' });

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();

                if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase)) { _needCtrl = true; continue; }
                if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase)) { _needAlt = true; continue; }
                if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase)) { _needShift = true; continue; }

                // Last non-modifier token is the actual key
                if (Enum.TryParse(part, ignoreCase: true, out InputKey parsed))
                {
                    _parsedKey = parsed;
                }
                // Unrecognised token — _parsedKey stays Invalid, hotkey is disabled
            }
        }

        public override void OnGameEnd(Game game)
        {
            base.OnGameEnd(game);
            _borderBehavior?.Cleanup();
            _borderBehavior = null;
            Patches.TownNameplatePatch.Update(0f);
            _lastHotkeyString = null;
            _parsedKey = InputKey.Invalid;
            ModLog.Close();
        }
    }
}