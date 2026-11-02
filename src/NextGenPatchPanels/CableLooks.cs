using System;
using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace NextGenPatchPanels
{
    // How trunk cables look. CablePositions.RedrawCable rebuilds a cable's "Cable_<id>" object from its path
    // with CreateTubeMesh, whose thickness is the cableWidth field. Around that call:
    //   MPO Trunk Fibre: one tube, thicker than a normal cable.
    //   Pre-terminated Cat6 Trunk: seven normal-width strands following the same path (one in the middle, six
    //   round it), with a strap round the bundle at every point it hangs from.
    // A laid cable doesn't record its spool, so a trunk cable is recognised by its link (both ends on trunk
    // ports), which also covers cables loaded from a save.
    internal static class CableLooks
    {
        const float MpoWidth = 1.6f;          // × a normal cable
        const float StrandSpacing = 1.9f;     // centre to centre, in strand radii (they touch, just)
        const float StrapLength = 0.02f;
        const float StrapOversize = 1.2f;     // × the bundle's diameter
        const float StrapShade = 0.55f;       // the strap's colour is the cable's, darker
        const string StrapName = "NextGenPatchPanels.Strap";

        static float _savedWidth = -1f;
        static bool? _fibre;
        static bool _radiusLogged;
        static readonly Dictionary<int, Material> StrapMaterials = new();

        public static void RedrawCablePrefix(CablePositions __instance, int __0)
        {
            _fibre = Trunks.IsFibreTrunk(__0);
            if (_fibre != true) return;
            _savedWidth = __instance.cableWidth;
            __instance.cableWidth = _savedWidth * MpoWidth;
        }

        public static void RedrawCablePostfix(CablePositions __instance, int __0)
        {
            try
            {
                if (_fibre == false) Bundle(__instance, __0);
            }
            catch (Exception e) { NextGenPatchPanelsMod.Log.Error($"Trunk cable {__0} look: {e}"); }
            finally
            {
                if (_savedWidth > 0f) __instance.cableWidth = _savedWidth;
                _savedWidth = -1f;
                _fibre = null;
            }
        }

        static void Bundle(CablePositions cp, int id)
        {
            if (cp.cableGameObjects == null || !cp.cableGameObjects.ContainsKey(id)) return;
            var go = cp.cableGameObjects[id];
            var mf = go == null ? null : go.GetComponent<MeshFilter>();
            var centre = mf == null ? null : mf.sharedMesh;
            var path = cp.GetCablePositions(id);
            if (centre == null || path == null || path.Count < 2) return;

            var pts = new List<Vector3>();
            for (int i = 0; i < path.Count; i++)
                if (pts.Count == 0 || (path[i] - pts[pts.Count - 1]).sqrMagnitude > 1e-8f) pts.Add(path[i]);
            if (pts.Count < 2) return;

            // The strand radius, measured off the tube the game just built (its first ring is round the first point).
            var cv = centre.vertices;
            float r = cv.Length > 0 ? Vector3.Distance(cv[0], pts[0]) : 0f;
            if (r < 0.0005f || r > 0.05f) r = cp.cableWidth * 0.5f;
            if (!_radiusLogged)
            {
                _radiusLogged = true;
                NextGenPatchPanelsMod.Log.Msg($"Cable tube: cableWidth {cp.cableWidth}, measured radius {r:F4}, {cv.Length} vertices for {pts.Count} points.");
            }

            var (tangents, normals, binormals) = Frames(pts);
            var meshes = new List<Mesh> { centre };
            for (int s = 0; s < 6; s++)
            {
                float a = s * Mathf.PI / 3f;
                var strand = new Il2CppSystem.Collections.Generic.List<Vector3>();
                for (int i = 0; i < pts.Count; i++)
                    strand.Add(pts[i] + (Mathf.Cos(a) * normals[i] + Mathf.Sin(a) * binormals[i]) * (r * StrandSpacing));
                meshes.Add(cp.CreateTubeMesh(strand));
            }

            mf.mesh = Combine(meshes);
            for (int i = 0; i < meshes.Count; i++) Object.Destroy(meshes[i]);

            var renderer = go.GetComponent<MeshRenderer>();
            float bundleDiameter = 2f * (r * StrandSpacing + r);
            AddStraps(cp, id, go, pts, tangents, bundleDiameter * StrapOversize, StrapMaterial(renderer == null ? null : renderer.sharedMaterial));
        }

        // Parallel-transport frames along the path, so the strands don't twist round each other.
        static (Vector3[] t, Vector3[] n, Vector3[] b) Frames(List<Vector3> pts)
        {
            int count = pts.Count;
            var t = new Vector3[count];
            var n = new Vector3[count];
            var b = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                var d = pts[Math.Min(i + 1, count - 1)] - pts[Math.Max(i - 1, 0)];
                t[i] = d.sqrMagnitude > 1e-10f ? d.normalized : (i > 0 ? t[i - 1] : Vector3.forward);
            }
            var up = Mathf.Abs(Vector3.Dot(t[0], Vector3.up)) > 0.9f ? Vector3.right : Vector3.up;
            n[0] = Vector3.Cross(t[0], up).normalized;
            for (int i = 1; i < count; i++)
            {
                var v = Quaternion.FromToRotation(t[i - 1], t[i]) * n[i - 1];
                v -= Vector3.Dot(v, t[i]) * t[i];
                n[i] = v.sqrMagnitude > 1e-10f ? v.normalized : n[i - 1];
            }
            for (int i = 0; i < count; i++) b[i] = Vector3.Cross(t[i], n[i]);
            return (t, n, b);
        }

        static Mesh Combine(List<Mesh> meshes)
        {
            int total = 0;
            foreach (var m in meshes) total += m.vertexCount;
            var verts = new Vector3[total];
            var uvs = new Vector2[total];
            var tris = new List<int>();
            int offset = 0;
            foreach (var m in meshes)
            {
                var v = m.vertices;
                var u = m.uv;
                var t = m.triangles;
                for (int j = 0; j < v.Length; j++)
                {
                    verts[offset + j] = v[j];
                    if (u.Length == v.Length) uvs[offset + j] = u[j];
                }
                for (int j = 0; j < t.Length; j++) tris.Add(t[j] + offset);
                offset += v.Length;
            }
            var mesh = new Mesh { name = "NextGenPatchPanels.Cat6Trunk" };
            if (total > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // A strap wherever the cable hangs: the points the player clicked between its two ends.
        static void AddStraps(CablePositions cp, int id, GameObject go, List<Vector3> pts, Vector3[] tangents, float diameter, Material material)
        {
            var raw = cp.GetRawCablePositions(id);
            if (raw == null || raw.Count < 3 || material == null) return;
            for (int r = 1; r < raw.Count - 1; r++)
            {
                int best = 0;
                float bestDist = float.MaxValue;
                for (int i = 0; i < pts.Count; i++)
                {
                    float d = (pts[i] - raw[r]).sqrMagnitude;
                    if (d < bestDist) { bestDist = d; best = i; }
                }
                var strap = new GameObject(StrapName);
                strap.transform.SetParent(go.transform, false);
                strap.transform.localPosition = pts[best];
                strap.transform.localRotation = Quaternion.FromToRotation(Vector3.up, tangents[best]);
                strap.transform.localScale = new Vector3(diameter, StrapLength / 2f, diameter); // the cylinder is 1 wide, 2 tall
                strap.AddComponent<MeshFilter>().sharedMesh = Shapes.Cylinder;
                var mr = strap.AddComponent<MeshRenderer>();
                mr.sharedMaterial = material;
                mr.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        static Material StrapMaterial(Material cable)
        {
            if (cable == null) return null;
            var colour = cable.HasProperty("_BaseColor") ? cable.GetColor("_BaseColor") : cable.color;
            int key = cable.GetInstanceID();
            if (StrapMaterials.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(cable) { name = cable.name + ".NextGenPatchPanels.Strap", hideFlags = HideFlags.DontUnloadUnusedAsset };
            var dark = new Color(colour.r * StrapShade, colour.g * StrapShade, colour.b * StrapShade, colour.a);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", dark);
            if (m.HasProperty("_Color")) m.SetColor("_Color", dark);
            StrapMaterials[key] = m;
            return m;
        }
    }
}
