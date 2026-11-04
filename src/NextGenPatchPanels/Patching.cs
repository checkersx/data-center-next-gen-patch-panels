using System;
using HarmonyLib;
using Il2Cpp;

namespace NextGenPatchPanels
{
    internal static class Patching
    {
        public static void Install(HarmonyLib.Harmony h)
        {
            Patch(h, typeof(SaveSystem), nameof(SaveSystem.SaveGame), typeof(SaveCompat), nameof(SaveCompat.SaveGamePrefix), nameof(SaveCompat.SaveGamePostfix));
            Patch(h, typeof(SaveSystem), nameof(SaveSystem.WriteMeta), typeof(SaveCompat), nameof(SaveCompat.WriteMetaPrefix), null);
            Patch(h, typeof(SaveSystem), nameof(SaveSystem.LoadGame), typeof(SaveCompat), null, nameof(SaveCompat.LoadGamePostfix));
            Patch(h, typeof(SaveSystem), nameof(SaveSystem.MoveSaveFiles), typeof(SaveCompat), null, nameof(SaveCompat.MoveSaveFilesPostfix));
            Patch(h, typeof(SaveSystem), nameof(SaveSystem.DeleteSaveFile), typeof(SaveCompat), null, nameof(SaveCompat.DeleteSaveFilePostfix));
            Patch(h, typeof(SaveSystem), nameof(SaveSystem.DeleteSaveFilesQuiet), typeof(SaveCompat), null, nameof(SaveCompat.DeleteSaveFilePostfix));

            // Last: gregCore's postfix can change the panel's ID ("restored clone ID"); ports follow the final one.
            Patch(h, typeof(PatchPanel), nameof(PatchPanel.InsertedInRack), typeof(Trunks), nameof(Trunks.InsertedInRackPrefix), nameof(Trunks.InsertedInRackPostfix), Priority.Last);
            Patch(h, typeof(PatchPanel), nameof(PatchPanel.GetPairedLink), typeof(Trunks), null, nameof(Trunks.GetPairedLinkPostfix));
            Patch(h, typeof(NetworkMap), nameof(NetworkMap.RegisterCableConnection), typeof(Trunks), null, nameof(Trunks.RegisterCableConnectionPostfix));
            Patch(h, typeof(NetworkMap), nameof(NetworkMap.RemoveCableConnection), typeof(Trunks), null, nameof(Trunks.RemoveCableConnectionPostfix));
            Patch(h, typeof(CableLink), nameof(CableLink.InteractOnClick), typeof(Spools), nameof(Spools.CableLinkClickPrefix), null);
            Patch(h, typeof(CablePositions), nameof(CablePositions.RedrawCable), typeof(CableLooks), nameof(CableLooks.RedrawCablePrefix), nameof(CableLooks.RedrawCablePostfix));

            Patch(h, typeof(ShopItem), nameof(ShopItem.Start), typeof(ShopCards), null, nameof(ShopCards.RefreshTextPostfix));
            Patch(h, typeof(ShopItem), nameof(ShopItem.UpdateVisualState), typeof(ShopCards), null, nameof(ShopCards.RefreshTextPostfix));
        }

        static void Patch(HarmonyLib.Harmony h, Type type, string method, Type patchType, string prefix, string postfix, int postfixPriority = -1)
        {
            try
            {
                var target = AccessTools.Method(type, method);
                if (target == null)
                {
                    NextGenPatchPanelsMod.Log.Error($"Patch target {type.Name}.{method} not found; the game may have changed.");
                    return;
                }
                h.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(AccessTools.Method(patchType, prefix)),
                    postfix: postfix == null ? null : new HarmonyMethod(AccessTools.Method(patchType, postfix)) { priority = postfixPriority });
            }
            catch (Exception e)
            {
                NextGenPatchPanelsMod.Log.Error($"Patching {type.Name}.{method} failed: {e.Message}");
            }
        }
    }
}
