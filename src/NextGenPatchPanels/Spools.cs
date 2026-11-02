using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NextGenPatchPanels
{
    internal sealed class SpoolType
    {
        public string Key;          // "cable-mpo" / "cable-cat6"; stable, used in shop guids
        public string Label;        // "MPO Trunk Fibre"
        public bool Fibre;          // which trunk panels it fits
        public int TwinIndex;       // the vanilla spool it's copied from (its prefabID, also its shop itemID)
        public int Index;           // ours: cableSpinnerPrefab index, prefabID and shop itemID
        public GameObject Prefab;
        public ShopItemSO SO;
        public ShopItemSO TwinSO;
    }

    // The trunk cables: copies of the vanilla copper and fibre spools. A trunk port only takes the trunk
    // cable of its kind, and a trunk cable only plugs into trunk ports (it can still hang on any holder).
    //
    // MainGameManager.cableSpinnerPrefab is indexed by a spool's prefabID, which is also what a save records
    // for a spool left lying around. MoreSpools pads the array with empty slots up to its own IDs (100+), so
    // ours sit at fixed high indices, and are put back if another mod rebuilds the array. Without the mod a
    // saved trunk spool is simply not respawned (GetCableSpinnerPrefab returns null and the loader skips it).
    internal static class Spools
    {
        const int FirstIndex = 900;
        const int CablesPerTrunk = 36;  // a trunk replaces one cable per port: priced as that many vanilla spools
        const float XpFactor = 2f;
        const float RefusalCooldown = 1.5f;
        static readonly Color MpoColour = new(0f, 0.75f, 0.8f);      // aqua
        static readonly Color Cat6Colour = new(0.18f, 0.38f, 0.85f); // blue, like a typical Cat6 bundle

        public static readonly List<SpoolType> Types = new();
        static bool _built;
        static bool _failed;
        static float _lastRefusal = -10f;

        public static SpoolType ByIndex(int index) => Types.FirstOrDefault(t => t.Index == index);
        public static SpoolType ByKey(string key) => Types.FirstOrDefault(t => t.Key == key);
        public static SpoolType For(bool fibre) => Types.FirstOrDefault(t => t.Fibre == fibre);

        public static bool EnsureRegistered(string reason)
        {
            var mgm = MainGameManager.instance;
            if (mgm == null || _failed) return false;
            var prefabs = mgm.cableSpinnerPrefab;
            if (prefabs == null || prefabs.Length == 0) return false;

            if (!_built)
            {
                _built = true;
                if (!Build(prefabs)) { _failed = true; return false; }
            }

            int need = Types.Max(t => t.Index) + 1;
            if (prefabs.Length >= need && Types.All(t => IsOurs(prefabs[t.Index], t))) return true;
            foreach (var t in Types)
                if (t.Index < prefabs.Length && prefabs[t.Index] != null && !IsOurs(prefabs[t.Index], t))
                {
                    NextGenPatchPanelsMod.Log.Error($"cableSpinnerPrefab[{t.Index}] is already '{prefabs[t.Index].name}' (another mod); trunk cables are not registered.");
                    _failed = true;
                    return false;
                }

            var extended = new Il2CppReferenceArray<GameObject>(Math.Max(prefabs.Length, need));
            for (int i = 0; i < prefabs.Length; i++) extended[i] = prefabs[i];
            foreach (var t in Types) extended[t.Index] = t.Prefab;
            mgm.cableSpinnerPrefab = extended;
            NextGenPatchPanelsMod.Log.Msg($"Registered {Types.Count} trunk cable spools at {FirstIndex}-{need - 1} ({reason}; array was {prefabs.Length} long).");
            return true;
        }

        static bool IsOurs(GameObject go, SpoolType t) => go != null && t.Prefab != null && go.GetInstanceID() == t.Prefab.GetInstanceID();

        static bool Build(Il2CppReferenceArray<GameObject> prefabs)
        {
            var shopSOs = Resources.FindObjectsOfTypeAll<ShopItemSO>()
                .Where(so => so != null && so.itemType == PlayerManager.ObjectInHand.CableSpinner).ToList();

            // The vanilla spools of each cable type (MoreSpools' copies are 100+).
            var vanilla = new List<(int index, CableSpinner spinner)>();
            for (int i = 0; i < prefabs.Length && i < 100; i++)
            {
                var s = prefabs[i] == null ? null : prefabs[i].GetComponent<CableSpinner>();
                if (s != null) vanilla.Add((i, s));
            }

            // The MPO trunk is a copy of the plain fibre spool (a QSFP-type cable would fail the game's own check
            // on the trunk port, which is an ordinary fibre port) but is priced from the QSFP fibre spool.
            ShopItemSO SpoolSO(int cableType)
            {
                var v = vanilla.Where(x => x.spinner.cableType == cableType).OrderBy(x => x.spinner.prefabID).FirstOrDefault();
                return v.spinner == null ? null : shopSOs.FirstOrDefault(so => so.itemID == v.spinner.prefabID) ?? shopSOs.FirstOrDefault(so => so.itemID == v.index);
            }

            int next = FirstIndex;
            foreach (var (key, label, fibre, cableType, priceCableType, colour) in new[]
            {
                ("cable-mpo", "MPO Trunk Fibre", true, 1, 2, MpoColour),
                ("cable-cat6", "Pre-terminated Cat6 Trunk", false, 0, 0, Cat6Colour),
            })
            {
                var twin = vanilla.Where(v => v.spinner.cableType == cableType).OrderBy(v => v.spinner.prefabID).FirstOrDefault();
                if (twin.spinner == null)
                {
                    NextGenPatchPanelsMod.Log.Warning($"No vanilla spool with cable type {cableType}; no {label}.");
                    continue;
                }
                var t = new SpoolType
                {
                    Key = key,
                    Label = label,
                    Fibre = fibre,
                    TwinIndex = twin.spinner.prefabID,
                    Index = next++,
                    TwinSO = shopSOs.FirstOrDefault(so => so.itemID == twin.spinner.prefabID) ?? shopSOs.FirstOrDefault(so => so.itemID == twin.index),
                };

                t.Prefab = Object.Instantiate(prefabs[twin.index], Templates.Holder);
                t.Prefab.name = $"CableSpinner.NextGenPatchPanels.{key}";
                var spinner = t.Prefab.GetComponent<CableSpinner>();
                spinner.prefabID = t.Index;
                var oldMaterial = spinner.cableMaterial;
                if (oldMaterial != null)
                {
                    var m = new Material(oldMaterial) { name = $"{oldMaterial.name}.NextGenPatchPanels.{key}", hideFlags = HideFlags.DontUnloadUnusedAsset };
                    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
                    if (m.HasProperty("_Color")) m.SetColor("_Color", colour);
                    spinner.cableMaterial = m;
                    // The cable wound on the spool shows the same material.
                    foreach (var r in t.Prefab.GetComponentsInChildren<Renderer>(true))
                    {
                        var mats = r.sharedMaterials;
                        bool changed = false;
                        for (int i = 0; i < mats.Length; i++)
                            if (mats[i] != null && mats[i].GetInstanceID() == oldMaterial.GetInstanceID()) { mats[i] = m; changed = true; }
                        if (changed) r.sharedMaterials = mats;
                    }
                }

                if (t.TwinSO == null)
                    NextGenPatchPanelsMod.Log.Warning($"{label}: no shop item for vanilla spool {t.TwinIndex}; it won't be sold.");
                else
                {
                    t.SO = Object.Instantiate(t.TwinSO);
                    t.SO.name = $"ShopItemSO_NextGenPatchPanels_{key}";
                    t.SO.itemID = t.Index;
                    var priceSO = SpoolSO(priceCableType) ?? t.TwinSO;
                    t.SO.price = priceSO.price * CablesPerTrunk;
                    t.SO.xpToUnlock = Templates.RoundTo10(t.TwinSO.xpToUnlock * XpFactor);
                    t.SO.hideFlags = HideFlags.DontUnloadUnusedAsset;
                }
                Types.Add(t);
                NextGenPatchPanelsMod.Log.Msg($"{label}: index {t.Index}, from spool {t.TwinIndex} ('{prefabs[twin.index].name}'), {spinner.cableLenght} m; ${t.SO?.price}, {t.SO?.xpToUnlock} XP.");
            }
            return Types.Count > 0;
        }

        // ---- which cable goes where ----

        static CableSpinner HeldSpinner()
        {
            var pm = PlayerManager.instance;
            if (pm == null || pm.objectInHand != PlayerManager.ObjectInHand.CableSpinner) return null;
            var held = pm.objectInHandGO;
            if (held == null || held.Length == 0 || held[0] == null) return null;
            return held[0].GetComponent<CableSpinner>();
        }

        // Prefix on CableLink.InteractOnClick. Only cable ends are checked; holders (isStartOrEnd false) take anything.
        public static bool CableLinkClickPrefix(CableLink __instance)
        {
            try
            {
                if (__instance == null || !__instance.isStartOrEnd || Types.Count == 0) return true;
                var spinner = HeldSpinner();
                if (spinner == null) return true;
                var ours = ByIndex(spinner.prefabID);
                bool trunkPort = Trunks.IsTrunkPort(__instance);
                if (!trunkPort && ours == null) return true;

                if (trunkPort)
                {
                    var panel = __instance.transform.parent == null ? null : __instance.transform.parent.GetComponent<PatchPanel>();
                    var panelType = Templates.Of(panel);
                    var want = panelType == null ? null : For(panelType.Fibre);
                    if (want == null || ours == want) return true;
                    Refuse($"Trunk ports only take {want.Label} cable.");
                    return false;
                }
                Refuse($"{ours.Label} only plugs into trunk patch panel trunk ports.");
                return false;
            }
            catch (Exception e)
            {
                NextGenPatchPanelsMod.Log.Error($"CableLinkClickPrefix: {e.Message}");
                return true;
            }
        }

        static void Refuse(string text)
        {
            if (Time.realtimeSinceStartup - _lastRefusal < RefusalCooldown) return;
            _lastRefusal = Time.realtimeSinceStartup;
            Trunks.Message(text);
        }
    }
}
