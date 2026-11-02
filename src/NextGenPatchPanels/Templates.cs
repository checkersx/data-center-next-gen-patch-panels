using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NextGenPatchPanels
{
    internal sealed class TrunkType
    {
        public string Key;          // "copper" / "fibre"; stable, used in save sidecars and shop guids
        public string Label;        // "Trunk Patch Panel (Copper)"; shop name, logs and messages
        public bool Fibre;
        public int TwinIndex;       // the vanilla panel it's built from: patchPanelsPrefabs index = patchPanelType
        public int Index;           // ours: patchPanelsPrefabs index, patchPanelType and shop itemID
        public int Ports;           // every port links on its own (vanilla pairs port i with port i + Ports/2)
        public GameObject Prefab;
        public ShopItemSO SO;
        public ShopItemSO TwinSO;
    }

    // Clones the vanilla copper and fibre patch panels into trunk panel templates and appends them to
    // MainGameManager.patchPanelsPrefabs, so the game's own buy/place/save code handles them.
    //
    // A vanilla panel is a 2U tray at the back of the rack: 36 ports on the face you patch from, in two rows
    // (top-row port i is wired to bottom-row port i), and cable loops in the open tray behind them. A trunk
    // panel keeps all 36 ports, each linking on its own, closes the tray with a box, and has one trunk port
    // on the box's back.
    internal static class Templates
    {
        public const string TrunkPortName = "NextGenPatchPanels.TrunkPort";
        const string BoxName = "NextGenPatchPanels.Box";

        const float PriceFactor = 2f;
        const float XpFactor = 2f;
        const float TrunkPortWiden = 1.6f;   // along the port's own x: an MPO adapter is wider than an LC or RJ45
        const float TrunkPortProud = 0.0015f; // how far the trunk port sits out from the box's back
        const float BoxBehindPorts = 0.015f;  // the box's front stays this far behind the port face
        const float BoxHalfWidth = 0.240f;    // inside the tray's side walls
        const float BoxMargin = 0.001f;
        static readonly Color FibreTrunkColour = new(0f, 0.75f, 0.8f); // aqua, like OM3/OM4 MPO trunks

        public static readonly List<TrunkType> Types = new();
        static readonly Dictionary<int, TrunkType> ByIndexMap = new();
        static GameObject _holder;
        static int _vanillaCount = -1;
        static bool _failed;

        public static bool Ready => Types.Count > 0;
        public static Transform Holder
        {
            get
            {
                if (_holder == null)
                {
                    // Templates live under an inactive holder so Awake/Start never run on them; the game's own
                    // Instantiate calls produce active copies that initialise normally.
                    _holder = new GameObject("NextGenPatchPanels.Templates");
                    _holder.SetActive(false);
                    Object.DontDestroyOnLoad(_holder);
                }
                return _holder.transform;
            }
        }
        public static TrunkType ByIndex(int index) => ByIndexMap.TryGetValue(index, out var t) ? t : null;
        public static TrunkType ByKey(string key) => key == null ? null : Types.FirstOrDefault(t => t.Key == key);
        public static TrunkType Of(PatchPanel panel) => panel == null ? null : ByIndex(panel.patchPanelType);

        // Returns true when the trunk panels are registered on the current MainGameManager.
        public static bool EnsureRegistered(string reason)
        {
            var mgm = MainGameManager.instance;
            if (mgm == null || _failed) return false;
            var prefabs = mgm.patchPanelsPrefabs;
            if (prefabs == null) return false;

            if (!Ready)
            {
                _vanillaCount = prefabs.Length;
                if (!Build(prefabs)) { _failed = true; return false; }
            }

            if (prefabs.Length == _vanillaCount + Types.Count && IsOurs(prefabs[Types[0].Index]))
                return true;
            if (prefabs.Length != _vanillaCount)
            {
                NextGenPatchPanelsMod.Log.Error($"patchPanelsPrefabs has {prefabs.Length} entries, expected {_vanillaCount}. " +
                                                "Another mod or a game update changed it; trunk panels are not registered.");
                _failed = true;
                return false;
            }

            var extended = new Il2CppReferenceArray<GameObject>(_vanillaCount + Types.Count);
            for (int i = 0; i < _vanillaCount; i++) extended[i] = prefabs[i];
            foreach (var t in Types) extended[t.Index] = t.Prefab;
            mgm.patchPanelsPrefabs = extended;
            NextGenPatchPanelsMod.Log.Msg($"Registered {Types.Count} trunk panels at indices {_vanillaCount}-{_vanillaCount + Types.Count - 1} ({reason}).");
            return true;
        }

        static bool IsOurs(GameObject go) =>
            go != null && Types.Any(t => t.Prefab != null && t.Prefab.GetInstanceID() == go.GetInstanceID());

        static bool Build(Il2CppReferenceArray<GameObject> prefabs)
        {
            var shopSOs = Resources.FindObjectsOfTypeAll<ShopItemSO>()
                .Where(so => so != null && so.itemType == PlayerManager.ObjectInHand.PatchPanel).ToList();

            int copper = -1, fibre = -1;
            for (int i = 0; i < prefabs.Length; i++)
            {
                var go = prefabs[i];
                if (go == null) continue;
                var name = go.name.ToLowerInvariant();
                if (name.Contains("combo")) continue;
                if (name.Contains("fiber") || name.Contains("fibre")) { if (fibre < 0) fibre = i; }
                else if (copper < 0) copper = i;
            }

            int next = prefabs.Length;
            foreach (var (key, label, isFibre, twin) in new[]
            {
                ("copper", "Trunk Patch Panel (Copper)", false, copper),
                ("fibre", "Trunk Patch Panel (Fibre)", true, fibre),
            })
            {
                if (twin < 0)
                {
                    NextGenPatchPanelsMod.Log.Warning($"No vanilla {key} patch panel found; no {label}.");
                    continue;
                }
                var t = BuildType(key, label, isFibre, twin, prefabs[twin], next, shopSOs);
                if (t == null) continue;
                Types.Add(t);
                ByIndexMap[t.Index] = t;
                next++;
            }
            return Ready;
        }

        static TrunkType BuildType(string key, string label, bool fibre, int twinIndex, GameObject twin, int index, List<ShopItemSO> shopSOs)
        {
            var go = Object.Instantiate(twin, Holder);
            go.name = fibre ? "PatchPanel_fiber_trunk" : "PatchPanel_trunk"; // also the start of every panel and port ID
            var root = go.transform;
            var panel = go.GetComponent<PatchPanel>();
            var ports = go.GetComponentsInChildren<CableLink>(true);
            var body = BodyBounds(go, ports);
            if (panel == null || ports.Length < 2 || ports.Length % 2 != 0 || !body.HasValue)
            {
                NextGenPatchPanelsMod.Log.Warning($"{label}: '{twin.name}' has {ports.Length} ports{(panel == null ? ", no PatchPanel" : "")}" +
                                                  $"{(body.HasValue ? "" : ", no body mesh")}; not built.");
                Object.Destroy(go);
                return null;
            }

            var local = ports.Select(p => root.InverseTransformPoint(p.transform.position)).ToArray();
            var face = Span(local);
            var b = body.Value;
            var t = new TrunkType
            {
                Key = key,
                Label = label,
                Fibre = fibre,
                TwinIndex = twinIndex,
                Index = index,
                Ports = ports.Length,
                Prefab = go,
                TwinSO = shopSOs.FirstOrDefault(so => so.itemID == twinIndex),
            };

            float speed = NextGenPatchPanelsMod.PortSpeed.Value;
            foreach (var p in ports) p.connectionSpeed = Mathf.Max(p.connectionSpeed, speed);

            // The ports face +z (the back of the rack); the tray runs from there towards the rack's front (-z).
            float backZ = b.min.z;
            float frontZ = face.min.z - BoxBehindPorts;
            var material = BodyMaterial(go, ports) ?? ports[0].GetComponentInChildren<Renderer>(true)?.sharedMaterial;
            Shapes.Box(BoxName, root, material,
                new Vector3(b.center.x, b.center.y, (backZ + frontZ) / 2f),
                new Vector3(Mathf.Min(BoxHalfWidth * 2f, b.size.x - 2 * BoxMargin), b.size.y - 2 * BoxMargin, frontZ - backZ));

            // The trunk port: a copy of a port, turned to face the rack's front, on the middle of the box's back.
            // Same spot as v0.1, so trunk cables in existing saves still find it.
            var donor = ports[ports.Length / 4];
            var rot = Quaternion.AngleAxis(180f, Vector3.up) * (Quaternion.Inverse(root.rotation) * donor.transform.rotation);
            var trunk = MakeTrunkPort(root, donor, new Vector3(face.center.x, b.center.y, backZ - TrunkPortProud), rot, fibre, speed);

            panel.patchPanelType = index;

            if (t.TwinSO == null)
                NextGenPatchPanelsMod.Log.Warning($"{label}: no shop item for vanilla patch panel {twinIndex}; it won't be sold, but saves with it still load.");
            else
            {
                t.SO = Object.Instantiate(t.TwinSO);
                t.SO.name = $"ShopItemSO_NextGenPatchPanels_{key}";
                t.SO.itemID = index;
                t.SO.price = RoundTo10(t.TwinSO.price * PriceFactor);
                t.SO.xpToUnlock = RoundTo10(t.TwinSO.xpToUnlock * XpFactor);
                t.SO.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }

            NextGenPatchPanelsMod.Log.Msg($"{label}: index {index}, from '{twin.name}' ({twinIndex}), {t.Ports} ports, " +
                                          $"box z {V1(backZ)}..{V1(frontZ)}, trunk port at {V(trunk.transform.localPosition)}; ${t.SO?.price}, {t.SO?.xpToUnlock} XP.");
            return t;
        }

        static CableLink MakeTrunkPort(Transform root, CableLink donor, Vector3 localPos, Quaternion localRot, bool fibre, float speed)
        {
            var go = Object.Instantiate(donor.gameObject, root);
            go.name = TrunkPortName;
            go.transform.SetAsLastSibling(); // keeps it last in the panel's port list
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            var ds = donor.transform.lossyScale;
            var rs = root.lossyScale;
            go.transform.localScale = new Vector3(ds.x / rs.x * TrunkPortWiden, ds.y / rs.y, ds.z / rs.z);

            // References inside the copied port that point outside it would still lead to the donor.
            var link = go.GetComponent<CableLink>();
            link.switchID = "";            // set per panel when it's racked (Trunks.InsertedInRackPostfix)
            link.cableIDsOnLink = -1;
            link.typeOfLink = CableLink.TypeOfLink.Switch; // like vanilla panel ports
            link.isStartOrEnd = true;
            link.connectionSpeed = speed;
            if (link.ropeAttachPoint != null && !link.ropeAttachPoint.IsChildOf(go.transform)) link.ropeAttachPoint = null;
            if (link.outlineEffect != null && !link.outlineEffect.transform.IsChildOf(go.transform)) link.outlineEffect = null;

            if (fibre) Tint(go, FibreTrunkColour);
            return link;
        }

        public static void Tint(GameObject go, Color colour)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                var tinted = new Il2CppReferenceArray<Material>(mats.Length);
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    var m = new Material(mats[i]) { name = mats[i].name + ".NextGenPatchPanels", hideFlags = HideFlags.DontUnloadUnusedAsset };
                    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
                    if (m.HasProperty("_Color")) m.SetColor("_Color", colour);
                    tinted[i] = m;
                }
                r.sharedMaterials = tinted;
            }
        }

        // The panel's own meshes (not its ports), in the panel's space.
        static Bounds? BodyBounds(GameObject go, CableLink[] ports)
        {
            var root = go.transform;
            Bounds? result = null;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || UnderAnyPort(mf.transform, ports)) continue;
                var b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                    var p = root.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (result == null) result = new Bounds(p, Vector3.zero);
                    else { var r = result.Value; r.Encapsulate(p); result = r; }
                }
            }
            return result;
        }

        // The material of the panel's biggest mesh, for the box.
        static Material BodyMaterial(GameObject go, CableLink[] ports)
        {
            Material best = null;
            float bestSize = -1f;
            foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (UnderAnyPort(mr.transform, ports)) continue;
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || mr.sharedMaterial == null) continue;
                var s = Vector3.Scale(mf.sharedMesh.bounds.size, mr.transform.lossyScale);
                float size = s.x * s.y + s.y * s.z + s.x * s.z;
                if (size > bestSize) { bestSize = size; best = mr.sharedMaterial; }
            }
            return best;
        }

        static bool UnderAnyPort(Transform t, CableLink[] ports)
        {
            foreach (var p in ports)
                if (t.IsChildOf(p.transform)) return true;
            return false;
        }

        static Bounds Span(Vector3[] v)
        {
            var b = new Bounds(v[0], Vector3.zero);
            foreach (var p in v) b.Encapsulate(p);
            return b;
        }

        public static int RoundTo10(double v) => (int)(Math.Round(v / 10.0) * 10);

        public static string V(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "({0:F4}, {1:F4}, {2:F4})", v.x, v.y, v.z);
        static string V1(float f) => f.ToString("F4", CultureInfo.InvariantCulture);
    }
}
