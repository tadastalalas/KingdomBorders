using HarmonyLib;
using SandBox.ViewModelCollection.Nameplate;

namespace KingdomBorders.Patches
{
    // Postfix: let vanilla IsVisible() decide, then force towns off above the configured height.
    [HarmonyPatch(typeof(SettlementNameplateVM), "DetermineIsVisibleOnMap")]
    internal static class TownNameplatePatch
    {
        private static readonly AccessTools.FieldRef<SettlementNameplateVM, bool> BindIsVisibleOnMap =
            AccessTools.FieldRefAccess<SettlementNameplateVM, bool>("_bindIsVisibleOnMap");

        private static volatile bool _hide;

        // Once per frame from SubModule — keeps MCM lookups off the TWParallel worker threads.
        internal static void Update(float cameraHeight)
        {
            int limit = MCMSettings.Instance?.TownNameplateHideHeight ?? 0;
            _hide = limit > 0 && cameraHeight > limit;
        }

        private static void Postfix(SettlementNameplateVM __instance)
        {
            if (!_hide || __instance?.Settlement == null || !__instance.Settlement.IsTown) return;
            BindIsVisibleOnMap(__instance) = false;
        }
    }
}