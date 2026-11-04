using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace NextGenPatchPanels
{
    internal sealed class Link
    {
        public int CableId;
        public PatchPanel Owner;  // its port IDs are the shared ones
        public PatchPanel Other;  // its ports take the owner's IDs while linked
        public string OwnerId;
        public string OtherId;
        public int Ports;
        public bool Fibre;
    }

    // How a trunk works. Every cable end is named after its port's switchID, and the network graph (routing,
    // VLANs, firewalls, malware) joins cables whose ends share a name. Vanilla gives a panel's two ports of a
    // pair the same ID, "<panelId>_<k>", so a cable into one meets a cable into the other.
    //
    // A trunk panel gives each of its 36 ports its own ID instead. Linking panel B to panel A gives B's port k
    // A's port-k ID, so cables on A's port k and B's port k meet at one node, exactly as across a vanilla pair.
    //
    // The link is made by a cable between the two panels' trunk ports, so the save stores it as an ordinary
    // cable. Cables already plugged into B are renamed (their CableInfo and their entry in NetworkMap) when
    // the link is made, and renamed back when the trunk cable is removed.
    //
    // Names on cables can go out of date. Two panels can end up with one ID (another mod gives a new panel an
    // ID made from the spot it was delivered to, so two panels bought to one spot match), and the game then
    // renames one of them on load while the cables in it keep the old name. So which panel and port a cable
    // end belongs to is decided by where it is plugged in, and its name is corrected to match.
    internal static class Trunks
    {
        public const string TrunkSuffix = "_MPO";
        const float ReconcileDelay = 0.5f;   // seconds; lets the game finish whatever registered the cable
        const float AfterLoadDelay = 2f;
        const float PortSnap = 0.01f;        // a cable end this close to a port is in it (ports are 2.5 cm apart)
        const float TrunkSnap = 0.05f;       // the same for trunk ports (one per panel, panels are 2U high)

        static readonly Dictionary<int, Link> ByCable = new();
        static readonly Dictionary<string, Link> ByPanel = new();
        static readonly Dictionary<string, PatchPanel> LivePanels = new();
        static readonly HashSet<int> PendingRedraws = new();
        static float _reconcileAt = -1f;
        static readonly List<string> _reconcileWhy = new();
        static bool _busy; // our own graph edits; don't treat them as the player's

        public static void Reset(string reason)
        {
            if (ByCable.Count > 0) NextGenPatchPanelsMod.Log.Msg($"Forgetting {ByCable.Count} trunk link(s) ({reason}).");
            ByCable.Clear();
            ByPanel.Clear();
            LivePanels.Clear();
            PendingRedraws.Clear();
            _reconcileAt = -1f;
            _reconcileWhy.Clear();
        }

        // The kind of trunk a cable is (for its look), or null if it isn't a trunk cable.
        public static bool? IsFibreTrunk(int cableId) => ByCable.TryGetValue(cableId, out var link) ? link.Fibre : null;

        // ---- panel set-up ----

        // The trunk port must not take part in vanilla's port pairing or device registration.
        public static void InsertedInRackPrefix(PatchPanel __instance)
        {
            try
            {
                if (Templates.Of(__instance) == null || __instance.cableLinkPorts == null) return;
                __instance.cableLinkPorts = new Il2CppReferenceArray<CableLink>(Ports(__instance).ToArray());
            }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"InsertedInRackPrefix: {e}"); }
        }

        // Patched with Priority.Last: another mod's postfix may change the panel's ID after the game set it,
        // and the ports must be named after the ID the panel ends up with.
        public static void InsertedInRackPostfix(PatchPanel __instance)
        {
            try
            {
                var t = Templates.Of(__instance);
                if (t == null) return;
                var id = MakeIdUnique(__instance, t);
                var ports = Ports(__instance);

                // Vanilla named the pairs "<id>_1".."<id>_18"; give every port its own ID and device.
                var map = NetworkMap.instance;
                for (int i = 0; i < ports.Count; i++)
                {
                    ports[i].switchID = PortId(id, i);
                    ports[i].typeOfLink = CableLink.TypeOfLink.Switch;
                    if (map != null) EnsureDevice(map, ports[i].switchID);
                }

                var trunk = TrunkPort(__instance);
                if (trunk == null)
                    NextGenPatchPanelsMod.Log.Warning($"{t.Label} '{id}' has no trunk port.");
                else
                {
                    trunk.switchID = id + TrunkSuffix;
                    trunk.typeOfLink = CableLink.TypeOfLink.Switch;
                    ports.Add(trunk); // last, so it still counts for IsAnyCableConnected (no unracking with a trunk attached)
                }
                __instance.cableLinkPorts = new Il2CppReferenceArray<CableLink>(ports.ToArray());
                LivePanels[id] = __instance;

                // Racked again while linked (the IDs were just reset): link it again.
                var link = LinkOf(__instance);
                if (link != null)
                {
                    SetPortIds(link, true);
                    Schedule(ReconcileDelay, "panel racked again");
                }
            }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"InsertedInRackPostfix: {e}"); }
        }

        // Two panels with one ID would share every port. The game only fixes that on load, and another mod gives
        // a new panel an ID made from the spot it was delivered to, so a second panel bought to the same spot
        // arrives with the first one's ID. The panel being racked gets a fresh ID (it has no cables yet).
        static string MakeIdUnique(PatchPanel panel, TrunkType t)
        {
            var id = panel.patchPanelId;
            if (string.IsNullOrEmpty(id)) return id;
            var twin = PanelsInScene(false).FirstOrDefault(p => p.Pointer != panel.Pointer && p.patchPanelId == id);
            if (twin == null) return id;
            var fresh = panel.GenerateUniquePatchPanelId();
            if (string.IsNullOrEmpty(fresh) || fresh == id) return id;
            panel.patchPanelId = fresh;
            NextGenPatchPanelsMod.Log.Msg($"{t.Label} '{id}' has the same ID as the panel at {Where(twin.transform.position)}; renamed to '{fresh}'.");
            return fresh;
        }

        // ---- making and breaking links ----

        // Postfix on NetworkMap.RegisterCableConnection(cableId, startPos, endPos, startType, endType, startId,
        // endId, ...): a cable between two trunk ports links their panels. The panels are found by the trunk
        // ports the ends are plugged into; the names on the cable can be out of date.
        public static void RegisterCableConnectionPostfix(int __0, Vector3 __1, Vector3 __2, string __5, string __6)
        {
            if (_busy || !IsTrunkId(__5) || !IsTrunkId(__6)) return;
            try
            {
                var panels = PanelsInScene(true);
                var a = PanelWithTrunkAt(panels, __1) ?? FindPanel(__5);
                var b = PanelWithTrunkAt(panels, __2) ?? FindPanel(__6);
                if (a == null || b == null)
                {
                    NextGenPatchPanelsMod.Log.Warning($"Trunk cable {__0} between '{__5}' and '{__6}': {(a == null ? __5 : __6)} not found; not linked.");
                    return;
                }
                if (a.Pointer == b.Pointer)
                {
                    NextGenPatchPanelsMod.Log.Warning($"Trunk cable {__0} has both ends on '{a.patchPanelId}'; not linked.");
                    return;
                }
                MakeLink(a, b, __0);
            }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"RegisterCableConnectionPostfix: {e}"); }
        }

        // Postfix on NetworkMap.RemoveCableConnection: the trunk cable going breaks the link.
        public static void RemoveCableConnectionPostfix(int __0)
        {
            if (_busy || !ByCable.TryGetValue(__0, out var link)) return;
            try { BreakLink(link, "trunk cable removed"); }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"RemoveCableConnectionPostfix: {e}"); }
        }

        static void MakeLink(PatchPanel a, PatchPanel b, int cableId)
        {
            if (ByCable.TryGetValue(cableId, out var same) && IsSame(same, a, b)) return; // registered again, e.g. on load

            foreach (var p in new[] { a, b })
            {
                var old = LinkOf(p);
                if (old == null) continue;
                NextGenPatchPanelsMod.Log.Warning($"'{p.patchPanelId}' was still linked by cable {old.CableId}; dropping that link.");
                BreakLink(old, "replaced");
            }

            var (owner, other) = string.CompareOrdinal(a.patchPanelId, b.patchPanelId) <= 0 ? (a, b) : (b, a);
            var ownerType = Templates.Of(owner);
            var link = new Link
            {
                CableId = cableId,
                Owner = owner,
                Other = other,
                OwnerId = owner.patchPanelId,
                OtherId = other.patchPanelId,
                Ports = Math.Min(ownerType.Ports, Templates.Of(other).Ports),
                Fibre = ownerType.Fibre,
            };
            ByCable[cableId] = link;
            ByPanel[link.OwnerId] = link;
            ByPanel[link.OtherId] = link;

            SetPortIds(link, true); // new cables on the other panel join straight away
            Schedule(IsLoading() ? AfterLoadDelay : ReconcileDelay, "trunk linked");
            PendingRedraws.Add(cableId); // its trunk look; not from inside the game's cable code

            NextGenPatchPanelsMod.Log.Msg($"Trunk cable {cableId} links '{link.OwnerId}' at {Where(owner.transform.position)} and '{link.OtherId}' at {Where(other.transform.position)} ({link.Ports} ports).");
            if (!IsLoading()) Message($"Trunk linked: {link.Ports} ports");
        }

        static void BreakLink(Link link, string reason)
        {
            ByCable.Remove(link.CableId);
            PendingRedraws.Remove(link.CableId);
            if (ByPanel.TryGetValue(link.OwnerId, out var l1) && l1 == link) ByPanel.Remove(link.OwnerId);
            if (ByPanel.TryGetValue(link.OtherId, out var l2) && l2 == link) ByPanel.Remove(link.OtherId);
            if (link.Other != null) SetPortIds(link, false);
            Schedule(ReconcileDelay, "trunk unlinked");
            NextGenPatchPanelsMod.Log.Msg($"Trunk link '{link.OwnerId}' - '{link.OtherId}' broken ({reason}).");
            if (!IsLoading()) Message("Trunk unlinked");
        }

        static bool IsSame(Link l, PatchPanel a, PatchPanel b) =>
            (Same(l.Owner, a) && Same(l.Other, b)) || (Same(l.Owner, b) && Same(l.Other, a));

        static bool Same(PatchPanel x, PatchPanel y) => x != null && y != null && x.Pointer == y.Pointer;

        // The live link this panel is part of, if any.
        static Link LinkOf(PatchPanel panel)
        {
            if (panel == null || string.IsNullOrEmpty(panel.patchPanelId)) return null;
            return ByPanel.TryGetValue(panel.patchPanelId, out var link) && (Same(link.Owner, panel) || Same(link.Other, panel)) ? link : null;
        }

        // The other panel's ports carry the owner's IDs while linked, their own otherwise.
        static void SetPortIds(Link link, bool linked)
        {
            var ports = Ports(link.Other);
            for (int k = 0; k < link.Ports && k < ports.Count; k++)
                ports[k].switchID = PortId(linked ? link.OwnerId : link.OtherId, k);
        }

        // ---- keeping cable names right ----

        static void Schedule(float delay, string why)
        {
            if (!_reconcileWhy.Contains(why)) _reconcileWhy.Add(why);
            float at = Time.realtimeSinceStartup + delay;
            if (_reconcileAt < 0f || at > _reconcileAt) _reconcileAt = at;
        }

        // Called every frame.
        public static void Update()
        {
            if (PendingRedraws.Count > 0 && !IsLoading()) Redraw();

            if (_reconcileAt < 0f || Time.realtimeSinceStartup < _reconcileAt) return;
            if (IsLoading()) { _reconcileAt = Time.realtimeSinceStartup + AfterLoadDelay; return; }
            _reconcileAt = -1f;
            var why = string.Join(", ", _reconcileWhy);
            _reconcileWhy.Clear();
            try { Reconcile(why); }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"Checking trunk panel cables ({why}): {e}"); }
        }

        static void Redraw()
        {
            var cp = CablePositions.instance;
            if (cp == null) return;
            foreach (var id in PendingRedraws.ToList())
            {
                PendingRedraws.Remove(id);
                try { if (cp.cableGameObjects != null && cp.cableGameObjects.ContainsKey(id)) cp.RedrawCable(id); }
                catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"Redrawing trunk cable {id}: {e.Message}"); }
            }
        }

        // Every cable end plugged into a trunk panel gets the name of the port it is in: the owner's port k
        // while the panel is linked, its own port k otherwise, and the panel's trunk ID on its trunk port.
        static void Reconcile(string why)
        {
            var wis = WaypointInitializationSystem.Instance;
            var map = NetworkMap.instance;
            if (wis == null || map == null) return;

            var portSpots = new List<(Vector3 pos, string want)>();
            var trunkSpots = new List<(Vector3 pos, string want)>();
            foreach (var panel in PanelsInScene(true))
            {
                var id = panel.patchPanelId;
                if (string.IsNullOrEmpty(id)) continue;
                var link = LinkOf(panel);
                var ports = Ports(panel);
                for (int k = 0; k < ports.Count; k++)
                    portSpots.Add((ports[k].transform.position, PortId(link != null && k < link.Ports ? link.OwnerId : id, k)));
                var trunk = TrunkPort(panel);
                if (trunk != null) trunkSpots.Add((trunk.transform.position, id + TrunkSuffix));
            }
            if (portSpots.Count == 0) return;

            var changed = new List<WaypointInitializationSystem.CableInfo>();
            var notes = new List<string>();
            int ends = 0;
            var all = wis.GetAllCables(); // a copy; UpdateCableInfo writes back to the live table
            for (int c = 0; c < all.Count; c++)
            {
                var info = all[c];
                bool dirty = false;
                for (int e = 0; e < 2; e++)
                {
                    var end = e == 0 ? info.StartPoint : info.EndPoint;
                    if (end.Type != CableLink.TypeOfLink.Switch || end.SwitchID == null || !end.SwitchID.Contains("PatchPanel")) continue;
                    var want = IsTrunkId(end.SwitchID) ? Nearest(trunkSpots, end.Position, TrunkSnap) : Nearest(portSpots, end.Position, PortSnap);
                    if (want == null || end.SwitchID == want) continue;

                    if (notes.Count < 12) notes.Add($"cable {info.CableID} '{end.SwitchID}' -> '{want}'");
                    end.SwitchID = want;
                    if (e == 0) info.StartPoint = end;
                    else info.EndPoint = end;
                    dirty = true;
                    ends++;
                }
                if (!dirty) continue;
                wis.UpdateCableInfo(info.CableID, info);
                changed.Add(info);
            }

            _busy = true;
            try
            {
                // NetworkMap keeps its own copy of each cable's ends; re-register the renamed ones.
                foreach (var info in changed)
                {
                    map.RemoveCableConnection(info.CableID, true);
                    wis.RegisterCableInNetworkMap(info);
                }
                // Vanilla registers every port as a device when a panel is racked; keep that true.
                foreach (var want in portSpots.Select(s => s.want).Distinct())
                    EnsureDevice(map, want);
            }
            finally { _busy = false; }

            wis.cablePairLookupDirty = true;
            wis.RequestRouteEvaluation();
            NextGenPatchPanelsMod.Log.Msg($"Trunk panel cables checked ({why}): {ByCable.Count} link(s), renamed {ends} cable end(s)" +
                                          (notes.Count > 0 ? $": {string.Join("; ", notes)}{(ends > notes.Count ? "; ..." : "")}" : "") + ".");
        }

        static string Nearest(List<(Vector3 pos, string want)> spots, Vector3 pos, float within)
        {
            string best = null;
            float bestDistance = within;
            foreach (var (spot, want) in spots)
            {
                float d = Vector3.Distance(spot, pos);
                if (d < bestDistance) { bestDistance = d; best = want; }
            }
            return best;
        }

        static PatchPanel PanelWithTrunkAt(List<PatchPanel> panels, Vector3 pos)
        {
            PatchPanel best = null;
            float bestDistance = TrunkSnap;
            foreach (var p in panels)
            {
                var trunk = TrunkPort(p);
                if (trunk == null) continue;
                float d = Vector3.Distance(trunk.transform.position, pos);
                if (d < bestDistance) { bestDistance = d; best = p; }
            }
            return best;
        }

        static void EnsureDevice(NetworkMap map, string id)
        {
            if (map.GetDevice(id) == null) map.AddDevice(id, CableLink.TypeOfLink.Switch, -1);
        }

        // ---- tracing through a trunk ----

        // Postfix on PatchPanel.GetPairedLink, used by the switch screen and cable highlighting to follow a
        // path through a panel. A trunk panel's ports have no partner on the same panel (vanilla returns null,
        // which both callers handle); a linked one's port k pairs with the other panel's port k.
        public static void GetPairedLinkPostfix(PatchPanel __instance, CableLink __0, ref CableLink __result)
        {
            if (ByPanel.Count == 0 || __0 == null) return;
            try
            {
                if (Templates.Of(__instance) == null) return;
                var link = LinkOf(__instance);
                if (link == null) return;
                int k = Ports(__instance).FindIndex(p => p.Pointer == __0.Pointer);
                if (k < 0 || k >= link.Ports) return;
                var partner = Same(link.Owner, __instance) ? link.Other : link.Owner;
                if (partner == null) return;
                var partnerPorts = Ports(partner);
                if (k < partnerPorts.Count) __result = partnerPorts[k];
            }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"GetPairedLinkPostfix: {e.Message}"); }
        }

        // ---- helpers ----

        public static string PortId(string panelId, int k) => $"{panelId}_{k + 1}";

        public static bool IsTrunkPort(CableLink link) => link != null && link.name == Templates.TrunkPortName;

        static bool IsTrunkId(string id) => id != null && id.EndsWith(TrunkSuffix, StringComparison.Ordinal);

        static bool IsLoading()
        {
            var wis = WaypointInitializationSystem.Instance;
            return wis != null && wis.isLoading;
        }

        static string Where(Vector3 p) => $"({p.x:F2}, {p.y:F2}, {p.z:F2})";

        public static CableLink TrunkPort(PatchPanel panel)
        {
            var t = panel.transform.Find(Templates.TrunkPortName);
            return t == null ? null : t.GetComponent<CableLink>();
        }

        // The panel's patch ports, in vanilla's order, without the trunk port.
        public static List<CableLink> Ports(PatchPanel panel)
        {
            var list = new List<CableLink>();
            var ports = panel.cableLinkPorts;
            if (ports != null && ports.Length > 0)
                for (int i = 0; i < ports.Length; i++) list.Add(ports[i]);
            else
                foreach (var p in panel.GetComponentsInChildren<CableLink>(true)) list.Add(p);
            list.RemoveAll(p => p == null || IsTrunkPort(p));
            return list;
        }

        // Panels in the world (not templates or prefabs); trunk panels only, or all of them.
        static List<PatchPanel> PanelsInScene(bool trunkOnly)
        {
            var list = new List<PatchPanel>();
            foreach (var p in Resources.FindObjectsOfTypeAll<PatchPanel>())
            {
                if (p == null || !p.gameObject.activeInHierarchy || string.IsNullOrEmpty(p.gameObject.scene.name)) continue;
                if (trunkOnly && Templates.Of(p) == null) continue;
                list.Add(p);
            }
            return list;
        }

        // By name: only used when no trunk port is where the cable ends.
        static PatchPanel FindPanel(string trunkId)
        {
            var id = trunkId.Substring(0, trunkId.Length - TrunkSuffix.Length);
            if (LivePanels.TryGetValue(id, out var p) && p != null && p.patchPanelId == id) return p;
            foreach (var panel in PanelsInScene(true))
            {
                if (panel.patchPanelId != id) continue;
                LivePanels[id] = panel;
                return panel;
            }
            return null;
        }

        public static void Message(string text)
        {
            try { StaticUIElements.instance?.AddMeesageInField(text); }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Warning($"Message '{text}': {e.Message}"); }
        }
    }
}
