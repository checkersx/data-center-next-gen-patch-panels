using System.Globalization;
using System.Linq;
using Il2Cpp;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NextGenPatchPanels
{
    // Cable mount points down the inside walls of every rack, so a trunk cable can run from a panel's trunk
    // port (which faces into the rack) up to the top and out, instead of cutting through the rack.
    //
    // The cable holders the game already has (the grey ones on the rack posts, the orange clips on the
    // ceiling) are CableLinks that aren't cable ends (isStartOrEnd false): clicking one while laying a cable
    // adds a bend point. These are copies of one of them. Every rack is built from MainGameManager.rackPrefab
    // (RackMount.InstantiateRack), so the mount points go on a copy of it, which takes its place. Rack saves
    // don't record a rack type and cables save their bend points as positions, so saves are unaffected.
    internal static class Racks
    {
        const string GroupName = "NextGenPatchPanels.MountPoints";
        const int Spacing = 6;                // rack units between mount points
        const float DeviceHalfWidth = 0.25f;  // devices (with their rack ears) span x ±0.25
        const float WallThickness = 0.02f;
        const float MountInset = 0.025f;      // a mount point's centre, in from the wall's collider
        const float ExitZ = 0.28f;            // the top pair sits towards the back of the rack (+z), where cables leave

        static GameObject _vanilla, _ours;
        static bool _failed;

        public static void EnsureMountPoints(string reason)
        {
            if (_failed || !NextGenPatchPanelsMod.MountPoints.Value) return;
            var mgm = MainGameManager.instance;
            var current = mgm == null ? null : mgm.rackPrefab;
            if (current == null) return;
            if (_ours != null && current.GetInstanceID() == _ours.GetInstanceID()) return;
            if (_vanilla != null && current.GetInstanceID() != _vanilla.GetInstanceID())
            {
                NextGenPatchPanelsMod.Log.Warning($"rackPrefab was replaced by something else ('{current.name}'); not adding mount points.");
                _failed = true;
                return;
            }
            if (_ours == null)
            {
                _vanilla = current;
                _ours = Build(current);
                if (_ours == null) { _failed = true; return; }
            }
            mgm.rackPrefab = _ours;
            NextGenPatchPanelsMod.Log.Msg($"Racks now come with cable mount points ({reason}).");
        }

        static GameObject Build(GameObject vanilla)
        {
            var log = NextGenPatchPanelsMod.Log;
            var go = Object.Instantiate(vanilla, Templates.Holder);
            go.name = vanilla.name;
            var root = go.transform;
            var positions = go.GetComponentsInChildren<RackPosition>(true)
                .OrderBy(p => root.InverseTransformPoint(p.transform.position).y).ToArray();
            var links = go.GetComponentsInChildren<CableLink>(true);
            if (positions.Length < 8)
            {
                log.Warning($"Rack '{vanilla.name}' has {positions.Length} rack positions; no mount points.");
                Object.Destroy(go);
                return null;
            }

            // Positions are worked out in a rack position's frame: the frame devices are placed in, with +z
            // the side their ports face.
            var frame = positions[0].transform;
            var bounds = BoundsIn(go, frame);

            // Mount points must sit in front of the side walls' colliders, or the look ray hits the wall
            // first. The walls: tall colliders outside the devices' width that cover the middle of the rack.
            float wall = float.MaxValue;
            foreach (var c in go.GetComponentsInChildren<Collider>(true))
            {
                if (c.GetComponent<CableLink>() != null || c.GetComponent<RackPosition>() != null) continue;
                var b = Templates.ColliderBounds(c, frame);
                if (b == null || b.Value.size.y < 1f || b.Value.min.z > 0f || b.Value.max.z < 0f) continue;
                if (b.Value.min.x > DeviceHalfWidth) wall = Mathf.Min(wall, b.Value.min.x);
                if (b.Value.max.x < -DeviceHalfWidth) wall = Mathf.Min(wall, -b.Value.max.x);
            }
            float x = wall < 1f
                ? wall - MountInset
                : (DeviceHalfWidth + Mathf.Min(bounds.max.x, -bounds.min.x) - WallThickness) / 2f;
            x = Mathf.Clamp(x, DeviceHalfWidth + 0.015f, 0.36f);

            var template = links.FirstOrDefault(l => !l.isStartOrEnd) ?? SceneHolder();
            if (template == null)
            {
                log.Warning("No cable holder found to copy; no mount points.");
                Object.Destroy(go);
                return null;
            }
            var templateRot = Quaternion.Inverse(frame.rotation) * template.transform.rotation;
            bool templateOnLeft = frame.InverseTransformPoint(template.transform.position).x < 0f;
            log.Msg($"Rack '{vanilla.name}': mount points copy '{template.name}', at x ±{x.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"(side walls at ±{(wall < 1f ? wall.ToString("F3", CultureInfo.InvariantCulture) : "?")}), every {Spacing} units.");

            var group = new GameObject(GroupName);
            group.transform.SetParent(root, false);
            int count = 0;
            foreach (float side in new[] { -1f, 1f })
            {
                // Facing the way the copied holder faces on its own side, mirrored on the other.
                var rot = (side < 0f) == templateOnLeft ? templateRot : Quaternion.AngleAxis(180f, Vector3.up) * templateRot;
                for (int i = 2; i < positions.Length; i += Spacing)
                    { Place(group.transform, template, positions[i].transform, new Vector3(side * x, 0f, 0f), rot); count++; }
                Place(group.transform, template, positions[positions.Length - 1].transform, new Vector3(side * x, 0f, ExitZ), rot);
                count++;
            }
            log.Msg($"Added {count} cable mount points to the rack.");
            return go;
        }

        static void Place(Transform group, CableLink template, Transform position, Vector3 local, Quaternion rot)
        {
            var copy = Object.Instantiate(template.gameObject, group);
            copy.name = "NextGenPatchPanels.MountPoint";
            copy.transform.position = position.TransformPoint(local);
            copy.transform.rotation = position.rotation * rot;
            var ts = template.transform.lossyScale;
            var gs = group.lossyScale;
            copy.transform.localScale = new Vector3(ts.x / gs.x, ts.y / gs.y, ts.z / gs.z);
            var link = copy.GetComponent<CableLink>();
            link.cableIDsOnLink = -1;
            link.isStartOrEnd = false;
            if (link.outlineEffect != null && !link.outlineEffect.transform.IsChildOf(copy.transform)) link.outlineEffect = null;
        }

        // A cable holder in the level (the orange ceiling clips), if the rack has none of its own.
        static CableLink SceneHolder()
        {
            foreach (var l in Resources.FindObjectsOfTypeAll<CableLink>())
            {
                if (l == null || l.isStartOrEnd || string.IsNullOrEmpty(l.gameObject.scene.name)) continue;
                if (l.parentServer != null || l.parentSwitch != null || l.parentPatchPanel != null) continue;
                NextGenPatchPanelsMod.Log.Msg($"Using the level's cable holder '{l.name}' (under '{l.transform.parent?.name}') as the mount point.");
                return l;
            }
            return null;
        }

        static Bounds BoundsIn(GameObject go, Transform frame)
        {
            Bounds? result = null;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                    var p = frame.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (result == null) result = new Bounds(p, Vector3.zero);
                    else { var r = result.Value; r.Encapsulate(p); result = r; }
                }
            }
            return result ?? new Bounds(Vector3.zero, new Vector3(0.6f, 2f, 1f));
        }

        static Vector3 Local(Transform root, Vector3 world) => root.InverseTransformPoint(world);
    }
}
