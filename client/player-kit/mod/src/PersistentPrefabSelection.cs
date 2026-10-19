using System;
using System.Collections.Generic;
using Alta.Networking;
using UnityEngine;

namespace PrefabEditorMod
{
    // Pick the persistent exported meshes without adding gameplay colliders.
    internal static class PersistentPrefabSelection
    {
        sealed class Geometry
        {
            public Vector3[] Vertices;
            public int[] Triangles;
        }

        static readonly Dictionary<Mesh, Geometry> cache = new Dictionary<Mesh, Geometry>();

        internal static NetworkEntity[] ActiveRoots()
        {
            List<NetworkEntity> roots = new List<NetworkEntity>();
            NetworkEntity[] entities = UnityEngine.Object.FindObjectsOfType<NetworkEntity>();
            for (int i = 0; i < entities.Length; i++)
            {
                NetworkEntity entity = entities[i];
                if (entity == null || entity.IsBeingDestroyed || entity.Identifier == 0) continue;
                NetworkPrefab prefab = entity.Prefab;
                if (prefab == null || (prefab.Hash != 21041u && prefab.Hash != 21042u)) continue;
                NetworkEntity root = entity.PrefabRoot != null ? entity.PrefabRoot : entity;
                if (root.gameObject.activeInHierarchy && !roots.Contains(root)) roots.Add(root);
            }
            return roots.ToArray();
        }

        internal static bool Raycast(NetworkEntity root, Ray ray, float maximum, out float distance)
        {
            distance = maximum;
            bool found = false;
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>();
            for (int f = 0; f < filters.Length; f++)
            {
                MeshFilter filter = filters[f];
                Mesh mesh = filter.sharedMesh;
                if (mesh == null || !filter.gameObject.activeInHierarchy) continue;
                Renderer renderer = filter.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled || !renderer.bounds.IntersectRay(ray)) continue;
                Geometry geometry;
                if (!cache.TryGetValue(mesh, out geometry))
                {
                    geometry = new Geometry { Vertices = mesh.vertices, Triangles = mesh.triangles };
                    cache.Add(mesh, geometry);
                }
                // Keep this direction unnormalized: its triangle parameter remains
                // a world distance even when the prefab has been scaled or rotated.
                Vector3 origin = filter.transform.InverseTransformPoint(ray.origin);
                Vector3 direction = filter.transform.InverseTransformVector(ray.direction);
                for (int i = 0; i + 2 < geometry.Triangles.Length; i += 3)
                {
                    float hit;
                    if (Triangle(origin, direction,
                        geometry.Vertices[geometry.Triangles[i]],
                        geometry.Vertices[geometry.Triangles[i + 1]],
                        geometry.Vertices[geometry.Triangles[i + 2]], out hit)
                        && hit <= distance)
                    {
                        distance = hit;
                        found = true;
                    }
                }
            }
            return found;
        }

        static bool Triangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0f;
            Vector3 edge1 = b - a, edge2 = c - a;
            Vector3 p = Vector3.Cross(direction, edge2);
            float determinant = Vector3.Dot(edge1, p);
            if (Math.Abs(determinant) < 0.00000001f) return false;
            float inverse = 1f / determinant;
            Vector3 offset = origin - a;
            float u = Vector3.Dot(offset, p) * inverse;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(offset, edge1);
            float v = Vector3.Dot(direction, q) * inverse;
            if (v < 0f || u + v > 1f) return false;
            distance = Vector3.Dot(edge2, q) * inverse;
            return distance >= 0f;
        }
    }
}
