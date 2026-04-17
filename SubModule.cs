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

            _borderBehavior.ApplicationTick();

            if (_borderBehavior.MapScene == null)
                return;

            float cameraHeight = _borderBehavior.MapScene.LastFinalRenderCameraPosition.z;

            if (_borderBehavior.Renderer != null)
            {
                _borderBehavior.Renderer.UpdateAlphaForCameraDistance(cameraHeight);
            }

            if (_borderBehavior.FillRenderer != null)
            {
                float fillOpacity = MCMSettings.Instance?.FillOpacity ?? 0.20f;
                _borderBehavior.FillRenderer.UpdateAlphaForCameraDistance(cameraHeight, fillOpacity);
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