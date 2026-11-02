using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace NextGenPatchPanels
{
    // Plain shapes for decoration (the closed box on a trunk panel, the straps on a Cat6 trunk): Unity's
    // built-in meshes, no collider (so they never block the player's clicks) and no shadows.
    internal static class Shapes
    {
        static Mesh _cube, _cylinder;

        static Mesh Cube => _cube != null ? _cube : _cube = PrimitiveMesh(PrimitiveType.Cube);

        // 1 wide, 2 tall, along y.
        public static Mesh Cylinder => _cylinder != null ? _cylinder : _cylinder = PrimitiveMesh(PrimitiveType.Cylinder);

        static Mesh PrimitiveMesh(PrimitiveType type)
        {
            var tmp = GameObject.CreatePrimitive(type);
            var mesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);
            return mesh;
        }

        // A box of the given size, centred at localPos in parent's space.
        public static GameObject Box(string name, Transform parent, Material material, Vector3 localPos, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            go.AddComponent<MeshFilter>().sharedMesh = Cube;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }
    }
}
