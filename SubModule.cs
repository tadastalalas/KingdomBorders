using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace KingdomBorders
{
    public class SubModule : MBSubModuleBase
    {
        private BorderRendererBehavior _borderBehavior;

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
        }

        public override void OnGameEnd(Game game)
        {
            base.OnGameEnd(game);
            _borderBehavior?.Cleanup();
            _borderBehavior = null;
        }
    }
}