using System;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(NextGenPatchPanels.NextGenPatchPanelsMod), "Next-Gen Patch Panels", "1.0.0", "checkersx")]
[assembly: MelonGame("Waseku", "Data Center")]

namespace NextGenPatchPanels
{
    // Trunk patch panels (copper and fibre): 36 ports and a closed box with one trunk port on the back. A trunk
    // cable (MPO Trunk Fibre / Pre-terminated Cat6 Trunk) between the trunk ports of two panels links them:
    // port k on one reaches port k on the other (see Trunks). Racks get cable mount points inside so the trunk
    // can be routed (see Racks). Saves stay vanilla on disk (see SaveCompat).
    public class NextGenPatchPanelsMod : MelonMod
    {
        internal static MelonLogger.Instance Log;
        internal static MelonPreferences_Entry<float> PortSpeed;
        internal static MelonPreferences_Entry<bool> MountPoints;
        internal static MelonPreferences_Entry<bool> HideRackSlotsWhileCabling;

        const float RecheckInterval = 2f; // other mods (MoreSpools) rebuild the arrays we add to
        static float _nextCheck;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            var prefs = MelonPreferences.CreateCategory("NextGenPatchPanels", "Next-Gen Patch Panels");
            PortSpeed = prefs.CreateEntry("PortSpeed", 10000f, "Port speed",
                "Speed of a trunk panel's ports, in the game's units (2 = 10 Gbps). High means the cables at either end set the speed.");
            MountPoints = prefs.CreateEntry("RackMountPoints", true, "Cable mount points in racks",
                "Adds cable holders down the inside walls of every rack, for routing trunk cables. Takes effect on the next start.");
            HideRackSlotsWhileCabling = prefs.CreateEntry("HideRackSlotsWhileCabling", true, "Look past rack slots while cabling",
                "While you hold a cable spool, rack slots don't get in the way of picking ports and holders inside a rack.");
            Patching.Install(HarmonyInstance);
        }

        // Covers a new game. A loaded game also gets a call from SaveCompat before anything spawns.
        public override void OnSceneWasLoaded(int buildIndex, string sceneName) => Setup($"scene '{sceneName}' loaded");

        public override void OnSceneWasInitialized(int buildIndex, string sceneName) => Setup($"scene '{sceneName}' initialized");

        public override void OnUpdate()
        {
            SaveCompat.RestoreIfStuck();
            Trunks.Update();
            LookAssist.Update();
            if (Time.realtimeSinceStartup >= _nextCheck)
            {
                _nextCheck = Time.realtimeSinceStartup + RecheckInterval;
                Setup("check");
            }
        }

        internal static void Setup(string reason)
        {
            try
            {
                if (!Templates.EnsureRegistered(reason)) return;
                Spools.EnsureRegistered(reason); // optional: the panels work with any cable if this fails
                Racks.EnsureMountPoints(reason); // optional too
                ShopCards.EnsureCards();
            }
            catch (Exception e)
            {
                Log.Error($"Setup failed ({reason}): {e}");
            }
        }
    }
}
