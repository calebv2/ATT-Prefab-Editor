using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace PrefabEditorMod
{
    // Self-contained OBJ and binary GLB mesh importer. Kept in the editor assembly
    // so model import does not require the separate MeshLoader mod.
    static class IntegratedModelImporter
    {
        [Serializable] sealed class GlbRoot { public GlbAccessor[] accessors; public GlbBufferView[] bufferViews; public GlbMesh[] meshes; }
        [Serializable] sealed class GlbMesh { public GlbPrimitive[] primitives; }
        [Serializable] sealed class GlbPrimitive { public GlbAttributes attributes; public int indices = -1; }
        [Serializable] sealed class GlbAttributes { public int POSITION = -1; public int NORMAL = -1; public int TEXCOORD_0 = -1; }
        [Serializable] sealed class GlbAccessor { public int bufferView = -1; public int byteOffset; public int componentType; public int count; public string type; }
        [Serializable] sealed class GlbBufferView { public int byteOffset; public int byteLength; public int byteStride; }

        public static Mesh Load(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".obj") return LoadObj(path);
            if (extension == ".glb") return LoadGlb(path);
            throw new InvalidDataException("Choose an .obj or binary .glb model file.");
        }

        public static string FindObjDiffuseTexturePath(string objPath)
        {
            if (!string.Equals(Path.GetExtension(objPath), ".obj", StringComparison.OrdinalIgnoreCase)) return null;
            string directory = Path.GetDirectoryName(Path.GetFullPath(objPath));
            string library = null;
            string usedMaterial = null;
            foreach (string rawLine in File.ReadLines(objPath))
            {
                string line = rawLine.Trim();
                if (library == null && line.StartsWith("mtllib ", StringComparison.Ordinal)) library = line.Substring(6).Trim();
                else if (usedMaterial == null && line.StartsWith("usemtl ", StringComparison.Ordinal)) usedMaterial = line.Substring(6).Trim();
                if (library != null && usedMaterial != null) break;
            }

            string mtlPath = library == null ? null : Path.Combine(directory, library);
            if (mtlPath == null || !File.Exists(mtlPath)) mtlPath = Path.ChangeExtension(objPath, ".mtl");
            if (!File.Exists(mtlPath)) return null;

            string material = null;
            foreach (string rawLine in File.ReadLines(mtlPath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("newmtl ", StringComparison.Ordinal)) material = line.Substring(6).Trim();
                else if (line.StartsWith("map_Kd ", StringComparison.Ordinal)
                    && (usedMaterial == null || string.Equals(material, usedMaterial, StringComparison.Ordinal)))
                {
                    string image = line.Substring(6).Trim();
                    if (image.StartsWith("-", StringComparison.Ordinal))
                    {
                        string[] parts = image.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        image = parts[parts.Length - 1];
                    }
                    string imagePath = Path.IsPathRooted(image) ? image : Path.Combine(Path.GetDirectoryName(mtlPath), image);
                    if (File.Exists(imagePath)) return imagePath;
                }
            }
            return null;
        }

        static Mesh LoadObj(string path)
        {
            List<Vector3> positions = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector3> vertices = new List<Vector3>();
            List<Vector2> finalUvs = new List<Vector2>();
            List<Vector3> finalNormals = new List<Vector3>();
            List<int> triangles = new List<int>();
            bool hasUvs = false, hasNormals = false;
            foreach (string rawLine in File.ReadLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                if (parts[0] == "v" && parts.Length >= 4)
                    positions.Add(new Vector3(ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3])));
                else if (parts[0] == "vt" && parts.Length >= 2)
                {
                    hasUvs = true;
                    uvs.Add(new Vector2(ParseFloat(parts[1]), parts.Length > 2 ? ParseFloat(parts[2]) : 0f));
                }
                else if (parts[0] == "vn" && parts.Length >= 4)
                {
                    hasNormals = true;
                    normals.Add(new Vector3(ParseFloat(parts[1]), ParseFloat(parts[2]), ParseFloat(parts[3])));
                }
                else if (parts[0] == "f" && parts.Length >= 4)
                {
                    int first = AddObjVertex(parts[1], positions, uvs, normals, vertices, finalUvs, finalNormals);
                    for (int i = 2; i < parts.Length - 1; i++)
                    {
                        triangles.Add(first);
                        triangles.Add(AddObjVertex(parts[i], positions, uvs, normals, vertices, finalUvs, finalNormals));
                        triangles.Add(AddObjVertex(parts[i + 1], positions, uvs, normals, vertices, finalUvs, finalNormals));
                    }
                }
            }
            if (vertices.Count == 0 || triangles.Count == 0) throw new InvalidDataException("OBJ has no usable vertices and faces.");
            Mesh mesh = NewMesh(Path.GetFileNameWithoutExtension(path), vertices.Count);
            mesh.SetVertices(vertices);
            if (hasUvs) mesh.SetUVs(0, finalUvs);
            mesh.SetTriangles(triangles, 0);
            if (hasNormals) mesh.SetNormals(finalNormals); else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static int AddObjVertex(string token, List<Vector3> positions, List<Vector2> uvs, List<Vector3> normals,
            List<Vector3> vertices, List<Vector2> finalUvs, List<Vector3> finalNormals)
        {
            string[] indices = token.Split('/');
            int p = ResolveObjIndex(indices[0], positions.Count);
            vertices.Add(positions[p]);
            Vector2 uv = Vector2.zero;
            if (indices.Length > 1 && indices[1].Length > 0) uv = uvs[ResolveObjIndex(indices[1], uvs.Count)];
            finalUvs.Add(uv);
            Vector3 normal = Vector3.zero;
            if (indices.Length > 2 && indices[2].Length > 0) normal = normals[ResolveObjIndex(indices[2], normals.Count)];
            finalNormals.Add(normal);
            return vertices.Count - 1;
        }

        static int ResolveObjIndex(string value, int count)
        {
            int index = int.Parse(value, CultureInfo.InvariantCulture);
            return index > 0 ? index - 1 : count + index;
        }

        static float ParseFloat(string value) { return float.Parse(value, CultureInfo.InvariantCulture); }

        static Mesh LoadGlb(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            string json = null;
            byte[] binary = null;
            using (MemoryStream stream = new MemoryStream(file))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt32() != 1179937895u) throw new InvalidDataException("Not a valid binary GLB file.");
                reader.ReadUInt32();
                uint totalLength = reader.ReadUInt32();
                while (stream.Position + 8 <= totalLength && stream.Position + 8 <= stream.Length)
                {
                    uint length = reader.ReadUInt32();
                    uint kind = reader.ReadUInt32();
                    if (length > stream.Length - stream.Position) throw new InvalidDataException("GLB chunk extends past the end of the file.");
                    byte[] chunk = reader.ReadBytes((int)length);
                    if (kind == 1313821514u) json = Encoding.UTF8.GetString(chunk);
                    else if (kind == 5130562u) binary = chunk;
                }
            }
            if (json == null || binary == null) throw new InvalidDataException("GLB must contain JSON and binary chunks.");
            GlbRoot root = JsonUtility.FromJson<GlbRoot>(json);
            if (root == null || root.meshes == null || root.meshes.Length == 0) throw new InvalidDataException("GLB contains no mesh data.");
            List<Vector3> vertices = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector2> uvs = new List<Vector2>();
            List<int> triangles = new List<int>();
            bool hasNormals = false, hasUvs = false;
            for (int m = 0; m < root.meshes.Length; m++)
            {
                GlbPrimitive[] primitives = root.meshes[m].primitives;
                if (primitives == null) continue;
                for (int p = 0; p < primitives.Length; p++)
                {
                    GlbPrimitive primitive = primitives[p];
                    if (primitive.attributes == null || primitive.attributes.POSITION < 0) continue;
                    List<float[]> points = ReadAccessor(root, binary, primitive.attributes.POSITION);
                    int vertexBase = vertices.Count;
                    for (int i = 0; i < points.Count; i++)
                    {
                        float[] v = points[i];
                        if (v.Length < 3) throw new InvalidDataException("GLB POSITION accessor is not VEC3.");
                        vertices.Add(new Vector3(v[0], v[1], v[2]));
                    }
                    if (primitive.attributes.NORMAL >= 0)
                    {
                        List<float[]> values = ReadAccessor(root, binary, primitive.attributes.NORMAL);
                        for (int i = 0; i < values.Count; i++) normals.Add(new Vector3(values[i][0], values[i][1], values[i][2]));
                        hasNormals = true;
                    }
                    else for (int i = 0; i < points.Count; i++) normals.Add(Vector3.zero);
                    if (primitive.attributes.TEXCOORD_0 >= 0)
                    {
                        List<float[]> values = ReadAccessor(root, binary, primitive.attributes.TEXCOORD_0);
                        for (int i = 0; i < values.Count; i++) uvs.Add(new Vector2(values[i][0], values[i][1]));
                        hasUvs = true;
                    }
                    else for (int i = 0; i < points.Count; i++) uvs.Add(Vector2.zero);
                    List<int> indices = primitive.indices >= 0 ? ReadIndices(root, binary, primitive.indices) : Sequential(points.Count);
                    for (int i = 0; i + 2 < indices.Count; i += 3)
                    {
                        triangles.Add(vertexBase + indices[i]);
                        triangles.Add(vertexBase + indices[i + 1]);
                        triangles.Add(vertexBase + indices[i + 2]);
                    }
                }
            }
            if (vertices.Count == 0 || triangles.Count == 0) throw new InvalidDataException("GLB mesh has no triangle geometry.");
            Mesh mesh = NewMesh(Path.GetFileNameWithoutExtension(path), vertices.Count);
            mesh.SetVertices(vertices);
            if (hasUvs) mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            if (hasNormals) mesh.SetNormals(normals); else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh NewMesh(string name, int count)
        {
            Mesh mesh = new Mesh();
            mesh.name = name;
            if (count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            return mesh;
        }

        static List<int> Sequential(int count)
        {
            List<int> indices = new List<int>(count);
            for (int i = 0; i < count; i++) indices.Add(i);
            return indices;
        }

        static List<int> ReadIndices(GlbRoot root, byte[] data, int index)
        {
            GlbAccessor accessor = root.accessors[index];
            List<int> output = new List<int>(accessor.count);
            foreach (float[] item in ReadAccessor(root, data, index)) output.Add((int)item[0]);
            return output;
        }

        static List<float[]> ReadAccessor(GlbRoot root, byte[] data, int index)
        {
            if (index < 0 || index >= root.accessors.Length) throw new InvalidDataException("GLB accessor index is invalid.");
            GlbAccessor accessor = root.accessors[index];
            if (accessor.bufferView < 0 || accessor.bufferView >= root.bufferViews.Length) throw new InvalidDataException("GLB accessor has no binary buffer view.");
            GlbBufferView view = root.bufferViews[accessor.bufferView];
            int componentSize = ComponentSize(accessor.componentType);
            int components = ComponentCount(accessor.type);
            int stride = view.byteStride > 0 ? view.byteStride : componentSize * components;
            int start = view.byteOffset + accessor.byteOffset;
            List<float[]> output = new List<float[]>(accessor.count);
            for (int i = 0; i < accessor.count; i++)
            {
                int offset = start + i * stride;
                float[] values = new float[components];
                for (int c = 0; c < components; c++) values[c] = ReadNumber(data, offset + c * componentSize, accessor.componentType);
                output.Add(values);
            }
            return output;
        }

        static float ReadNumber(byte[] data, int offset, int type)
        {
            switch (type)
            {
                case 5120: return (sbyte)data[offset];
                case 5121: return data[offset];
                case 5122: return BitConverter.ToInt16(data, offset);
                case 5123: return BitConverter.ToUInt16(data, offset);
                case 5125: return BitConverter.ToUInt32(data, offset);
                case 5126: return BitConverter.ToSingle(data, offset);
                default: throw new InvalidDataException("Unsupported GLB component type " + type + ".");
            }
        }

        static int ComponentSize(int type)
        {
            switch (type) { case 5120: case 5121: return 1; case 5122: case 5123: return 2; case 5125: case 5126: return 4; default: throw new InvalidDataException("Unsupported GLB component type " + type + "."); }
        }

        static int ComponentCount(string type)
        {
            if (type == "SCALAR") return 1;
            if (type == "VEC2") return 2;
            if (type == "VEC3") return 3;
            if (type == "VEC4") return 4;
            throw new InvalidDataException("Unsupported GLB accessor type " + type + ".");
        }
    }
}
