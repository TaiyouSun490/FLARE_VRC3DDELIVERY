using System;
using UdonSharp;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace AvatarCatalog.Remote
{
    /// <summary>
    /// Downloads and displays a static mesh stored in the RAC1 binary format.
    ///
    /// VRCUrl values cannot be constructed freely at runtime in Udon. Populate
    /// CatalogUrls in the inspector, then select one of those fixed slots.
    /// Downloads are local to each visitor; this behaviour intentionally has no
    /// network-synchronised state.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class RemoteAvatarCatalogLoader : UdonSharpBehaviour
    {
        // RAC1 flag bits. Unknown bits are rejected so future formats cannot be
        // silently misread by this version of the loader.
        public const uint FlagHasNormals = 1u << 0;
        public const uint FlagHasUv0 = 1u << 1;
        public const uint FlagHasColors = 1u << 2;
        public const uint FlagHasTextureRgba32 = 1u << 3;

        public const int StatusIdle = 0;
        public const int StatusLoading = 1;
        public const int StatusReady = 2;
        public const int StatusError = 3;
        public const int StatusDiscarding = 4;

        private const uint RacVersion = 1u;
        private const uint KnownFlags = FlagHasNormals | FlagHasUv0 | FlagHasColors | FlagHasTextureRgba32;
        private const int HeaderBytes = 60;
        private const int HardMaxDownloadBytes = 100000000;
        private const int HardMaxVertices = 1000000;
        private const int HardMaxIndices = 3000000;
        private const int HardMaxTextureDimension = 8192;
        private const int HardMaxTextureBytes = 268435456;
        private const int StandardBoothMaxDownloadBytes = 10000000;
        private const int StandardBoothMaxVertices = 40000;
        private const int StandardBoothMaxIndices = 120000;
        private const int StandardBoothMaxTextureDimension = 1024;
        private const int StandardBoothMaxTextureBytes = 4194304;
        private const float StandardBoothWidth = 3f;
        private const float StandardBoothDepth = 3f;
        private const float StandardBoothHeight = 2.7f;
        private const float StandardBoothBoundaryTolerance = 0.005f;
        private const float HeaderBoundsContainmentTolerance = 0.001f;
        private const float MaximumHeaderBoundsPadding = 0.1f;

        [Header("Fixed remote slots")]
        [Tooltip("RAC1 files that this world is allowed to request. VRCUrl slots must be authored in the Unity inspector.")]
        public VRCUrl[] CatalogUrls;

        [Tooltip("Slot used by LoadSelectedSlot and Interact.")]
        public int SelectedSlot;

        [Tooltip("If enabled, interacting with this object loads SelectedSlot.")]
        public bool LoadOnInteract = true;

        [Tooltip("If enabled, loads SelectedSlot after this behaviour starts. Leave this off when another booth manager schedules downloads.")]
        public bool LoadOnStart;

        [Tooltip("Delay before LoadOnStart fires. Stagger this value across booths to avoid every display requesting data at once.")]
        public float LoadOnStartDelaySeconds = 1f;

        [Header("Display target")]
        [Tooltip("Receives the reconstructed runtime Mesh.")]
        public MeshFilter TargetMeshFilter;

        [Tooltip("Receives a private material instance and is enabled after a successful load.")]
        public Renderer TargetRenderer;

        [Tooltip("Template copied for the runtime material. If omitted, TargetRenderer's assigned material is instanced.")]
        public Material MaterialTemplate;

        [Tooltip("Color property used by the display shader.")]
        public string BaseColorProperty = "_Color";

        [Tooltip("RGBA32 texture property used by the display shader.")]
        public string MainTextureProperty = "_MainTex";

        [Tooltip("Generate normals when the RAC1 file omits them.")]
        public bool RecalculateMissingNormals = true;

        [Header("Safety limits")]
        [Tooltip("Reject downloads larger than this before parsing.")]
        public int MaxDownloadBytes = 52428800;

        [Tooltip("Maximum vertex count accepted from one RAC1 file.")]
        public int MaxVertices = 100000;

        [Tooltip("Maximum index count accepted from one RAC1 file.")]
        public int MaxIndices = 300000;

        [Tooltip("Maximum width or height of an embedded texture.")]
        public int MaxTextureDimension = 4096;

        [Tooltip("Maximum raw embedded RGBA32 byte count.")]
        public int MaxTextureBytes = 67108864;

        [Tooltip("Absolute limit applied to positions and bounds values.")]
        public float MaxAbsCoordinate = 1000000f;

        [Tooltip("Absolute limit applied to UV values.")]
        public float MaxAbsUv = 1000000f;

        [Header("Standard booth profile")]
        [Tooltip("Apply the standard booth geometry, file, mesh, and texture limits. The booth origin is the centre of the floor: X is width, Y is height, and Z is depth.")]
        public bool EnforceStandardBoothProfile;

        [Tooltip("Allowed booth width in metres, centred on local X = 0.")]
        public float BoothWidth = StandardBoothWidth;

        [Tooltip("Allowed booth depth in metres, centred on local Z = 0.")]
        public float BoothDepth = StandardBoothDepth;

        [Tooltip("Allowed booth height in metres, measured upward from local Y = 0.")]
        public float BoothHeight = StandardBoothHeight;

        [Tooltip("Small boundary tolerance in metres. Invalid or excessive values fail closed instead of disabling the booth check.")]
        public float BoothBoundaryTolerance = StandardBoothBoundaryTolerance;

        [Header("Optional Udon callbacks")]
        [Tooltip("Receives the named custom events below. Read this loader's public status fields in the callback.")]
        public UdonBehaviour CallbackReceiver;

        public string LoadStartedEvent = "OnRemoteCatalogLoadStarted";
        public string LoadSucceededEvent = "OnRemoteCatalogLoadSucceeded";
        public string LoadFailedEvent = "OnRemoteCatalogLoadFailed";
        public string ClearedEvent = "OnRemoteCatalogCleared";

        [Header("Runtime state (read only)")]
        [HideInInspector] public int Status = StatusIdle;
        [HideInInspector] public string StatusMessage = "Idle";
        [HideInInspector] public string LastError = "";
        [HideInInspector] public int LastHttpErrorCode;
        [HideInInspector] public int LoadedSlot = -1;
        [HideInInspector] public int LoadedVertexCount;
        [HideInInspector] public int LoadedIndexCount;
        [HideInInspector] public bool LoadedHasTexture;

        [Header("Diagnostics")]
        public bool VerboseLogging;

        private bool _isLoading;
        private bool _discardPending;
        private int _pendingSlot = -1;
        private string _pendingUrl = "";

        private Mesh _runtimeMesh;
        private Material _runtimeMaterial;
        private Texture2D _runtimeTexture;

        private byte[] _readData;
        private int _readOffset;
        private string _parseError = "";

        private void Start()
        {
            if (TargetRenderer != null)
            {
                TargetRenderer.enabled = false;
            }

            if (LoadOnStart)
            {
                float delay = LoadOnStartDelaySeconds;
                if (!IsFiniteBounded(delay, 3600f) || delay < 0f)
                {
                    delay = 0f;
                }

                if (delay > 0f)
                {
                    SendCustomEventDelayedSeconds(nameof(LoadSelectedSlot), delay);
                }
                else
                {
                    LoadSelectedSlot();
                }
            }
        }

        public override void Interact()
        {
            if (LoadOnInteract)
            {
                LoadSelectedSlot();
            }
        }

        /// <summary>Loads the inspector-selected fixed URL slot.</summary>
        public void LoadSelectedSlot()
        {
            LoadSlot(SelectedSlot);
        }

        /// <summary>
        /// Selects and loads a fixed slot. This is useful when another Udon
        /// behaviour owns the catalogue UI and calls this method directly.
        /// </summary>
        public void LoadSlot(int slot)
        {
            if (_isLoading)
            {
                LastError = "A RAC1 download is already in progress.";
                Debug.LogWarning("[RemoteAvatarCatalog] " + LastError);
                return;
            }

            if (CatalogUrls == null || CatalogUrls.Length == 0)
            {
                ReportError("No fixed RAC1 URL slots are configured.", 0);
                return;
            }

            if (slot < 0 || slot >= CatalogUrls.Length)
            {
                ReportError("RAC1 URL slot is outside the configured range: " + slot, 0);
                return;
            }

            VRCUrl url = CatalogUrls[slot];
            if (url == null)
            {
                ReportError("RAC1 URL slot " + slot + " is empty.", 0);
                return;
            }

            string address = url.Get();
            if (address == null || address.Length == 0)
            {
                ReportError("RAC1 URL slot " + slot + " is empty.", 0);
                return;
            }

            _isLoading = true;
            _discardPending = false;
            _pendingSlot = slot;
            _pendingUrl = address;
            SelectedSlot = slot;

            LastError = "";
            LastHttpErrorCode = 0;
            Status = StatusLoading;
            StatusMessage = "Loading slot " + slot;
            Notify(LoadStartedEvent);

            if (VerboseLogging)
            {
                Debug.Log("[RemoteAvatarCatalog] Loading fixed slot " + slot + ": " + address);
            }

            VRCStringDownloader.LoadUrl(url, (IUdonEventReceiver)this);
        }

        /// <summary>Moves the selection to the next fixed slot without loading it.</summary>
        public void SelectNextSlot()
        {
            if (CatalogUrls == null || CatalogUrls.Length == 0)
            {
                SelectedSlot = 0;
                return;
            }

            SelectedSlot++;
            if (SelectedSlot >= CatalogUrls.Length)
            {
                SelectedSlot = 0;
            }
        }

        /// <summary>Moves the selection to the previous fixed slot without loading it.</summary>
        public void SelectPreviousSlot()
        {
            if (CatalogUrls == null || CatalogUrls.Length == 0)
            {
                SelectedSlot = 0;
                return;
            }

            SelectedSlot--;
            if (SelectedSlot < 0)
            {
                SelectedSlot = CatalogUrls.Length - 1;
            }
        }

        public void LoadNextSlot()
        {
            SelectNextSlot();
            LoadSelectedSlot();
        }

        public void LoadPreviousSlot()
        {
            SelectPreviousSlot();
            LoadSelectedSlot();
        }

        /// <summary>
        /// Clears the displayed asset. VRCStringDownloader has no cancellation
        /// API, so an in-flight result is marked for discard. A second request
        /// is intentionally blocked until that callback arrives; this prevents
        /// stale callbacks (especially repeated requests to the same URL) from
        /// replacing a newer selection.
        /// </summary>
        public void Clear()
        {
            ClearDisplayResources();

            LoadedSlot = -1;
            LoadedVertexCount = 0;
            LoadedIndexCount = 0;
            LoadedHasTexture = false;
            LastError = "";
            LastHttpErrorCode = 0;

            if (_isLoading)
            {
                _discardPending = true;
                Status = StatusDiscarding;
                StatusMessage = "Cleared; waiting to discard the active download";
            }
            else
            {
                Status = StatusIdle;
                StatusMessage = "Idle";
            }

            Notify(ClearedEvent);
        }

        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            if (!_isLoading)
            {
                return;
            }

            _isLoading = false;

            if (_discardPending)
            {
                FinishDiscard();
                return;
            }

            string returnedUrl = result.Url == null ? "" : result.Url.Get();
            if (returnedUrl != _pendingUrl)
            {
                ReportError("Ignored a RAC1 callback for an unexpected URL.", 0);
                ResetPendingRequest();
                return;
            }

            byte[] bytes = result.ResultBytes;
            if (bytes == null)
            {
                ReportError("The RAC1 response did not contain any bytes.", 0);
                ResetPendingRequest();
                return;
            }

            int downloadCap = EffectiveProfileLimit(
                MaxDownloadBytes,
                HardMaxDownloadBytes,
                StandardBoothMaxDownloadBytes);
            if (bytes.Length > downloadCap)
            {
                ReportError("RAC1 response exceeds MaxDownloadBytes (" + bytes.Length + " > " + downloadCap + ").", 0);
                ResetPendingRequest();
                return;
            }

            int completedSlot = _pendingSlot;
            bool loaded = ParseAndApply(bytes, completedSlot);
            ResetPendingRequest();

            if (!loaded)
            {
                ReportError(_parseError, 0);
                return;
            }

            Status = StatusReady;
            StatusMessage = "Ready: slot " + completedSlot;
            LastError = "";
            LastHttpErrorCode = 0;

            if (VerboseLogging)
            {
                Debug.Log("[RemoteAvatarCatalog] Loaded slot " + completedSlot + " (" + LoadedVertexCount + " vertices, " + LoadedIndexCount + " indices).");
            }

            Notify(LoadSucceededEvent);
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            if (!_isLoading)
            {
                return;
            }

            _isLoading = false;

            if (_discardPending)
            {
                FinishDiscard();
                return;
            }

            int errorCode = result.ErrorCode;
            string errorText = result.Error;
            ResetPendingRequest();
            ReportError("RAC1 download failed: " + errorText, errorCode);
        }

        private bool ParseAndApply(byte[] data, int sourceSlot)
        {
            _parseError = "";

            if (TargetMeshFilter == null || TargetRenderer == null)
            {
                return ParseFail("TargetMeshFilter and TargetRenderer are both required.");
            }

            if (MaterialTemplate == null && TargetRenderer.sharedMaterial == null && _runtimeMaterial == null)
            {
                return ParseFail("Assign MaterialTemplate or a material on TargetRenderer.");
            }

            if (EnforceStandardBoothProfile && data.Length > StandardBoothMaxDownloadBytes)
            {
                return ParseFail("RAC1 file exceeds the standard booth file cap (" +
                                 data.Length + " > " + StandardBoothMaxDownloadBytes + ").");
            }

            if (data.Length < HeaderBytes)
            {
                return ParseFail("RAC1 file is shorter than its 60-byte header.");
            }

            if (data[0] != 0x52 || data[1] != 0x41 || data[2] != 0x43 || data[3] != 0x31)
            {
                return ParseFail("RAC1 magic is missing.");
            }

            _readData = data;
            _readOffset = 4;

            uint version = ReadUInt32LittleEndian();
            uint flags = ReadUInt32LittleEndian();
            uint vertexCountRaw = ReadUInt32LittleEndian();
            uint indexCountRaw = ReadUInt32LittleEndian();

            if (version != RacVersion)
            {
                return ParseFail("Unsupported RAC version: " + version);
            }

            if ((flags & ~KnownFlags) != 0u)
            {
                return ParseFail("RAC1 contains unknown flag bits: " + flags);
            }

            int vertexCap = EffectiveProfileLimit(
                MaxVertices,
                HardMaxVertices,
                StandardBoothMaxVertices);
            int indexCap = EffectiveProfileLimit(
                MaxIndices,
                HardMaxIndices,
                StandardBoothMaxIndices);
            if (vertexCountRaw == 0u || vertexCountRaw > (uint)vertexCap)
            {
                return ParseFail("RAC1 vertex count is zero or exceeds the configured cap: " + vertexCountRaw);
            }

            if (indexCountRaw == 0u || indexCountRaw > (uint)indexCap)
            {
                return ParseFail("RAC1 index count is zero or exceeds the configured cap: " + indexCountRaw);
            }

            int vertexCount = (int)vertexCountRaw;
            int indexCount = (int)indexCountRaw;

            if ((indexCount % 3) != 0)
            {
                return ParseFail("RAC1 index count is not a triangle list (not divisible by 3).");
            }

            Vector3 boundsCenter = new Vector3(ReadSingleLittleEndian(), ReadSingleLittleEndian(), ReadSingleLittleEndian());
            Vector3 boundsSize = new Vector3(ReadSingleLittleEndian(), ReadSingleLittleEndian(), ReadSingleLittleEndian());
            Color baseColor = new Color(ReadSingleLittleEndian(), ReadSingleLittleEndian(), ReadSingleLittleEndian(), ReadSingleLittleEndian());

            float coordinateCap = EffectivePositiveFloat(MaxAbsCoordinate, 1000000f);
            float uvCap = EffectivePositiveFloat(MaxAbsUv, 1000000f);
            if (!IsFiniteBounded(boundsCenter.x, coordinateCap) ||
                !IsFiniteBounded(boundsCenter.y, coordinateCap) ||
                !IsFiniteBounded(boundsCenter.z, coordinateCap))
            {
                return ParseFail("RAC1 bounds center is not finite or is outside MaxAbsCoordinate.");
            }

            if (!IsFiniteBounded(boundsSize.x, coordinateCap * 2f) ||
                !IsFiniteBounded(boundsSize.y, coordinateCap * 2f) ||
                !IsFiniteBounded(boundsSize.z, coordinateCap * 2f) ||
                boundsSize.x < 0f || boundsSize.y < 0f || boundsSize.z < 0f)
            {
                return ParseFail("RAC1 bounds size is negative, non-finite, or too large.");
            }

            if (!IsFiniteBounded(baseColor.r, 1024f) ||
                !IsFiniteBounded(baseColor.g, 1024f) ||
                !IsFiniteBounded(baseColor.b, 1024f) ||
                !IsFiniteBounded(baseColor.a, 1024f))
            {
                return ParseFail("RAC1 base color contains an invalid float.");
            }

            bool hasNormals = (flags & FlagHasNormals) != 0u;
            bool hasUv0 = (flags & FlagHasUv0) != 0u;
            bool hasColors = (flags & FlagHasColors) != 0u;
            if (EnforceStandardBoothProfile)
            {
                if (!IsFiniteBounded(BoothWidth, StandardBoothWidth) || BoothWidth <= 0f ||
                    !IsFiniteBounded(BoothDepth, StandardBoothDepth) || BoothDepth <= 0f ||
                    !IsFiniteBounded(BoothHeight, StandardBoothHeight) || BoothHeight <= 0f)
                {
                    return ParseFail(
                        "The standard booth dimensions must be finite positive values no larger than " +
                        StandardBoothWidth + "m wide, " + StandardBoothDepth + "m deep, and " + StandardBoothHeight + "m high.");
                }

                if (!IsFiniteBounded(BoothBoundaryTolerance, StandardBoothBoundaryTolerance) ||
                    BoothBoundaryTolerance < 0f)
                {
                    return ParseFail("BoothBoundaryTolerance must be finite and between 0 and " + StandardBoothBoundaryTolerance + " metres for the standard booth profile.");
                }
            }

            bool hasTexture = (flags & FlagHasTextureRgba32) != 0u;

            // Validate the complete byte layout before allocating the large
            // vertex arrays. All counts are capped above, and calculations use
            // long to prevent integer wraparound.
            long bodyEnd = HeaderBytes;
            bodyEnd += (long)vertexCount * 12L;
            if (hasNormals) bodyEnd += (long)vertexCount * 12L;
            if (hasUv0) bodyEnd += (long)vertexCount * 8L;
            if (hasColors) bodyEnd += (long)vertexCount * 4L;
            bodyEnd += (long)indexCount * 4L;

            if (bodyEnd > data.Length)
            {
                return ParseFail("RAC1 payload is truncated before the end of its mesh arrays.");
            }

            int textureWidth = 0;
            int textureHeight = 0;
            int textureByteLength = 0;
            if (hasTexture)
            {
                if (bodyEnd + 12L > data.Length)
                {
                    return ParseFail("RAC1 texture header is truncated.");
                }

                int textureHeaderOffset = (int)bodyEnd;
                uint widthRaw = ReadUInt32At(textureHeaderOffset);
                uint heightRaw = ReadUInt32At(textureHeaderOffset + 4);
                uint byteLengthRaw = ReadUInt32At(textureHeaderOffset + 8);

                int dimensionCap = EffectiveProfileLimit(
                    MaxTextureDimension,
                    HardMaxTextureDimension,
                    StandardBoothMaxTextureDimension);
                int textureBytesCap = EffectiveProfileLimit(
                    MaxTextureBytes,
                    HardMaxTextureBytes,
                    StandardBoothMaxTextureBytes);
                if (widthRaw == 0u || widthRaw > (uint)dimensionCap || heightRaw == 0u || heightRaw > (uint)dimensionCap)
                {
                    return ParseFail("RAC1 texture dimensions are zero or exceed MaxTextureDimension.");
                }

                long requiredTextureBytes = (long)widthRaw * (long)heightRaw * 4L;
                if (requiredTextureBytes > textureBytesCap || byteLengthRaw != (uint)requiredTextureBytes)
                {
                    return ParseFail("RAC1 texture byteLength must equal width * height * 4 and remain within the configured cap.");
                }

                long expectedLength = bodyEnd + 12L + requiredTextureBytes;
                if (expectedLength != data.Length)
                {
                    return ParseFail("RAC1 texture payload is truncated or has trailing bytes.");
                }

                textureWidth = (int)widthRaw;
                textureHeight = (int)heightRaw;
                textureByteLength = (int)byteLengthRaw;
            }
            else if (bodyEnd != data.Length)
            {
                return ParseFail("RAC1 mesh payload has unexpected trailing bytes.");
            }

            // The header has already been consumed, so _readOffset now points
            // at the mandatory position array.
            Vector3[] positions = new Vector3[vertexCount];
            Vector3 actualBoundsMin = Vector3.zero;
            Vector3 actualBoundsMax = Vector3.zero;
            int i;
            for (i = 0; i < vertexCount; i++)
            {
                float x = ReadSingleLittleEndian();
                float y = ReadSingleLittleEndian();
                float z = ReadSingleLittleEndian();
                if (!IsFiniteBounded(x, coordinateCap) || !IsFiniteBounded(y, coordinateCap) || !IsFiniteBounded(z, coordinateCap))
                {
                    return ParseFail("RAC1 position " + i + " is not finite or exceeds MaxAbsCoordinate.");
                }

                Vector3 position = new Vector3(x, y, z);
                positions[i] = position;
                if (i == 0)
                {
                    actualBoundsMin = position;
                    actualBoundsMax = position;
                }
                else
                {
                    if (position.x < actualBoundsMin.x) actualBoundsMin.x = position.x;
                    if (position.y < actualBoundsMin.y) actualBoundsMin.y = position.y;
                    if (position.z < actualBoundsMin.z) actualBoundsMin.z = position.z;
                    if (position.x > actualBoundsMax.x) actualBoundsMax.x = position.x;
                    if (position.y > actualBoundsMax.y) actualBoundsMax.y = position.y;
                    if (position.z > actualBoundsMax.z) actualBoundsMax.z = position.z;
                }
            }

            Vector3 actualBoundsCenter = (actualBoundsMin + actualBoundsMax) * 0.5f;
            Vector3 actualBoundsSize = actualBoundsMax - actualBoundsMin;
            if (!HeaderBoundsAreConsistent(boundsCenter, boundsSize, actualBoundsMin, actualBoundsMax))
            {
                return ParseFail("RAC1 header bounds do not consistently enclose the decoded positions.");
            }

            if (EnforceStandardBoothProfile &&
                !ActualBoundsFitBooth(actualBoundsMin, actualBoundsMax, BoothBoundaryTolerance))
            {
                return ParseFail(
                    "RAC1 geometry is outside the configured floor-centred booth volume (" +
                    BoothWidth + "m wide, " + BoothDepth + "m deep, " + BoothHeight + "m high).");
            }

            Vector3[] normals = new Vector3[0];
            if (hasNormals)
            {
                normals = new Vector3[vertexCount];
                for (i = 0; i < vertexCount; i++)
                {
                    float x = ReadSingleLittleEndian();
                    float y = ReadSingleLittleEndian();
                    float z = ReadSingleLittleEndian();
                    if (!IsFiniteBounded(x, 16f) || !IsFiniteBounded(y, 16f) || !IsFiniteBounded(z, 16f))
                    {
                        return ParseFail("RAC1 normal " + i + " contains an invalid float.");
                    }
                    normals[i] = new Vector3(x, y, z);
                }
            }

            Vector2[] uv0 = new Vector2[0];
            if (hasUv0)
            {
                uv0 = new Vector2[vertexCount];
                for (i = 0; i < vertexCount; i++)
                {
                    float u = ReadSingleLittleEndian();
                    float v = ReadSingleLittleEndian();
                    if (!IsFiniteBounded(u, uvCap) || !IsFiniteBounded(v, uvCap))
                    {
                        return ParseFail("RAC1 UV0 value " + i + " is not finite or exceeds MaxAbsUv.");
                    }
                    uv0[i] = new Vector2(u, v);
                }
            }

            Color32[] colors = new Color32[0];
            if (hasColors)
            {
                colors = new Color32[vertexCount];
                for (i = 0; i < vertexCount; i++)
                {
                    colors[i] = new Color32(_readData[_readOffset], _readData[_readOffset + 1], _readData[_readOffset + 2], _readData[_readOffset + 3]);
                    _readOffset += 4;
                }
            }

            int[] triangles = new int[indexCount];
            for (i = 0; i < indexCount; i++)
            {
                uint indexRaw = ReadUInt32LittleEndian();
                if (indexRaw >= vertexCountRaw)
                {
                    return ParseFail("RAC1 index " + i + " references a missing vertex: " + indexRaw);
                }
                triangles[i] = (int)indexRaw;
            }

            byte[] textureBytes = new byte[0];
            if (hasTexture)
            {
                // Texture metadata was checked above; consume it now and copy
                // the raw section because Texture2D.LoadRawTextureData requires
                // a standalone byte array in Udon.
                ReadUInt32LittleEndian();
                ReadUInt32LittleEndian();
                ReadUInt32LittleEndian();
                textureBytes = new byte[textureByteLength];
                Buffer.BlockCopy(_readData, _readOffset, textureBytes, 0, textureByteLength);
                _readOffset += textureByteLength;
            }

            if (_readOffset != data.Length)
            {
                return ParseFail("Internal RAC1 parser offset did not reach end-of-file.");
            }

            // Everything is validated before touching the currently displayed
            // object. A malformed remote file therefore cannot erase the last
            // successfully loaded catalogue item.
            Texture2D nextTexture = null;
            if (hasTexture)
            {
                nextTexture = new Texture2D(textureWidth, textureHeight, TextureFormat.RGBA32, false);
                nextTexture.name = "RemoteAvatarCatalog Texture";
                nextTexture.LoadRawTextureData(textureBytes);
                nextTexture.Apply(false, false);
            }

            if (_runtimeMesh == null)
            {
                _runtimeMesh = new Mesh();
                _runtimeMesh.name = "RemoteAvatarCatalog Mesh";
            }
            else
            {
                _runtimeMesh.Clear();
            }

            _runtimeMesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _runtimeMesh.vertices = positions;
            if (hasNormals)
            {
                _runtimeMesh.normals = normals;
            }
            if (hasUv0)
            {
                _runtimeMesh.uv = uv0;
            }
            if (hasColors)
            {
                _runtimeMesh.colors32 = colors;
            }
            _runtimeMesh.triangles = triangles;
            if (!hasNormals && RecalculateMissingNormals)
            {
                _runtimeMesh.RecalculateNormals();
            }
            // Use bounds derived from validated positions. The remote header
            // remains an integrity cross-check and cannot control culling.
            _runtimeMesh.bounds = new Bounds(actualBoundsCenter, actualBoundsSize);

            EnsureRuntimeMaterial();
            if (_runtimeMaterial == null)
            {
                if (nextTexture != null)
                {
                    Destroy(nextTexture);
                }
                return ParseFail("Could not create a runtime material instance.");
            }

            if (BaseColorProperty != null && BaseColorProperty.Length > 0 && _runtimeMaterial.HasProperty(BaseColorProperty))
            {
                _runtimeMaterial.SetColor(BaseColorProperty, baseColor);
            }
            if (MainTextureProperty != null && MainTextureProperty.Length > 0 && _runtimeMaterial.HasProperty(MainTextureProperty))
            {
                _runtimeMaterial.SetTexture(MainTextureProperty, nextTexture);
            }

            if (_runtimeTexture != null)
            {
                Destroy(_runtimeTexture);
            }
            _runtimeTexture = nextTexture;

            TargetMeshFilter.sharedMesh = _runtimeMesh;
            TargetRenderer.sharedMaterial = _runtimeMaterial;
            TargetRenderer.enabled = true;

            LoadedSlot = sourceSlot;
            LoadedVertexCount = vertexCount;
            LoadedIndexCount = indexCount;
            LoadedHasTexture = hasTexture;

            _readData = null;
            return true;
        }

        private void EnsureRuntimeMaterial()
        {
            if (_runtimeMaterial != null)
            {
                return;
            }

            if (MaterialTemplate != null)
            {
                TargetRenderer.sharedMaterial = MaterialTemplate;
            }

            // Renderer.material returns a private instance, so remote colour and
            // texture changes never mutate the world author's shared asset.
            if (TargetRenderer != null && TargetRenderer.sharedMaterial != null)
            {
                _runtimeMaterial = TargetRenderer.material;
                _runtimeMaterial.name = "RemoteAvatarCatalog Material";
            }
        }

        private void ClearDisplayResources()
        {
            if (TargetRenderer != null)
            {
                TargetRenderer.enabled = false;
            }

            if (_runtimeMesh != null)
            {
                _runtimeMesh.Clear();
            }

            if (_runtimeMaterial != null && MainTextureProperty != null && MainTextureProperty.Length > 0 && _runtimeMaterial.HasProperty(MainTextureProperty))
            {
                _runtimeMaterial.SetTexture(MainTextureProperty, null);
            }

            if (_runtimeTexture != null)
            {
                Destroy(_runtimeTexture);
                _runtimeTexture = null;
            }
        }

        private void FinishDiscard()
        {
            _discardPending = false;
            ResetPendingRequest();
            Status = StatusIdle;
            StatusMessage = "Idle";
        }

        private void ResetPendingRequest()
        {
            _pendingSlot = -1;
            _pendingUrl = "";
            _discardPending = false;
        }

        private void ReportError(string message, int httpErrorCode)
        {
            if (message == null || message.Length == 0)
            {
                message = "Unknown RAC1 error.";
            }

            LastError = message;
            LastHttpErrorCode = httpErrorCode;
            Status = StatusError;
            StatusMessage = message;
            Debug.LogError("[RemoteAvatarCatalog] " + message);
            Notify(LoadFailedEvent);
        }

        private bool ParseFail(string message)
        {
            _parseError = message;
            _readData = null;
            return false;
        }

        private void Notify(string eventName)
        {
            if (CallbackReceiver != null && eventName != null && eventName.Length > 0)
            {
                CallbackReceiver.SendCustomEvent(eventName);
            }
        }

        private uint ReadUInt32LittleEndian()
        {
            uint value = ReadUInt32At(_readOffset);
            _readOffset += 4;
            return value;
        }

        private uint ReadUInt32At(int offset)
        {
            return (uint)_readData[offset]
                | ((uint)_readData[offset + 1] << 8)
                | ((uint)_readData[offset + 2] << 16)
                | ((uint)_readData[offset + 3] << 24);
        }

        private float ReadSingleLittleEndian()
        {
            // All currently supported VRChat targets are little-endian. Using
            // BitConverter directly avoids allocating a four-byte buffer for
            // every component of a large mesh.
            float value = BitConverter.ToSingle(_readData, _readOffset);
            _readOffset += 4;
            return value;
        }

        private bool IsFiniteBounded(float value, float absoluteLimit)
        {
            // value != value catches NaN. Either comparison catches infinities
            // as well as deliberately extreme hostile payload values.
            return value == value && value <= absoluteLimit && value >= -absoluteLimit;
        }

        private int EffectivePositiveLimit(int configured, int hardLimit)
        {
            if (configured <= 0 || configured > hardLimit)
            {
                return hardLimit;
            }
            return configured;
        }

        private int EffectiveProfileLimit(int configured, int hardLimit, int standardBoothLimit)
        {
            int effective = EffectivePositiveLimit(configured, hardLimit);
            if (EnforceStandardBoothProfile && effective > standardBoothLimit)
            {
                return standardBoothLimit;
            }

            return effective;
        }

        private bool HeaderBoundsAreConsistent(
            Vector3 headerCenter,
            Vector3 headerSize,
            Vector3 actualMinimum,
            Vector3 actualMaximum)
        {
            Vector3 headerExtents = headerSize * 0.5f;
            Vector3 headerMinimum = headerCenter - headerExtents;
            Vector3 headerMaximum = headerCenter + headerExtents;

            if (actualMinimum.x < headerMinimum.x - HeaderBoundsContainmentTolerance ||
                actualMinimum.y < headerMinimum.y - HeaderBoundsContainmentTolerance ||
                actualMinimum.z < headerMinimum.z - HeaderBoundsContainmentTolerance ||
                actualMaximum.x > headerMaximum.x + HeaderBoundsContainmentTolerance ||
                actualMaximum.y > headerMaximum.y + HeaderBoundsContainmentTolerance ||
                actualMaximum.z > headerMaximum.z + HeaderBoundsContainmentTolerance)
            {
                return false;
            }

            // Unity may conservatively pad baked SkinnedMeshRenderer bounds.
            // Existing RAC1 exports use up to roughly eight centimetres.
            float maximumPadding = MaximumHeaderBoundsPadding + HeaderBoundsContainmentTolerance;
            return actualMinimum.x - headerMinimum.x <= maximumPadding &&
                   actualMinimum.y - headerMinimum.y <= maximumPadding &&
                   actualMinimum.z - headerMinimum.z <= maximumPadding &&
                   headerMaximum.x - actualMaximum.x <= maximumPadding &&
                   headerMaximum.y - actualMaximum.y <= maximumPadding &&
                   headerMaximum.z - actualMaximum.z <= maximumPadding;
        }

        private bool ActualBoundsFitBooth(
            Vector3 actualMinimum,
            Vector3 actualMaximum,
            float tolerance)
        {
            float halfWidth = BoothWidth * 0.5f;
            float halfDepth = BoothDepth * 0.5f;
            return actualMinimum.x >= -halfWidth - tolerance &&
                   actualMaximum.x <= halfWidth + tolerance &&
                   actualMinimum.y >= -tolerance &&
                   actualMaximum.y <= BoothHeight + tolerance &&
                   actualMinimum.z >= -halfDepth - tolerance &&
                   actualMaximum.z <= halfDepth + tolerance;
        }

        private float EffectivePositiveFloat(float configured, float fallback)
        {
            if (!(configured > 0f) || configured > fallback)
            {
                return fallback;
            }
            return configured;
        }
    }
}
