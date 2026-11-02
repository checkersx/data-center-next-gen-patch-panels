using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NextGenPatchPanels
{
    // The game picks what you're looking at with a single Physics.Raycast (RayLookAt.HandleLookAtRay) that
    // stops at the first collider, ignoring only the "CableHolder" and "Ignore Raycast" layers, and then
    // needs that collider tagged "Interact" or "CablePoint". So a cable holder or port sitting inside
    // something else's collider can be seen but not picked.
    //
    // Rack slots (RackPosition) have colliders that are hit even when a device fills the slot ("position in
    // use"). Nothing needs them while you hold a cable spool, so for that time they go on "Ignore Raycast".
    // F8 logs everything along the look ray, to find whatever else gets in the way.
    internal static class LookAssist
    {
        const float ProbeDistance = 6f;

        static bool _hidden;
        static readonly List<(GameObject go, int layer)> Moved = new();
        static int _ignoreLayer = -1;

        public static void Update()
        {
            try
            {
                bool holdingSpool = PlayerManager.instance != null && PlayerManager.instance.objectInHand == PlayerManager.ObjectInHand.CableSpinner;
                bool want = holdingSpool && NextGenPatchPanelsMod.HideRackSlotsWhileCabling.Value;
                if (want != _hidden) SetSlotsHidden(want);

                var kb = Keyboard.current;
                if (kb != null && kb.f8Key.wasPressedThisFrame) Probe();
            }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"LookAssist: {e.Message}"); }
        }

        static void SetSlotsHidden(bool hide)
        {
            _hidden = hide;
            if (!hide)
            {
                foreach (var (go, layer) in Moved)
                    if (go != null) go.layer = layer;
                Moved.Clear();
                return;
            }
            if (_ignoreLayer < 0) _ignoreLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (_ignoreLayer < 0) return;
            foreach (var rp in Resources.FindObjectsOfTypeAll<RackPosition>())
            {
                if (rp == null || string.IsNullOrEmpty(rp.gameObject.scene.name)) continue;
                var go = rp.gameObject;
                // Only the slot's own collider: devices in the slot may be its children.
                if (go.layer == _ignoreLayer || go.GetComponent<Collider>() == null) continue;
                Moved.Add((go, go.layer));
                go.layer = _ignoreLayer;
            }
        }

        // Everything along the look ray, nearest first, with what the game's raycast would make of it.
        static void Probe()
        {
            var log = NextGenPatchPanelsMod.Log;
            var cam = Camera.main;
            if (cam == null) { log.Msg("F8: no camera."); return; }
            var ray = cam.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
            var hits = Physics.RaycastAll(ray, ProbeDistance).ToArray().OrderBy(h => h.distance).ToList();
            int holder = LayerMask.NameToLayer("CableHolder"), ignore = LayerMask.NameToLayer("Ignore Raycast");
            log.Msg($"F8 look ray from {Templates.V(ray.origin)} towards {Templates.V(ray.direction)}: {hits.Count} hit(s), holding {PlayerManager.instance?.objectInHand}, " +
                    $"rack slots {(_hidden ? "hidden" : "visible")}.");
            bool first = true;
            foreach (var h in hits.Take(12))
            {
                var c = h.collider;
                if (c == null) continue;
                var go = c.gameObject;
                bool skipped = go.layer == holder || go.layer == ignore;
                var link = go.GetComponent<CableLink>();
                string what = skipped ? "skipped by the game's ray" : first ? "<- the game picks this" : "behind";
                if (!skipped) first = false;
                log.Msg(string.Format(CultureInfo.InvariantCulture,
                    "  {0:F3} m  {1} '{2}'  tag {3}  layer {4}{5}{6}  {7}",
                    h.distance, c.GetIl2CppType().Name, Path(go.transform), go.tag, LayerMask.LayerToName(go.layer),
                    c.isTrigger ? "  trigger" : "", link == null ? "" : $"  CableLink end={link.isStartOrEnd} id='{link.switchID}'", what));
            }
        }

        static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 4; i++, t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
