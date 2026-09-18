using System;
using System.Text;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;

namespace AvatarCatalog.Remote
{
    /// <summary>Bounded static GLB profile with incremental mesh decoding; no external resources.</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public sealed class FlareGlbSceneLoader : UdonSharpBehaviour
    {
        public GameObject RuntimeNodePrefab;
        public Transform DisplayRoot;
        public Material MaterialTemplate;
        public FlareGimmickInterpreter Interpreter;
        [Range(1, 32)] public int MaximumNodes = 16;
        [Range(3, 4096)] public int MaximumVerticesPerNode = 2048;
        [Range(3, 40000)] public int MaximumVertices = 16000;
        [Range(1024, 10000000)] public int MaximumBytes = 10000000;
        [Range(1, 16)] public int MaximumMaterials = 8;
        [Range(1, 4)] public int MaximumTextures = 4;
        [Range(1, 512)] public int MaximumTextureDimension = 512;
        [Range(16, 512)] public int MeshElementsPerFrame = 128;
        public int PeakDecodeElements;
        public int DecodeContinuations;
        [HideInInspector] public byte[] InputBytes;
        public int Status; // 0 idle, 1 parsing, 2 ready, 3 error
        public string LastError = "";
        public int LoadedVertices;
        public FlareRuntimeNode[] Nodes;
        private Mesh[] _meshes;
        private Material[] _materials;
        private Texture2D[] _textures;
        private byte[] _bytes;
        private DataList _definitions, _meshDefs, _accessors, _views, _materialDefs, _textureDefs;
        private int _bin, _binLength, _next;
        private int[] _parents;
        private bool[] _initialActive;
        private int _readStart, _readStride, _readCount, _readComponent;
        private bool _scheduled;
        private int _meshStage, _meshNode, _meshCount, _cursor;
        private DataDictionary _primitive, _attributes;
        private Vector3[] _positions, _normals;
        private Vector2[] _uv;
        private int[] _indices;

        public void Clear()
        {
            if (Interpreter != null) Interpreter.Clear();
            if (Nodes != null) for (int n = 0; n < Nodes.Length; n++) if (Nodes[n] != null)
            { Nodes[n].gameObject.SetActive(false); Destroy(Nodes[n].gameObject); }
            if (_meshes != null) for (int n = 0; n < _meshes.Length; n++) if (_meshes[n] != null) Destroy(_meshes[n]);
            if (_materials != null) for (int n = 0; n < _materials.Length; n++) if (_materials[n] != null) Destroy(_materials[n]);
            if (_textures != null) for (int n = 0; n < _textures.Length; n++) if (_textures[n] != null) Destroy(_textures[n]);
            Nodes = null; _meshes = null; _materials = null; _textures = null;
            InputBytes = null; _bytes = null; Status = 0; LoadedVertices = 0;
            _definitions = null; _meshDefs = null; _accessors = null; _views = null;
            _materialDefs = null; _textureDefs = null;
            ReleaseMeshScratch(); _parents = null; _initialActive = null;
            PeakDecodeElements = 0; DecodeContinuations = 0;
        }

        public void LoadInput()
        {
            byte[] value = InputBytes;
            Clear(); LastError = "";
            if (value == null || value.Length < 28 || value.Length > Mathf.Clamp(MaximumBytes, 1024, 10000000))
            { Fail("GLB byte size exceeds policy or is truncated."); return; }
            if (RuntimeNodePrefab == null || DisplayRoot == null || MaterialTemplate == null || Interpreter == null)
            { Fail("Runtime prefab, display, material or interpreter is missing."); return; }
            _bytes = value;
            if (U32(0) != 0x46546c67u || U32(4) != 2u || U32(8) != (uint)value.Length || U32(16) != 0x4e4f534au)
            { Fail("Invalid GLB 2.0 header."); return; }
            uint jsonLength = U32(12);
            if (jsonLength < 2u || jsonLength > 262144u || (int)jsonLength % 4 != 0 || 28L + jsonLength > value.Length)
            { Fail("GLB JSON exceeds policy or is truncated."); return; }
            int header = 20 + (int)jsonLength;
            uint binLength = U32(header);
            if (U32(header + 4) != 0x004e4942u || binLength > 10000000u || header + 8L + binLength != value.Length)
            { Fail("Exactly one embedded BIN chunk is required."); return; }
            _bin = header + 8; _binLength = (int)binLength;
            if (!VRCJson.TryDeserializeFromJson(Encoding.UTF8.GetString(value, 20, (int)jsonLength), out DataToken rootToken) ||
                rootToken.TokenType != TokenType.DataDictionary)
            { Fail("Invalid GLB JSON."); return; }
            DataDictionary root = rootToken.DataDictionary;
            if (Text(Dict(root, "asset"), "version", "") != "2.0") { Fail("glTF asset.version must be 2.0."); return; }
            string[] listKeys = { "buffers", "nodes", "meshes", "accessors", "bufferViews", "materials", "scenes", "extensionsRequired", "skins", "animations" };
            for (int k = 0; k < listKeys.Length; k++)
                if (root.ContainsKey(listKeys[k]) && !root.TryGetValue(listKeys[k], TokenType.DataList, out DataToken unused))
                { Fail("Invalid GLB array: " + listKeys[k]); return; }
            DataList buffers = List(root, "buffers");
            _definitions = List(root, "nodes"); _meshDefs = List(root, "meshes");
            _accessors = List(root, "accessors"); _views = List(root, "bufferViews");
            _materialDefs = List(root, "materials");
            _textureDefs = List(Dict(root, "extras"), "flare_rgba_textures");
            if (List(root, "extensionsRequired").Count != 0 || List(root, "skins").Count != 0 || List(root, "animations").Count != 0)
            { Fail("Required extensions, skins and glTF animations are outside the static MVP profile."); return; }
            if (_definitions.Count < 1 || _definitions.Count > Mathf.Clamp(MaximumNodes, 1, 32) ||
                _materialDefs.Count > Mathf.Clamp(MaximumMaterials, 1, 16) ||
                _textureDefs.Count > Mathf.Clamp(MaximumTextures, 1, 4) || _meshDefs.Count > 32 || _views.Count > 512 || _accessors.Count > 256)
            { Fail("GLB resource count exceeds policy."); return; }
            DataDictionary buffer = buffers.Count == 1 ? AsDict(buffers[0]) : null;
            int declared = Integer(buffer, "byteLength", -1);
            if (buffer == null || buffer.ContainsKey("uri") || declared < 0 || declared > _binLength || _binLength - declared > 3)
            { Fail("One embedded buffer is required; external URIs are forbidden."); return; }
            _binLength = declared;
            int count = _definitions.Count;
            _parents = new int[count];
            _initialActive = new bool[count];
            for (int n = 0; n < count; n++) _parents[n] = -1;
            for (int n = 0; n < count; n++)
            {
                DataDictionary node = AsDict(_definitions[n]);
                if (node == null || node.ContainsKey("matrix") || node.ContainsKey("skin") || node.ContainsKey("weights"))
                { Fail("Nodes require static TRS transforms (no matrix/skin/morph)."); return; }
                DataList children = List(node, "children");
                if (children.Count > count) { Fail("Too many child references."); return; }
                for (int c = 0; c < children.Count; c++)
                {
                    int child = Index(children[c]);
                    if (child < 0 || child >= count || child == n || _parents[child] != -1)
                    { Fail("Invalid or multiply-parented node."); return; }
                    _parents[child] = n;
                }
            }
            for (int n = 0; n < count; n++)
            {
                int parent = n, depth = 0;
                while (parent != -1 && depth <= 16) { parent = _parents[parent]; depth++; }
                if (parent != -1) { Fail("Node cycle or hierarchy depth above 16."); return; }
            }
            DataList scenes = List(root, "scenes");
            int scene = Integer(root, "scene", 0);
            if (scene < 0 || scene >= scenes.Count) { Fail("Default scene is missing."); return; }
            DataList roots = List(AsDict(scenes[scene]), "nodes");
            bool[] seen = new bool[count];
            for (int r = 0; r < roots.Count; r++)
            {
                int id = Index(roots[r]);
                if (id < 0 || id >= count || _parents[id] != -1 || seen[id]) { Fail("Invalid scene root."); return; }
                seen[id] = true;
            }
            for (int n = 0; n < count; n++) if (_parents[n] == -1 && !seen[n])
            { Fail("MVP requires all nodes to belong to the selected scene."); return; }
            Nodes = new FlareRuntimeNode[count]; _meshes = new Mesh[count]; _materials = new Material[_materialDefs.Count + 1];
            _textures = new Texture2D[_textureDefs.Count];
            _next = -_textureDefs.Count; Status = 1;
            Schedule();
        }

        private void Schedule()
        {
            if (_scheduled) return;
            _scheduled = true;
            SendCustomEventDelayedFrames(nameof(ContinueLoad), 1);
        }

        public void ContinueLoad()
        {
            _scheduled = false;
            if (Status != 1) return;
            if (_meshStage != 0)
            {
                if (ContinueMesh()) Schedule();
                return;
            }
            if (_next < 0)
            {
                if (!BuildTexture(_next + _textures.Length)) return;
                _next++; Schedule(); return;
            }
            if (_next < Nodes.Length)
            {
                if (!BuildNode(_next)) return;
                _next++; Schedule(); return;
            }
            for (int n = 0; n < Nodes.Length; n++)
                if (_parents[n] >= 0) Nodes[n].transform.SetParent(Nodes[_parents[n]].transform, false);
            for (int n = 0; n < Nodes.Length; n++)
            {
                Vector3 p = DisplayRoot.InverseTransformPoint(Nodes[n].transform.position);
                Vector3 s = Nodes[n].transform.lossyScale;
                if (!ValidVector(p, 1000f) || !ValidVector(s, 1000f)) { Fail("Composed transform exceeds policy."); return; }
            }
            if (!Interpreter.Configure(Nodes, _definitions)) { Fail(Interpreter.LastError); return; }
            for (int n = 0; n < Nodes.Length; n++) Nodes[n].gameObject.SetActive(_initialActive[n]);
            _bytes = null; _definitions = null; _meshDefs = null; _accessors = null; _views = null;
            _materialDefs = null; _textureDefs = null;
            Status = 2; Debug.Log("[FLARE] Parse success; declarative scene ready.");
        }

        private bool BuildTexture(int index)
        {
            DataDictionary d = AsDict(_textureDefs[index]);
            int width = Integer(d, "width", -1), height = Integer(d, "height", -1);
            int maximum = Mathf.Clamp(MaximumTextureDimension, 1, 512);
            if (width < 1 || height < 1 || width > maximum || height > maximum ||
                !View(Integer(d, "bufferView", -1), 1) || _readStride != 1 || _readCount != width * height * 4)
                return Fail("Invalid prepared RGBA texture. Prepare PNG/JPEG in the editor first.");
            byte[] pixels = new byte[_readCount]; Buffer.BlockCopy(_bytes, _readStart, pixels, 0, pixels.Length);
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            _textures[index] = texture;
            texture.LoadRawTextureData(pixels); texture.Apply(false, true);
            return true;
        }

        private bool BuildNode(int index)
        {
            DataDictionary d = AsDict(_definitions[index]);
            Vector3 position = Vector(d, "translation", Vector3.zero);
            Vector3 scale = Vector(d, "scale", Vector3.one);
            DataList rotation = List(d, "rotation");
            Quaternion q = Quaternion.identity;
            if (d.ContainsKey("rotation"))
            {
                if (rotation.Count != 4) return Fail("Invalid node rotation.");
                q = new Quaternion(-Number(rotation[0]), -Number(rotation[1]), Number(rotation[2]), Number(rotation[3]));
                float norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                if (!Finite(norm, 2f) || norm < 0.999f || norm > 1.001f) return Fail("Quaternion must be normalized.");
            }
            if (!ValidVector(position, 100f) || !ValidVector(scale, 100f) ||
                Mathf.Abs(scale.x) < 0.001f || Mathf.Abs(scale.y) < 0.001f || Mathf.Abs(scale.z) < 0.001f)
                return Fail("Node transform is outside policy.");
            GameObject instance = Instantiate(RuntimeNodePrefab);
            instance.SetActive(false);
            FlareRuntimeNode node = instance.GetComponent<FlareRuntimeNode>();
            if (node == null || node.Filter == null || node.Renderer == null || node.Box == null || node.Audio == null)
            { Destroy(instance); return Fail("Runtime node prefab is incomplete."); }
            Nodes[index] = node;
            instance.name = Text(d, "name", "FLARE Node");
            node.transform.SetParent(DisplayRoot, false);
            node.transform.localPosition = new Vector3(position.x, position.y, -position.z);
            node.transform.localScale = scale; node.transform.localRotation = q;
            node.Renderer.enabled = false; node.Box.enabled = false;
            node.Audio.playOnAwake = false; node.Audio.loop = false; node.Audio.Stop();
            if (d.ContainsKey("mesh") && !BeginMesh(Integer(d, "mesh", -1), index)) return false;
            DataDictionary g = Dict(Dict(d, "extras"), "vrc_gimmick");
            _initialActive[index] = g == null || !g.TryGetValue("initialActive", TokenType.Boolean, out DataToken initial) || initial.Boolean;
            DataDictionary collider = Dict(g, "collider");
            if (collider != null)
            {
                if (Text(collider, "type", "box") != "box") return Fail("Only BoxCollider is allowed by this profile.");
                Vector3 size = Vector(collider, "size", Vector3.one);
                Vector3 center = Vector(collider, "center", Vector3.zero);
                if (!ValidVector(size, 10f) || !ValidVector(center, 10f) || size.x <= 0 || size.y <= 0 || size.z <= 0)
                    return Fail("Collider bounds exceed policy.");
                node.Box.size = size; node.Box.center = center; node.Box.isTrigger = false; node.Box.enabled = true;
            }
            return true;
        }

        private bool BeginMesh(int meshIndex, int nodeIndex)
        {
            if (meshIndex < 0 || meshIndex >= _meshDefs.Count) return Fail("Invalid mesh reference.");
            DataList primitives = List(AsDict(_meshDefs[meshIndex]), "primitives");
            if (primitives.Count != 1) return Fail("MVP requires one primitive per node; split submeshes in the editor.");
            DataDictionary primitive = AsDict(primitives[0]);
            if (primitive == null || Integer(primitive, "mode", 4) != 4 || primitive.ContainsKey("targets"))
                return Fail("Only static TRIANGLES are supported.");
            DataDictionary attributes = Dict(primitive, "attributes");
            if (!Accessor(Integer(attributes, "POSITION", -1), "VEC3", 3, false)) return false;
            int count = _readCount;
            if (count > Mathf.Clamp(MaximumVerticesPerNode, 3, 4096) ||
                LoadedVertices + count > Mathf.Clamp(MaximumVertices, 3, 40000)) return Fail("Vertex budget exceeded.");
            _meshNode = nodeIndex; _meshCount = count; _primitive = primitive; _attributes = attributes;
            _positions = new Vector3[count]; _meshStage = 1; _cursor = 0;
            _meshes[nodeIndex] = new Mesh();
            return true;
        }

        private bool ContinueMesh()
        {
            if (_meshStage <= 4)
            {
                int end = Mathf.Min(_readCount, _cursor + Mathf.Clamp(MeshElementsPerFrame, 16, 512));
                PeakDecodeElements = Mathf.Max(PeakDecodeElements, end - _cursor); DecodeContinuations++;
                for (int i = _cursor; i < end; i++)
                {
                    int at = _readStart + i * _readStride;
                    if (_meshStage == 1)
                    {
                        Vector3 p = new Vector3(F32(at), F32(at + 4), -F32(at + 8));
                        if (!ValidVector(p, 100f)) return Fail("Invalid vertex position.");
                        _positions[i] = p;
                    }
                    else if (_meshStage == 2)
                    {
                        Vector3 normal = new Vector3(F32(at), F32(at + 4), -F32(at + 8));
                        if (!ValidVector(normal, 16f)) return Fail("Invalid normal.");
                        _normals[i] = normal.normalized;
                    }
                    else if (_meshStage == 3)
                    {
                        float x = F32(at), y = F32(at + 4);
                        if (!Finite(x, 10000f) || !Finite(y, 10000f)) return Fail("Invalid UV.");
                        _uv[i] = new Vector2(x, 1f - y);
                    }
                    else
                    {
                        uint id = _readComponent == 5121 ? _bytes[at] : _readComponent == 5123 ? (uint)(_bytes[at] | _bytes[at + 1] << 8) : U32(at);
                        if (id >= (uint)_meshCount) return Fail("Index references a missing vertex.");
                        int destination = i % 3 == 1 ? i + 1 : i % 3 == 2 ? i - 1 : i;
                        _indices[destination] = (int)id;
                    }
                }
                _cursor = end;
                if (end < _readCount) return true;
                _meshStage++; _cursor = 0;
                return PrepareMeshStage();
            }
            // Native mesh uploads cannot be subdivided; keep each on its own continuation.
            Mesh mesh = _meshes[_meshNode];
            if (_meshStage == 5) mesh.vertices = _positions;
            else if (_meshStage == 6 && _normals != null) mesh.normals = _normals;
            else if (_meshStage == 7 && _uv != null) mesh.uv = _uv;
            else if (_meshStage == 8) mesh.triangles = _indices;
            else if (_meshStage == 9 && _normals == null) mesh.RecalculateNormals();
            else if (_meshStage == 10)
            {
                mesh.RecalculateBounds();
                Nodes[_meshNode].Filter.sharedMesh = mesh;
                if (!FinishMaterial()) return false;
                Nodes[_meshNode].Renderer.enabled = true; LoadedVertices += _meshCount;
                ReleaseMeshScratch(); return true;
            }
            _meshStage++; return true;
        }

        private bool PrepareMeshStage()
        {
            if (_meshStage == 2)
            {
                if (_attributes.ContainsKey("NORMAL"))
                {
                    if (!Accessor(Integer(_attributes, "NORMAL", -1), "VEC3", 3, false) || _readCount != _meshCount) return Fail("Invalid normals.");
                    _normals = new Vector3[_meshCount]; return true;
                }
                _meshStage++;
            }
            if (_meshStage == 3)
            {
                if (_attributes.ContainsKey("TEXCOORD_0"))
                {
                    if (!Accessor(Integer(_attributes, "TEXCOORD_0", -1), "VEC2", 2, false) || _readCount != _meshCount) return Fail("Invalid UVs.");
                    _uv = new Vector2[_meshCount]; return true;
                }
                _meshStage++;
            }
            if (_meshStage == 4)
            {
                if (!Accessor(Integer(_primitive, "indices", -1), "SCALAR", 1, true) || _readCount % 3 != 0)
                    return Fail("Invalid index buffer.");
                _indices = new int[_readCount];
            }
            return true;
        }

        private void ReleaseMeshScratch()
        {
            _meshStage = 0; _cursor = 0;
            _positions = null; _normals = null; _uv = null; _indices = null; _primitive = null; _attributes = null;
        }

        private bool FinishMaterial()
        {
            FlareRuntimeNode node = Nodes[_meshNode];
            int materialIndex = Integer(_primitive, "material", -1);
            if (_primitive.ContainsKey("material") && (materialIndex < 0 || materialIndex >= _materialDefs.Count)) return Fail("Invalid material reference.");
            int slot = materialIndex + 1;
            if (_materials[slot] != null) { node.Renderer.sharedMaterial = _materials[slot]; return true; }
            node.Renderer.sharedMaterial = MaterialTemplate;
            Material material = node.Renderer.material; _materials[slot] = material;
            // glTF defaults must not inherit an unrelated template's tint, texture or UV transform.
            material.SetColor("_Color", Color.white); material.SetTexture("_MainTex", null);
            material.mainTextureScale = Vector2.one; material.mainTextureOffset = Vector2.zero;
            if (materialIndex >= 0)
            {
                DataDictionary mat = AsDict(_materialDefs[materialIndex]);
                if (mat == null || Text(mat, "alphaMode", "OPAQUE") != "OPAQUE") return Fail("MVP permits opaque materials only.");
                DataDictionary pbr = Dict(mat, "pbrMetallicRoughness");
                DataList rgba = List(pbr, "baseColorFactor");
                if (rgba.Count != 0)
                {
                    if (rgba.Count != 4) return Fail("Invalid base color.");
                    float r = Number(rgba[0]), g = Number(rgba[1]), b = Number(rgba[2]), a = Number(rgba[3]);
                    if (!Finite(r, 1f) || !Finite(g, 1f) || !Finite(b, 1f) || !Finite(a, 1f) || r < 0 || g < 0 || b < 0 || a < 0)
                        return Fail("Invalid base color range.");
                    material.SetColor("_Color", new Color(r, g, b, a));
                }
                DataDictionary texture = Dict(pbr, "baseColorTexture");
                if (texture != null)
                {
                    int id = Integer(texture, "index", -1);
                    if (id < 0 || id >= _textures.Length || Integer(texture, "texCoord", 0) != 0)
                        return Fail("Texture needs editor preparation into flare_rgba_textures.");
                    material.SetTexture("_MainTex", _textures[id]);
                }
            }
            return true;
        }

        private bool Accessor(int index, string type, int components, bool indices)
        {
            if (index < 0 || index >= _accessors.Count) return Fail("Accessor index is invalid.");
            DataDictionary d = AsDict(_accessors[index]);
            int component = Integer(d, "componentType", -1), count = Integer(d, "count", -1);
            int bytes = indices ? (component == 5121 ? 1 : component == 5123 ? 2 : component == 5125 ? 4 : 0) : (component == 5126 ? 4 : 0);
            if (d == null || Text(d, "type", "") != type || bytes == 0 || count < 1 || count > 12288 ||
                d.ContainsKey("sparse") || d.ContainsKey("normalized") || !View(Integer(d, "bufferView", -1), bytes * components))
                return Fail("Unsupported accessor.");
            int offset = Integer(d, "byteOffset", 0);
            if (offset < 0 || (long)offset + (long)(count - 1) * _readStride + bytes * components > _readCount)
                return Fail("Accessor exceeds its view.");
            _readStart += offset; _readCount = count; _readComponent = component; return true;
        }
        private bool View(int index, int elementBytes)
        {
            if (index < 0 || index >= _views.Count) return false;
            DataDictionary d = AsDict(_views[index]);
            int offset = Integer(d, "byteOffset", 0), length = Integer(d, "byteLength", -1);
            int stride = Integer(d, "byteStride", elementBytes);
            if (Integer(d, "buffer", -1) != 0 || offset < 0 || length < 0 || (long)offset + length > _binLength ||
                stride < elementBytes || stride > 252) return false;
            _readStart = _bin + offset; _readCount = length; _readStride = stride; return true;
        }
        private DataDictionary AsDict(DataToken t) { return t.TokenType == TokenType.DataDictionary ? t.DataDictionary : null; }
        private DataDictionary Dict(DataDictionary d, string key)
        { return d != null && d.TryGetValue(key, TokenType.DataDictionary, out DataToken t) ? t.DataDictionary : null; }
        private DataList List(DataDictionary d, string key)
        { return d != null && d.TryGetValue(key, TokenType.DataList, out DataToken t) ? t.DataList : new DataList(); }
        private string Text(DataDictionary d, string key, string fallback)
        { return d != null && d.TryGetValue(key, TokenType.String, out DataToken t) ? t.String : fallback; }
        private int Integer(DataDictionary d, string key, int fallback)
        { return d != null && d.TryGetValue(key, out DataToken t) ? Index(t) : fallback; }
        private int Index(DataToken t)
        { if (!t.IsNumber) return -1; double x = t.Number; if (!(x >= 0d && x <= 2147483647d)) return -1; int n = (int)x; return x == n ? n : -1; }
        private float Number(DataToken t) { return t.IsNumber ? (float)t.Number : float.NaN; }
        private Vector3 Vector(DataDictionary d, string key, Vector3 fallback)
        {
            if (!d.ContainsKey(key)) return fallback;
            DataList values = List(d, key);
            return values.Count == 3 ? new Vector3(Number(values[0]), Number(values[1]), Number(values[2])) : new Vector3(float.NaN, 0f, 0f);
        }
        private bool ValidVector(Vector3 p, float max) { return Finite(p.x, max) && Finite(p.y, max) && Finite(p.z, max); }
        private bool Finite(float x, float max) { return x >= -max && x <= max; }
        private float F32(int at) { return BitConverter.ToSingle(_bytes, at); }
        private uint U32(int at) { return (uint)_bytes[at] | (uint)_bytes[at + 1] << 8 | (uint)_bytes[at + 2] << 16 | (uint)_bytes[at + 3] << 24; }
        private bool Fail(string error)
        { Clear(); LastError = error; Status = 3; Debug.LogError("[FLARE] Parse failed: " + error); return false; }
    }
}
