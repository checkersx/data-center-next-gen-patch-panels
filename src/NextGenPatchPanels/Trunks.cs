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
    internal static class Trunks
    {
        public const string TrunkSuffix = "_MPO";
        const float ReconcileDelay = 0.5f;   // seconds; lets the game finish whatever registered the cable
        const float AfterLoadDelay = 2f;

        static readonly Dictionary<int, Link> ByCable = new();
        static readonly Dictionary<string, Link> ByPanel = new();
        static readonly Dictionary<string, PatchPanel> LivePanels = new();
        static readonly List<(Link link, bool linked)> Pending = new();
        static readonly HashSet<int> PendingRedraws = new();
        static float _reconcileAt = -1f;
        static bool _busy; // our own graph edits; don't treat them as the player's

        public static void Reset(string reason)
        {
            if (ByCable.Count > 0) NextGenPatchPanelsMod.Log.Msg($"Forgetting {ByCable.Count} trunk link(s) ({reason}).");
            ByCable.Clear();
            ByPanel.Clear();
            LivePanels.Clear();
            Pending.Clear();
            PendingRedraws.Clear();
            _reconcileAt = -1f;
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

        public static void InsertedInRackPostfix(PatchPanel __instance)
        {
            try
            {
                var t = Templates.Of(__instance);
                if (t == null) return;
                var id = __instance.patchPanelId;
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
                if (ByPanel.TryGetValue(id, out var link))
                {
                    SetPortIds(link, true);
                    Schedule(link, true, ReconcileDelay);
                }
            }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"InsertedInRackPostfix: {e}"); }
        }

        // ---- making and breaking links ----

        // Postfix on NetworkMap.RegisterCableConnection: a cable between two trunk ports links their panels.
        public static void RegisterCableConnectionPostfix(int __0, string __5, string __6)
        {
            if (_busy || !IsTrunkId(__5) || !IsTrunkId(__6)) return;
            try
            {
                var a = FindPanel(__5);
                var b = FindPanel(__6);
                if (a == null || b == null)
                {
                    NextGenPatchPanelsMod.Log.Warning($"Trunk cable {__0} between '{__5}' and '{__6}': {(a == null ? __5 : __6)} not found; not linked.");
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
            if (a.patchPanelId == b.patchPanelId) return;
            if (ByCable.TryGetValue(cableId, out var same) && IsSame(same, a, b)) return; // registered again, e.g. on load

            foreach (var p in new[] { a, b })
                if (ByPanel.TryGetValue(p.patchPanelId, out var old))
                {
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
            Schedule(link, true, IsLoading() ? AfterLoadDelay : ReconcileDelay);
            PendingRedraws.Add(cableId); // its trunk look; not from inside the game's cable code

            NextGenPatchPanelsMod.Log.Msg($"Trunk cable {cableId} links '{link.OwnerId}' and '{link.OtherId}' ({link.Ports} ports).");
            if (!IsLoading()) Message($"Trunk linked: {link.Ports} ports");
        }

        static void BreakLink(Link link, string reason)
        {
            ByCable.Remove(link.CableId);
            PendingRedraws.Remove(link.CableId);
            if (ByPanel.TryGetValue(link.OwnerId, out var l1) && l1 == link) ByPanel.Remove(link.OwnerId);
            if (ByPanel.TryGetValue(link.OtherId, out var l2) && l2 == link) ByPanel.Remove(link.OtherId);
            if (link.Other != null) SetPortIds(link, false);
            Schedule(link, false, ReconcileDelay);
            NextGenPatchPanelsMod.Log.Msg($"Trunk link '{link.OwnerId}' - '{link.OtherId}' broken ({reason}).");
            if (!IsLoading()) Message("Trunk unlinked");
        }

        static bool IsSame(Link l, PatchPanel a, PatchPanel b) =>
            (l.OwnerId == a.patchPanelId && l.OtherId == b.patchPanelId) || (l.OwnerId == b.patchPanelId && l.OtherId == a.patchPanelId);

        // The other panel's ports carry the owner's IDs while linked, their own otherwise.
        static void SetPortIds(Link link, bool linked)
        {
            var ports = Ports(link.Other);
            for (int k = 0; k < link.Ports && k < ports.Count; k++)
                ports[k].switchID = PortId(linked ? link.OwnerId : link.OtherId, k);
        }

        // ---- moving existing cables ----

        static void Schedule(Link link, bool linked, float delay)
        {
            Pending.RemoveAll(p => p.link == link);
            Pending.Add((link, linked));
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

            var work = Pending.ToList();
            Pending.Clear();
            // Live links are always re-checked: after a load, vanilla's own repairs may have renamed cable ends.
            foreach (var link in ByCable.Values)
                if (!work.Any(w => w.link == link)) work.Add((link, true));
            foreach (var (link, linked) in work)
            {
                try { Reconcile(link, linked); }
                catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"Moving cables for '{link.OwnerId}' - '{link.OtherId}': {e}"); }
            }
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

        // Every cable end on port k of either panel should name the owner's port k while linked; when not,
        // the ends on the other panel go back to its own port k.
        static void Reconcile(Link link, bool linked)
        {
            var wis = WaypointInitializationSystem.Instance;
            var map = NetworkMap.instance;
            if (wis == null || map == null) return;
            if (linked && (link.Owner == null || link.Other == null)) return;

            var ownerPorts = link.Owner == null ? null : Ports(link.Owner);
            var otherPorts = link.Other == null ? null : Ports(link.Other);

            var portOf = new Dictionary<string, int>();
            for (int k = 0; k < link.Ports; k++)
            {
                portOf[PortId(link.OwnerId, k)] = k;
                portOf[PortId(link.OtherId, k)] = k;
            }

            var changed = new List<WaypointInitializationSystem.CableInfo>();
            var speeds = new List<string>();
            var all = wis.GetAllCables(); // a copy; UpdateCableInfo writes back to the live table
            for (int c = 0; c < all.Count; c++)
            {
                var info = all[c];
                if (info.CableID == link.CableId) continue;
                bool dirty = false;
                for (int e = 0; e < 2; e++)
                {
                    var end = e == 0 ? info.StartPoint : info.EndPoint;
                    if (end.Type != CableLink.TypeOfLink.Switch || end.SwitchID == null || !portOf.TryGetValue(end.SwitchID, out int k)) continue;

                    if (speeds.Count < 6)
                    {
                        var far = e == 0 ? info.EndPoint : info.StartPoint;
                        speeds.Add($"cable {info.CableID} to '{(string.IsNullOrEmpty(far.SwitchID) ? far.ServerID : far.SwitchID)}' max {info.MaxSpeed}");
                    }

                    string want;
                    if (linked) want = PortId(link.OwnerId, k);
                    else
                    {
                        float toOwner = Distance(end.Position, ownerPorts, k);
                        float toOther = Distance(end.Position, otherPorts, k);
                        want = PortId(toOther < toOwner ? link.OtherId : link.OwnerId, k);
                    }
                    if (end.SwitchID == want) continue;

                    end.SwitchID = want;
                    if (e == 0) info.StartPoint = end;
                    else info.EndPoint = end;
                    dirty = true;
                }
                if (!dirty) continue;
                wis.UpdateCableInfo(info.CableID, info);
                changed.Add(info);
            }

            _busy = true;
            try
            {
                // NetworkMap keeps its own copy of each cable's ends; re-register the moved ones.
                foreach (var info in changed)
                {
                    map.RemoveCableConnection(info.CableID, true);
                    wis.RegisterCableInNetworkMap(info);
                }
                // Vanilla registers every port as a device when a panel is racked; keep that true.
                for (int k = 0; k < link.Ports; k++)
                {
                    EnsureDevice(map, PortId(link.OwnerId, k));
                    if (!linked) EnsureDevice(map, PortId(link.OtherId, k));
                }
            }
            finally { _busy = false; }

            wis.cablePairLookupDirty = true;
            wis.RequestRouteEvaluation();
            NextGenPatchPanelsMod.Log.Msg($"Trunk '{link.OwnerId}' - '{link.OtherId}' {(linked ? "linked" : "unlinked")}: moved {changed.Count} cable(s)" +
                                          (speeds.Count > 0 ? $"; {string.Join("; ", speeds)}" : "") + ".");
        }

        static float Distance(Vector3 pos, List<CableLink> ports, int k) =>
            ports == null || k >= ports.Count ? float.MaxValue : Vector3.Distance(pos, ports[k].transform.position);

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
                if (Templates.Of(__instance) == null || !ByPanel.TryGetValue(__instance.patchPanelId, out var link)) return;
                int k = Ports(__instance).FindIndex(p => p.Pointer == __0.Pointer);
                if (k < 0 || k >= link.Ports) return;
                var partner = link.OwnerId == __instance.patchPanelId ? link.Other : link.Owner;
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

        static PatchPanel FindPanel(string trunkId)
        {
            var id = trunkId.Substring(0, trunkId.Length - TrunkSuffix.Length);
            if (LivePanels.TryGetValue(id, out var p) && p != null && p.patchPanelId == id) return p;
            foreach (var panel in Resources.FindObjectsOfTypeAll<PatchPanel>())
            {
                if (panel == null || string.IsNullOrEmpty(panel.gameObject.scene.name) || panel.patchPanelId != id) continue;
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
