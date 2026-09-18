using System;
using System.Text;
using UdonSharp;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.Data;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace AvatarCatalog.Remote
{
    /// <summary>
    /// Small, self-owned GLB 2.0 preview loader for the ImagePad.
    /// It intentionally supports one embedded mesh/primitive/material only.
    /// Textures, skins, morphs, animations and compression belong in the GLB -> RAC2 path.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public sealed class SimpleGlbRuntimeLoader : UdonSharpBehaviour
    {
        public const int StatusIdle = 0;
        public const int StatusLoading = 1;
        public const int StatusReady = 2;
        public const int StatusError = 3;

        [Header("Source")]
        public VRCUrl RuntimeUrl;

        [Header("Display")]
        public MeshFilter TargetMeshFilter;
        public MeshRenderer TargetRenderer;
        public Material MaterialTemplate;
        public string BaseColorProperty = "_Color";
        [Tooltip("The largest dimension is normalized to this size for the simple preview.")]
        public float DisplaySize = 1.5f;

        [Header("Limits")]
        public int MaxBytes = 10000000;
        public int MaxVertices = 40000;
        public int MaxIndices = 120000;

        [Header("Callbacks")]
        public UdonBehaviour CallbackReceiver;
        public string LoadStartedEvent = "OnGlbLoadStarted";
        public string LoadSucceededEvent = "OnGlbLoadSucceeded";
        public string LoadFailedEvent = "OnGlbLoadFailed";
        public string ClearedEvent = "OnGlbCleared";

        [Header("Diagnostics")]
        public int Status;
        public string StatusMessage = "Idle";
        public string LastError = "";
        public int LastHttpErrorCode;
        public int LoadedVertexCount;
        public int LoadedIndexCount;
        public bool LoadedHasNormals;
        public bool LoadedHasUv0;

        private const uint GlbMagic = 0x46546c67u;
        private const uint JsonChunk = 0x4e4f534au;
        private const uint BinChunk = 0x004e4942u;
        private const int FloatComponent = 5126;

        private byte[] _bytes;
        private int _binOffset;
        private int _binLength;
        private DataList _accessors;
        private DataList _bufferViews;
        private Mesh _mesh;
        private Material _material;
        private string _parseError;

        public void LoadRuntimeUrl()
        {
            if (Status == StatusLoading) return;
            if (VRCUrl.IsNullOrEmpty(RuntimeUrl)) { ReportError("Enter a direct GLB URL first.", 0); return; }
            if (TargetMeshFilter == null || TargetRenderer == null || MaterialTemplate == null)
            {
                ReportError("GLB display references are incomplete.", 0);
                return;
            }

            Status = StatusLoading;
            StatusMessage = "Downloading GLB...";
            LastError = "";
            LastHttpErrorCode = 0;
            Notify(LoadStartedEvent);
            VRCStringDownloader.LoadUrl(RuntimeUrl, (IUdonEventReceiver)this);
        }

        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            byte[] value = result.ResultBytes;
            if (value == null || value.Length == 0) { ReportError("GLB response was empty.", 0); return; }
            if (value.Length > MaxBytes) { ReportError("GLB exceeds the 10 MB simple-loader limit.", 0); return; }
            if (!ParseAndApply(value)) { ReportError(_parseError, 0); return; }
            Status = StatusReady;
            StatusMessage = "Ready";
            Notify(LoadSucceededEvent);
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            ReportError("GLB download failed: " + result.Error, result.ErrorCode);
        }

        public void Clear()
        {
            if (TargetRenderer != null) TargetRenderer.enabled = false;
            if (_mesh != null) _mesh.Clear();
            LoadedVertexCount = 0;
            LoadedIndexCount = 0;
            LoadedHasNormals = false;
            LoadedHasUv0 = false;
            Status = StatusIdle;
            StatusMessage = "Idle";
            LastError = "";
            Notify(ClearedEvent);
        }

        private bool ParseAndApply(byte[] value)
        {
            _bytes = value;
            _parseError = "Invalid GLB.";
            if (value.Length < 28 || ReadU32(0) != GlbMagic || ReadU32(4) != 2u || ReadU32(8) != (uint)value.Length)
                return Fail("Only complete GLB 2.0 files are supported.");

            uint jsonLengthRaw = ReadU32(12);
            if (ReadU32(16) != JsonChunk || jsonLengthRaw == 0u || jsonLengthRaw > int.MaxValue)
                return Fail("GLB JSON chunk is missing.");
            int jsonLength = (int)jsonLengthRaw;
            int binHeader = 20 + jsonLength;
            if (binHeader < 20 || binHeader + 8 > value.Length || ReadU32(binHeader + 4) != BinChunk)
                return Fail("GLB must contain one embedded BIN chunk.");
            uint binLengthRaw = ReadU32(binHeader);
            if (binLengthRaw == 0u || binLengthRaw > int.MaxValue || binHeader + 8L + binLengthRaw != value.Length)
                return Fail("GLB BIN chunk is missing or non-canonical.");
            _binOffset = binHeader + 8;
            _binLength = (int)binLengthRaw;

            string json = Encoding.UTF8.GetString(value, 20, jsonLength);
            if (!VRCJson.TryDeserializeFromJson(json, out DataToken rootToken) || rootToken.TokenType != TokenType.DataDictionary)
                return Fail("GLB JSON could not be parsed: " + rootToken.ToString());
            DataDictionary root = rootToken.DataDictionary;

            if (!GetList(root, "buffers", out DataList buffers) || buffers.Count != 1 ||
                !GetList(root, "bufferViews", out _bufferViews) ||
                !GetList(root, "accessors", out _accessors) ||
                !GetList(root, "meshes", out DataList meshes) || meshes.Count != 1)
                return Fail("Simple GLB mode requires one embedded buffer and one mesh.");

            if (!TokenDictionary(buffers[0], out DataDictionary buffer) || !GetInt(buffer, "byteLength", out int declaredBufferLength) ||
                declaredBufferLength < 1 || declaredBufferLength > _binLength || buffer.ContainsKey("uri"))
                return Fail("External GLB buffers are not supported.");

            if (!TokenDictionary(meshes[0], out DataDictionary meshData) ||
                !GetList(meshData, "primitives", out DataList primitives) || primitives.Count != 1 ||
                !TokenDictionary(primitives[0], out DataDictionary primitive))
                return Fail("Simple GLB mode supports exactly one TRIANGLES primitive.");

            if (TryGetInt(primitive, "mode", out int mode) && mode != 4)
                return Fail("Only TRIANGLES primitives are supported.");
            if (!GetDictionary(primitive, "attributes", out DataDictionary attributes) ||
                !GetInt(attributes, "POSITION", out int positionAccessor) ||
                !GetInt(primitive, "indices", out int indexAccessor))
                return Fail("GLB POSITION or indices are missing.");

            bool hasNormal = TryGetInt(attributes, "NORMAL", out int normalAccessor);
            bool hasUv = TryGetInt(attributes, "TEXCOORD_0", out int uvAccessor);
            if (!ReadFloatAccessor(positionAccessor, "VEC3", 3, out float[] positions, out int vertexCount)) return false;
            if (vertexCount < 1 || vertexCount > MaxVertices) return Fail("GLB vertex count exceeds the simple-loader limit.");

            float[] normals = new float[0];
            int normalCount = 0;
            if (hasNormal && (!ReadFloatAccessor(normalAccessor, "VEC3", 3, out normals, out normalCount) || normalCount != vertexCount))
                return Fail("GLB NORMAL accessor does not match POSITION.");
            float[] uv = new float[0];
            int uvCount = 0;
            if (hasUv && (!ReadFloatAccessor(uvAccessor, "VEC2", 2, out uv, out uvCount) || uvCount != vertexCount))
                return Fail("GLB TEXCOORD_0 accessor does not match POSITION.");
            if (!ReadIndices(indexAccessor, vertexCount, out int[] triangles)) return false;
            if (triangles.Length < 3 || triangles.Length > MaxIndices || triangles.Length % 3 != 0)
                return Fail("GLB index count exceeds the simple-loader limit or is not triangles.");

            Color baseColor = Color.white;
            if (TryGetInt(primitive, "material", out int materialIndex))
            {
                if (!GetList(root, "materials", out DataList materials) || materialIndex < 0 || materialIndex >= materials.Count ||
                    !TokenDictionary(materials[materialIndex], out DataDictionary materialData))
                    return Fail("GLB material reference is invalid.");
                if (materialData.ContainsKey("normalTexture") || materialData.ContainsKey("occlusionTexture") || materialData.ContainsKey("emissiveTexture"))
                    return Fail("Textured GLB is not supported in direct mode. Convert it to RAC2 in Unity.");
                if (GetOptionalDictionary(materialData, "pbrMetallicRoughness", out DataDictionary pbr))
                {
                    if (pbr.ContainsKey("baseColorTexture") || pbr.ContainsKey("metallicRoughnessTexture"))
                        return Fail("Textured GLB is not supported in direct mode. Convert it to RAC2 in Unity.");
                    if (GetOptionalList(pbr, "baseColorFactor", out DataList colorValues))
                    {
                        if (colorValues.Count != 4 || !Number(colorValues[0], out float r) || !Number(colorValues[1], out float g) ||
                            !Number(colorValues[2], out float b) || !Number(colorValues[3], out float a))
                            return Fail("GLB baseColorFactor is invalid.");
                        baseColor = new Color(r, g, b, a);
                    }
                }
            }

            Vector3[] vertices = new Vector3[vertexCount];
            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 p = new Vector3(positions[i * 3], positions[i * 3 + 1], -positions[i * 3 + 2]);
                if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z)) return Fail("GLB POSITION contains a non-finite value.");
                vertices[i] = p;
                if (i == 0) { min = p; max = p; }
                else { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            }

            Vector3 size = max - min;
            float largest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            if (!(largest > 0.000001f) || !Finite(largest)) return Fail("GLB bounds are empty or invalid.");
            float targetSize = Mathf.Clamp(DisplaySize, 0.1f, 3f);
            float scale = targetSize / largest;
            Vector3 center = (min + max) * 0.5f;
            float floorOffset = size.y * scale * 0.5f;
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 p = (vertices[i] - center) * scale;
                p.y += floorOffset;
                vertices[i] = p;
            }

            Vector3[] unityNormals = new Vector3[0];
            if (hasNormal)
            {
                unityNormals = new Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    Vector3 n = new Vector3(normals[i * 3], normals[i * 3 + 1], -normals[i * 3 + 2]);
                    if (!Finite(n.x) || !Finite(n.y) || !Finite(n.z)) return Fail("GLB NORMAL contains a non-finite value.");
                    unityNormals[i] = n.normalized;
                }
            }

            Vector2[] unityUv = new Vector2[0];
            if (hasUv)
            {
                unityUv = new Vector2[vertexCount];
                for (int i = 0; i < vertexCount; i++) unityUv[i] = new Vector2(uv[i * 2], 1f - uv[i * 2 + 1]);
            }

            for (int i = 0; i < triangles.Length; i += 3)
            {
                int swap = triangles[i + 1];
                triangles[i + 1] = triangles[i + 2];
                triangles[i + 2] = swap;
            }

            if (_mesh == null) { _mesh = new Mesh(); _mesh.name = "Direct GLB Preview"; }
            else _mesh.Clear();
            _mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _mesh.vertices = vertices;
            if (hasNormal) _mesh.normals = unityNormals;
            if (hasUv) _mesh.uv = unityUv;
            _mesh.triangles = triangles;
            if (!hasNormal) _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();

            if (_material == null)
            {
                TargetRenderer.sharedMaterial = MaterialTemplate;
                _material = TargetRenderer.material;
                _material.name = "Direct GLB Material";
            }
            if (_material.HasProperty(BaseColorProperty)) _material.SetColor(BaseColorProperty, baseColor);
            if (_material.HasProperty("_MainTex")) _material.SetTexture("_MainTex", null);
            TargetMeshFilter.sharedMesh = _mesh;
            TargetRenderer.sharedMaterial = _material;
            TargetRenderer.enabled = true;
            LoadedVertexCount = vertexCount;
            LoadedIndexCount = triangles.Length;
            LoadedHasNormals = hasNormal;
            LoadedHasUv0 = hasUv;
            _bytes = null;
            return true;
        }

        private bool ReadFloatAccessor(int accessorIndex, string expectedType, int components, out float[] values, out int count)
        {
            values = new float[0]; count = 0;
            if (!Accessor(accessorIndex, expectedType, FloatComponent, components * 4, out count, out int start, out int stride)) return false;
            values = new float[count * components];
            for (int i = 0; i < count; i++)
            {
                int at = start + i * stride;
                for (int c = 0; c < components; c++) values[i * components + c] = BitConverter.ToSingle(_bytes, at + c * 4);
            }
            return true;
        }

        private bool ReadIndices(int accessorIndex, int vertexCount, out int[] indices)
        {
            indices = new int[0];
            if (accessorIndex < 0 || accessorIndex >= _accessors.Count || !TokenDictionary(_accessors[accessorIndex], out DataDictionary accessor) ||
                !GetString(accessor, "type", out string type) || type != "SCALAR" || !GetInt(accessor, "componentType", out int componentType) ||
                !GetInt(accessor, "count", out int count) || count < 1 || count > MaxIndices || accessor.ContainsKey("sparse") || accessor.ContainsKey("normalized") ||
                !GetInt(accessor, "bufferView", out int viewIndex)) return Fail("GLB index accessor is unsupported.");
            int componentBytes = componentType == 5121 ? 1 : componentType == 5123 ? 2 : componentType == 5125 ? 4 : 0;
            if (componentBytes == 0 || !View(viewIndex, componentBytes, out int viewStart, out int viewLength, out int stride))
                return Fail("GLB indices must use unsigned byte, ushort, or uint.");
            int accessorOffset = 0;
            if (TryGetInt(accessor, "byteOffset", out int foundOffset)) accessorOffset = foundOffset;
            long required = (long)accessorOffset + (long)(count - 1) * stride + componentBytes;
            if (accessorOffset < 0 || required > viewLength) return Fail("GLB index accessor exceeds its bufferView.");
            indices = new int[count];
            int start = viewStart + accessorOffset;
            for (int i = 0; i < count; i++)
            {
                int at = start + i * stride;
                uint raw = componentBytes == 1 ? _bytes[at] : componentBytes == 2 ? ReadU16(at) : ReadU32(at);
                if (raw >= (uint)vertexCount) return Fail("GLB index references a missing vertex.");
                indices[i] = (int)raw;
            }
            return true;
        }

        private bool Accessor(int index, string expectedType, int componentType, int elementBytes, out int count, out int start, out int stride)
        {
            count = 0; start = 0; stride = 0;
            if (index < 0 || index >= _accessors.Count || !TokenDictionary(_accessors[index], out DataDictionary accessor) ||
                !GetString(accessor, "type", out string type) || type != expectedType ||
                !GetInt(accessor, "componentType", out int foundComponent) || foundComponent != componentType ||
                !GetInt(accessor, "count", out count) || count < 1 || accessor.ContainsKey("sparse") || accessor.ContainsKey("normalized") ||
                !GetInt(accessor, "bufferView", out int viewIndex) || !View(viewIndex, elementBytes, out int viewStart, out int viewLength, out stride))
                return Fail("GLB " + expectedType + " accessor is unsupported.");
            int accessorOffset = 0;
            if (TryGetInt(accessor, "byteOffset", out int foundOffset)) accessorOffset = foundOffset;
            long required = (long)accessorOffset + (long)(count - 1) * stride + elementBytes;
            if (accessorOffset < 0 || required > viewLength) return Fail("GLB accessor exceeds its bufferView.");
            start = viewStart + accessorOffset;
            return true;
        }

        private bool View(int index, int elementBytes, out int start, out int length, out int stride)
        {
            start = 0; length = 0; stride = elementBytes;
            if (index < 0 || index >= _bufferViews.Count || !TokenDictionary(_bufferViews[index], out DataDictionary view) ||
                !GetInt(view, "buffer", out int bufferIndex) || bufferIndex != 0 || !GetInt(view, "byteLength", out length)) return false;
            int offset = 0;
            if (TryGetInt(view, "byteOffset", out int foundOffset)) offset = foundOffset;
            if (TryGetInt(view, "byteStride", out int foundStride)) stride = foundStride;
            if (offset < 0 || length < elementBytes || stride < elementBytes || stride > 252 || (stride & 3) != 0 || (long)offset + length > _binLength) return false;
            start = _binOffset + offset;
            return true;
        }

        private bool GetDictionary(DataDictionary source, string key, out DataDictionary value)
        {
            value = null;
            return source.TryGetValue(key, TokenType.DataDictionary, out DataToken token) && TokenDictionary(token, out value);
        }

        private bool GetOptionalDictionary(DataDictionary source, string key, out DataDictionary value)
        {
            value = null;
            if (!source.ContainsKey(key)) return false;
            return GetDictionary(source, key, out value);
        }

        private bool GetList(DataDictionary source, string key, out DataList value)
        {
            value = null;
            if (!source.TryGetValue(key, TokenType.DataList, out DataToken token)) return false;
            value = token.DataList;
            return value != null;
        }

        private bool GetOptionalList(DataDictionary source, string key, out DataList value)
        {
            value = null;
            if (!source.ContainsKey(key)) return false;
            return GetList(source, key, out value);
        }

        private bool TokenDictionary(DataToken token, out DataDictionary value)
        {
            value = null;
            if (token.TokenType != TokenType.DataDictionary) return false;
            value = token.DataDictionary;
            return value != null;
        }

        private bool GetString(DataDictionary source, string key, out string value)
        {
            value = "";
            if (!source.TryGetValue(key, TokenType.String, out DataToken token)) return false;
            value = token.String;
            return value != null;
        }

        private bool GetInt(DataDictionary source, string key, out int value)
        {
            value = 0;
            if (!source.TryGetValue(key, out DataToken token) || !token.IsNumber) return false;
            double number = token.Number;
            if (number < 0d || number > 2147483647d) return false;
            value = (int)number;
            return number == value;
        }

        private bool TryGetInt(DataDictionary source, string key, out int value)
        {
            value = 0;
            return source.ContainsKey(key) && GetInt(source, key, out value);
        }

        private bool Number(DataToken token, out float value)
        {
            value = 0f;
            if (!token.IsNumber) return false;
            value = (float)token.Number;
            return Finite(value);
        }

        private bool Finite(float value) { return value == value && value >= -1000000f && value <= 1000000f; }
        private ushort ReadU16(int offset) { return (ushort)(_bytes[offset] | _bytes[offset + 1] << 8); }
        private uint ReadU32(int offset) { return (uint)_bytes[offset] | (uint)_bytes[offset + 1] << 8 | (uint)_bytes[offset + 2] << 16 | (uint)_bytes[offset + 3] << 24; }
        private bool Fail(string message) { _parseError = message; _bytes = null; return false; }

        private void ReportError(string message, int code)
        {
            _bytes = null;
            LastError = message;
            LastHttpErrorCode = code;
            Status = StatusError;
            StatusMessage = message;
            Debug.LogError("[Direct GLB] " + message);
            Notify(LoadFailedEvent);
        }

        private void Notify(string eventName)
        {
            if (CallbackReceiver != null && eventName != null && eventName.Length > 0) CallbackReceiver.SendCustomEvent(eventName);
        }
    }
}


