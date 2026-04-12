using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace KingdomBorders
{
    public class SubModule : MBSubModuleBase
    {
        private BorderRendererBehavior _borderBehavior;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
        }

        protected override void OnSubModuleUnloaded()
        {
            base.OnSubModuleUnloaded();
        }

        protected override void OnBeforeInitialModuleScreenSetAsRoot()
        {
            base.OnBeforeInitialModuleScreenSetAsRoot();
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

            // Drive the build pipeline every frame — works even while paused on load
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