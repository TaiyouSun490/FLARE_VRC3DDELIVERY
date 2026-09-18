using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public static class FlareGimmickExporter
    {
        [MenuItem("Tools/FLARE/Export Selected Gimmick GLB...")]
        public static void ExportSelected()
        {
            if (Selection.activeGameObject == null) { EditorUtility.DisplayDialog("FLARE", "Select the exhibit root first.", "OK"); return; }
            string path = EditorUtility.SaveFilePanel("Export declarative GLB", "", Selection.activeGameObject.name, "glb");
            if (string.IsNullOrEmpty(path)) return;
            try { File.WriteAllBytes(path, Export(Selection.activeGameObject)); AssetDatabase.Refresh(); }
            catch (Exception e) { Debug.LogException(e); EditorUtility.DisplayDialog("FLARE export failed", e.Message, "OK"); }
        }

        [MenuItem("Tools/FLARE/Prepare Existing GLB Textures...")]
        public static void PrepareSelected()
        {
            string path = EditorUtility.OpenFilePanel("Select GLB", "", "glb");
            if (string.IsNullOrEmpty(path)) return;
            string output = EditorUtility.SaveFilePanel("Save runtime-prepared GLB", Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "-flare", "glb");
            if (string.IsNullOrEmpty(output)) return;
            try { File.WriteAllBytes(output, Prepare(File.ReadAllBytes(path))); AssetDatabase.Refresh(); }
            catch (Exception e) { Debug.LogException(e); EditorUtility.DisplayDialog("FLARE preparation failed", e.Message, "OK"); }
        }

        public static byte[] Export(GameObject root)
        {
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            if (transforms.Length > 16) throw new InvalidDataException("Default runtime allows at most 16 nodes.");
            if (root.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                throw new NotSupportedException("This static GLB profile does not support SkinnedMeshRenderer. Existing VAT/RAC2 remains a separate workflow.");
            var ids = new Dictionary<Transform, string>();
            var unique = new HashSet<string>();
            foreach (Transform t in transforms)
            {
                var definition = t.GetComponent<FlareGimmickDefinition>();
                string id = definition != null && !string.IsNullOrWhiteSpace(definition.GimmickId) ? definition.GimmickId : "node_" + ids.Count;
                if (id.Length > 64 || !unique.Add(id)) throw new InvalidDataException("Duplicate or too-long gimmick ID: " + id);
                ids.Add(t, id);
            }
            JObject document = NewDocument();
            var nodes = (JArray)document["nodes"]; var meshes = (JArray)document["meshes"];
            var materials = (JArray)document["materials"]; var accessors = (JArray)document["accessors"];
            var views = (JArray)document["bufferViews"]; var rawTextures = (JArray)document["extras"]["flare_rgba_textures"];
            int totalVertices = 0, actionCount = 0;
            using (var bin = new MemoryStream())
            using (var writer = new BinaryWriter(bin))
            {
                foreach (Transform t in transforms)
                {
                    // Root is the export origin. Child TRS and pivots remain intact.
                    Vector3 p = t == root.transform ? Vector3.zero : t.localPosition;
                    Quaternion q = t == root.transform ? Quaternion.identity : t.localRotation;
                    Vector3 s = t == root.transform ? Vector3.one : t.localScale;
                    var node = new JObject { ["name"] = t.name, ["translation"] = XYZ(new Vector3(p.x, p.y, -p.z)),
                        ["rotation"] = new JArray(-q.x, -q.y, q.z, q.w), ["scale"] = XYZ(s) };
                    if (t.childCount > 0)
                    {
                        var children = new JArray();
                        for (int c = 0; c < t.childCount; c++) children.Add(Array.IndexOf(transforms, t.GetChild(c)));
                        node["children"] = children;
                    }
                    var gimmick = new JObject { ["gimmickId"] = ids[t], ["initialActive"] = t.gameObject.activeSelf };
                    node["extras"] = new JObject { ["vrc_gimmick"] = gimmick };
                    BoxCollider box = t.GetComponent<BoxCollider>();
                    if (box != null && box.enabled)
                    {
                        if (box.isTrigger) throw new NotSupportedException("Trigger events are outside this MVP; use a non-trigger BoxCollider.");
                        gimmick["collider"] = new JObject { ["type"] = "box", ["size"] = XYZ(box.size), ["center"] = XYZ(box.center) };
                    }
                    FlareGimmickDefinition definition = t.GetComponent<FlareGimmickDefinition>();
                    if (definition != null)
                    {
                        if (definition.On == "interact" && (box == null || !box.enabled))
                            throw new InvalidDataException(t.name + ": Interact needs an enabled BoxCollider.");
                        gimmick["on"] = definition.On;
                        var actions = new JArray(); gimmick["actions"] = actions;
                        foreach (FlareDeclarativeAction a in definition.Actions)
                        {
                            if (++actionCount > 64) throw new InvalidDataException("Default action limit is 64.");
                            Transform target = a.Target != null ? a.Target : t;
                            if (!ids.ContainsKey(target)) throw new InvalidDataException("Action target is outside the exported root.");
                            actions.Add(new JObject { ["type"] = a.Type.ToString(), ["target"] = ids[target],
                                ["axis"] = a.Axis.ToString(), ["value"] = a.Type == FlareActionKind.move ? (JToken)XYZ(a.Move) : new JValue(a.Value),
                                ["duration"] = a.Duration, ["easing"] = a.Easing.ToString(), ["clip"] = a.ClipId, ["event"] = a.Event });
                        }
                    }
                    MeshFilter filter = t.GetComponent<MeshFilter>(); MeshRenderer renderer = t.GetComponent<MeshRenderer>();
                    if (filter != null && filter.sharedMesh != null && renderer != null)
                    {
                        Mesh mesh = filter.sharedMesh;
                        if (mesh.subMeshCount != 1 || mesh.vertexCount > 2048 || mesh.triangles.Length > 12288)
                            throw new InvalidDataException(t.name + ": split into single-submesh nodes with <=2048 vertices / 12288 indices.");
                        totalVertices += mesh.vertexCount;
                        if (totalVertices > 16000) throw new InvalidDataException("Default total vertex limit is 16000.");
                        var attrs = new JObject();
                        Vector3[] vertices = mesh.vertices;
                        attrs["POSITION"] = WriteAccessor(writer, views, accessors, vertices.Length, "VEC3", 5126, () => {
                            foreach (Vector3 v in vertices) { writer.Write(v.x); writer.Write(v.y); writer.Write(-v.z); }
                        });
                        Vector3 low = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                        Vector3 high = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                        foreach (Vector3 v in vertices)
                        {
                            Vector3 point = new Vector3(v.x, v.y, -v.z);
                            low = Vector3.Min(low, point); high = Vector3.Max(high, point);
                        }
                        accessors[(int)attrs["POSITION"]]["min"] = XYZ(low);
                        accessors[(int)attrs["POSITION"]]["max"] = XYZ(high);
                        Vector3[] normals = mesh.normals;
                        if (normals.Length == vertices.Length) attrs["NORMAL"] = WriteAccessor(writer, views, accessors, normals.Length, "VEC3", 5126, () => {
                            foreach (Vector3 v in normals) { writer.Write(v.x); writer.Write(v.y); writer.Write(-v.z); }
                        });
                        Vector2[] uv = mesh.uv;
                        if (uv.Length == vertices.Length) attrs["TEXCOORD_0"] = WriteAccessor(writer, views, accessors, uv.Length, "VEC2", 5126, () => {
                            foreach (Vector2 v in uv) { writer.Write(v.x); writer.Write(1f - v.y); }
                        });
                        int[] triangles = mesh.triangles;
                        int indices = WriteAccessor(writer, views, accessors, triangles.Length, "SCALAR", 5123, () => {
                            for (int i = 0; i < triangles.Length; i += 3) { writer.Write((ushort)triangles[i]); writer.Write((ushort)triangles[i + 2]); writer.Write((ushort)triangles[i + 1]); }
                        });
                        Material material = renderer.sharedMaterial;
                        Color color = material != null && material.HasProperty("_Color") ? material.color : Color.white;
                        var pbr = new JObject { ["baseColorFactor"] = new JArray(color.r, color.g, color.b, color.a), ["metallicFactor"] = 0 };
                        Texture texture = material != null && material.HasProperty("_MainTex") ? material.mainTexture : null;
                        if (texture != null)
                        {
                            if (rawTextures.Count >= 4) throw new InvalidDataException("Default texture limit is 4.");
                            int id = rawTextures.Count;
                            Texture2D readable = ReadTexture(texture);
                            try
                            {
                                int rawView = AddBytes(writer, views, readable.GetRawTextureData());
                                rawTextures.Add(new JObject { ["width"] = readable.width, ["height"] = readable.height, ["bufferView"] = rawView });
                                int imageView = AddBytes(writer, views, readable.EncodeToPNG());
                                ((JArray)document["images"]).Add(new JObject { ["bufferView"] = imageView, ["mimeType"] = "image/png" });
                                ((JArray)document["textures"]).Add(new JObject { ["source"] = id });
                                pbr["baseColorTexture"] = new JObject { ["index"] = id };
                            }
                            finally { UnityEngine.Object.DestroyImmediate(readable); }
                        }
                        if (materials.Count >= 8) throw new InvalidDataException("Default material limit is 8.");
                        int matIndex = materials.Count; materials.Add(new JObject { ["pbrMetallicRoughness"] = pbr });
                        node["mesh"] = meshes.Count;
                        meshes.Add(new JObject { ["primitives"] = new JArray(new JObject { ["attributes"] = attrs, ["indices"] = indices, ["material"] = matIndex, ["mode"] = 4 }) });
                    }
                    nodes.Add(node);
                }
                document["scenes"] = new JArray(new JObject { ["nodes"] = new JArray(0) }); document["scene"] = 0;
                return Container(document, bin.ToArray());
            }
        }

        public static byte[] Prepare(byte[] glb)
        {
            if (glb.Length < 28 || glb.Length > 10000000 || BitConverter.ToUInt32(glb, 0) != 0x46546c67u ||
                BitConverter.ToUInt32(glb, 4) != 2 || BitConverter.ToUInt32(glb, 8) != glb.Length)
                throw new InvalidDataException("Invalid GLB container.");
            int jsonLength = checked((int)BitConverter.ToUInt32(glb, 12));
            if (jsonLength > 262144 || jsonLength < 2 || 28L + jsonLength > glb.Length || BitConverter.ToUInt32(glb, 16) != 0x4e4f534au)
                throw new InvalidDataException("Invalid JSON chunk.");
            int header = 20 + jsonLength, binLength = checked((int)BitConverter.ToUInt32(glb, header));
            if (header + 8L + binLength != glb.Length || BitConverter.ToUInt32(glb, header + 4) != 0x004e4942u)
                throw new InvalidDataException("Invalid BIN chunk.");
            JObject doc = JObject.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength));
            var views = (JArray)doc["bufferViews"];
            var textures = (JArray)doc["textures"] ?? new JArray(); var images = (JArray)doc["images"] ?? new JArray();
            if (textures.Count > 4) throw new InvalidDataException("Default texture limit is 4.");
            var prepared = new JArray();
            if (doc["extras"] == null) doc["extras"] = new JObject();
            doc["extras"]["flare_rgba_textures"] = prepared;
            using (var bin = new MemoryStream())
            using (var writer = new BinaryWriter(bin))
            {
                writer.Write(glb, header + 8, binLength);
                foreach (JObject texture in textures)
                {
                    int source = (int?)texture["source"] ?? -1;
                    if (source < 0 || source >= images.Count) throw new InvalidDataException("Invalid texture source.");
                    JObject image = (JObject)images[source];
                    int viewIndex = (int?)image["bufferView"] ?? -1;
                    if (image["uri"] != null || viewIndex < 0 || viewIndex >= views.Count) throw new InvalidDataException("Only embedded PNG/JPEG is supported.");
                    JObject view = (JObject)views[viewIndex];
                    int offset = (int?)view["byteOffset"] ?? 0, length = (int)view["byteLength"];
                    if ((int)view["buffer"] != 0 || offset < 0 || length < 0 || (long)offset + length > binLength) throw new InvalidDataException("Image exceeds BIN.");
                    byte[] encoded = new byte[length]; Buffer.BlockCopy(glb, header + 8 + offset, encoded, 0, length);
                    var decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    try
                    {
                        if (!ImageConversion.LoadImage(decoded, encoded) || decoded.width > 512 || decoded.height > 512)
                            throw new InvalidDataException("Image decoding failed or dimension exceeds 512. Resize before preparation.");
                        Texture2D rgba = ReadTexture(decoded);
                        try { prepared.Add(new JObject { ["width"] = rgba.width, ["height"] = rgba.height, ["bufferView"] = AddBytes(writer, views, rgba.GetRawTextureData()) }); }
                        finally { UnityEngine.Object.DestroyImmediate(rgba); }
                    }
                    finally { UnityEngine.Object.DestroyImmediate(decoded); }
                }
                return Container(doc, bin.ToArray());
            }
        }

        private static Texture2D ReadTexture(Texture source)
        {
            if (source.width > 512 || source.height > 512) throw new InvalidDataException("Texture dimensions exceed 512. Resize before export.");
            RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt); RenderTexture.active = rt;
                var output = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                output.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); output.Apply(); return output;
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); }
        }
        private static int WriteAccessor(BinaryWriter writer, JArray views, JArray accessors, int count, string type, int component, Action write)
        {
            while (writer.BaseStream.Position % 4 != 0) writer.Write((byte)0);
            int offset = (int)writer.BaseStream.Position; write();
            int view = views.Count; views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = (int)writer.BaseStream.Position - offset });
            int result = accessors.Count; accessors.Add(new JObject { ["bufferView"] = view, ["componentType"] = component, ["count"] = count, ["type"] = type }); return result;
        }
        private static int AddBytes(BinaryWriter writer, JArray views, byte[] bytes)
        {
            while (writer.BaseStream.Position % 4 != 0) writer.Write((byte)0);
            int offset = (int)writer.BaseStream.Position; writer.Write(bytes);
            int view = views.Count; views.Add(new JObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = bytes.Length }); return view;
        }
        public static byte[] Container(JObject doc, byte[] bin)
        {
            doc["buffers"] = new JArray(new JObject { ["byteLength"] = bin.Length });
            byte[] json = Encoding.UTF8.GetBytes(doc.ToString(Formatting.None));
            int jlen = (json.Length + 3) & ~3, blen = (bin.Length + 3) & ~3;
            if (jlen > 262144 || 28L + jlen + blen > 10000000) throw new InvalidDataException("Prepared GLB exceeds runtime size limits.");
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output))
            {
                writer.Write(0x46546c67u); writer.Write(2u); writer.Write(28 + jlen + blen);
                writer.Write(jlen); writer.Write(0x4e4f534au); writer.Write(json);
                for (int p = json.Length; p < jlen; p++) writer.Write((byte)32);
                writer.Write(blen); writer.Write(0x004e4942u); writer.Write(bin);
                for (int p = bin.Length; p < blen; p++) writer.Write((byte)0);
                return output.ToArray();
            }
        }
        private static JObject NewDocument()
        {
            return new JObject { ["asset"] = new JObject { ["version"] = "2.0", ["generator"] = "FLARE declarative MVP" },
                ["nodes"] = new JArray(), ["meshes"] = new JArray(), ["materials"] = new JArray(), ["accessors"] = new JArray(),
                ["bufferViews"] = new JArray(), ["images"] = new JArray(), ["textures"] = new JArray(),
                ["extras"] = new JObject { ["flare_rgba_textures"] = new JArray() } };
        }
        private static JArray XYZ(Vector3 v) { return new JArray(v.x, v.y, v.z); }
    }
}
