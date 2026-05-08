using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace KingdomBorders
{
    public static class KingdomBordersCommands
    {
        // -------------------------------------------------------------------------
        // kb.change_owner_of_settlement_by_gift Locahan Castle,Rhagaea
        //
        // Transfers a town or castle to a new owner via ChangeOwnerOfSettlementAction.ApplyByGift.
        // This fires OnSettlementOwnerChanged exactly as the game would, which also
        // triggers the KingdomBorders partial border rebuild automatically.
        //
        // Separator: comma (,) — allows spaces in both the settlement and hero name.
        // -------------------------------------------------------------------------
        [CommandLineFunctionality.CommandLineArgumentFunction("change_owner_of_settlement_by_gift", "kb")]
        public static string ChangeOwnerOfSettlementByGift(List<string> args)
        {
            if (Campaign.Current == null)
                return "ERROR: No campaign is active.";

            if (args.Count == 0)
                return "Usage: kb.change_owner_of_settlement_by_gift <Settlement Name>,<Hero Name>\n" +
                       "Example: kb.change_owner_of_settlement_by_gift Locahan Castle,Rhagaea";

            // Join all args back to a single string, then split on comma
            string full = string.Join(" ", args);
            int commaIndex = full.IndexOf(',');

            if (commaIndex < 0)
                return "ERROR: Missing comma separator.\n" +
                       "Usage: kb.change_owner_of_settlement_by_gift <Settlement Name>,<Hero Name>\n" +
                       "Example: kb.change_owner_of_settlement_by_gift Locahan Castle,Rhagaea";

            string settlementName = full.Substring(0, commaIndex).Trim();
            string heroName       = full.Substring(commaIndex + 1).Trim();

            if (string.IsNullOrEmpty(settlementName))
                return "ERROR: Settlement name is empty.";

            if (string.IsNullOrEmpty(heroName))
                return "ERROR: Hero name is empty.";

            // --- Resolve settlement (towns and castles only) ---
            Settlement targetSettlement = null;
            foreach (Settlement s in Settlement.All)
            {
                if (!s.IsTown && !s.IsCastle)
                    continue;

                if (string.Compare(s.Name.ToString(), settlementName, ignoreCase: true) == 0)
                {
                    targetSettlement = s;
                    break;
                }
            }

            if (targetSettlement == null)
                return $"ERROR: No town or castle found with the name \"{settlementName}\".";

            // --- Resolve hero (alive, must have a clan) ---
            Hero targetHero = null;
            foreach (Hero h in Hero.AllAliveHeroes)
            {
                if (string.Compare(h.Name.ToString(), heroName, ignoreCase: true) == 0)
                {
                    targetHero = h;
                    break;
                }
            }

            if (targetHero == null)
                return $"ERROR: No living hero found with the name \"{heroName}\".";

            if (targetHero.Clan == null)
                return $"ERROR: Hero \"{targetHero.Name}\" has no clan. Ownership transfer requires a clan.";

            // --- Capture pre-transfer state for a useful result message ---
            string previousOwner = targetSettlement.OwnerClan?.Leader?.Name.ToString() ?? "unknown";
            string previousClan  = targetSettlement.OwnerClan?.Name.ToString() ?? "unknown";
            string newClan       = targetHero.Clan.Name.ToString();

            // --- Execute the transfer ---
            ChangeOwnerOfSettlementAction.ApplyByGift(targetSettlement, targetHero);

            return $"SUCCESS: \"{targetSettlement.Name}\" transferred from {previousOwner} ({previousClan}) " +
                   $"to {targetHero.Name} ({newClan}).";
        }

        // -------------------------------------------------------------------------
        // kb.force_rebuild_borders
        //
        // Forces a full border rebuild. Useful after manual ownership changes or
        // when borders appear out of sync for any reason.
        // -------------------------------------------------------------------------
        [CommandLineFunctionality.CommandLineArgumentFunction("force_rebuild_borders", "kb")]
        public static string ForceRebuildBorders(List<string> args)
        {
            if (Campaign.Current == null)
                return "ERROR: No campaign is active.";

            // Correct Bannerlord API: Campaign.Current.GetCampaignBehavior<T>()
            var behavior = Campaign.Current.GetCampaignBehavior<BorderRendererBehavior>();
            if (behavior == null)
                return "ERROR: BorderRendererBehavior not found. Is the mod loaded correctly?";

            if (behavior.Renderer == null)
                return "ERROR: Renderer is not initialized yet. Wait for the map to fully load.";

            behavior.ForceFullRebuild();
            return "SUCCESS: Full border rebuild has been queued.";
        }
    }
}