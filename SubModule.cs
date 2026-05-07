using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.MountAndBlade;

namespace KingdomBorders
{
    public class SubModule : MBSubModuleBase
    {
        private BorderRendererBehavior _borderBehavior;
        private bool _mPrev; // previous-frame M state for edge detection

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

            if (_borderBehavior.Renderer != null && _borderBehavior.MapScene != null)
            {
                float cameraHeight = _borderBehavior.MapScene.LastFinalRenderCameraPosition.z;
                _borderBehavior.Renderer.UpdateAlphaForCameraDistance(cameraHeight);
            }

            HandleHotkey();
        }

        /// <summary>
        /// Edge-detected M key. Only fires on the campaign map, never in missions or
        /// fullscreen menus, so we don't accidentally hijack a user-bound key elsewhere.
        /// </summary>
        private void HandleHotkey()
        {
            // Only react when we're actually on the campaign map screen
            if (Game.Current == null || GameStateManager.Current?.ActiveState is not MapState)
            {
                _mPrev = Input.IsKeyDown(InputKey.M);
                return;
            }

            bool now = Input.IsKeyDown(InputKey.M);
            if (now && !_mPrev)
            {
                _borderBehavior.ToggleFillVisibility();
            }
            _mPrev = now;
        }

        public override void OnGameEnd(Game game)
        {
            base.OnGameEnd(game);
            _borderBehavior?.Cleanup();
            _borderBehavior = null;
        }
    }
}