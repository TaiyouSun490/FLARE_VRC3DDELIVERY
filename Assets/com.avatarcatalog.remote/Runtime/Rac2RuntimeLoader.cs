using System;
using System.Text;
using UdonSharp;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDK3.StringLoading;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace AvatarCatalog.Remote
{
    /// <summary>Strict, allocation-bounded RAC2 v2 static, VAT, and RAW/LZ4 loader.</summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public sealed class Rac2RuntimeLoader : UdonSharpBehaviour
    {
        public const int StatusIdle = 0;
        public const int StatusLoading = 1;
        public const int StatusReady = 2;
        public const int StatusError = 3;
        public const int LoadPerformanceSmooth = 0;
        public const int LoadPerformanceBalanced = 1;
        public const int LoadPerformanceFast = 2;

        [Header("Source")]
        public VRCUrl RuntimeUrl;

        [Header("Loading performance")]
        [Tooltip("Continuously adapts decompression work to the local client's measured frame time.")]
        public bool AdaptiveLoadingEnabled = true;
        [Tooltip("Used only when adaptive loading is disabled: 0 = Smooth, 1 = Balanced, 2 = Fast.")]
        [Range(0, 2)] public int FixedLoadingPerformanceMode = LoadPerformanceBalanced;
        [Tooltip("Allowed frame-time increase relative to the pre-load baseline.")]
        [Range(0.05f, 0.5f)] public float AdaptiveAllowedFrameTimeIncrease = 0.12f;

        [Header("Display")]
        public MeshFilter TargetMeshFilter;
        public MeshRenderer TargetRenderer;
        public Rac2ParticlePlayer ParticlePlayer;
        public Rac2ProductController ProductController;
        public BoxCollider TargetCollider;
        public Rigidbody TargetRigidbody;
        public VRCPickup TargetPickup;
        public VRCObjectSync TargetObjectSync;
        public Vector3 InitialLocalPosition;
        public Vector3 InitialLocalEulerAngles;
        [Tooltip("Fallback used by legacy RAC2 files without MATL.")]
        public Material MaterialTemplate;
        [Tooltip("Required for RAC2 lilToon Opaque.")]
        public Material LilToonOpaqueTemplate;
        [Tooltip("Required for RAC2 lilToon Cutout.")]
        public Material LilToonCutoutTemplate;
        [Tooltip("Required for RAC2 VAT lilToon Opaque.")]
        public Material VatOpaqueTemplate;
        [Tooltip("Required for RAC2 VAT lilToon Cutout.")]
        public Material VatCutoutTemplate;
        [Range(0f, 4f)] public float VatPlaybackSpeed = 1f;
        public string MainTextureProperty = "_MainTex";
        public string BaseColorProperty = "_Color";

        [Header("Standard booth profile")]
        public bool EnforceStandardBoothProfile = true;
        public float BoothWidth = 3f;
        public float BoothDepth = 3f;
        public float BoothHeight = 2.7f;
        [Tooltip("Ground-plane tolerance for VAT foot motion and bake padding.")]
        public float BoothBoundaryTolerance = 0.1f;

        [Header("Callbacks")]
        public UdonBehaviour CallbackReceiver;
        public string LoadStartedEvent = "OnRac2LoadStarted";
        public string LoadSucceededEvent = "OnRac2LoadSucceeded";
        public string LoadFailedEvent = "OnRac2LoadFailed";
        public string ClearedEvent = "OnRac2Cleared";

        [Header("Diagnostics")]
        public int Status;
        public string StatusMessage = "Idle";
        public string LastError = "";
        public int LastHttpErrorCode;
        public int LoadedVertexCount;
        public int LoadedIndexCount;
        public bool LoadedHasTexture;
        public bool LoadedHasNormalMap;
        public int LoadedMaterialProfile;
        public bool LoadedHasVat;
        public int LoadedVatFrameCount;
        public float LoadedVatFramesPerSecond;
        public bool LoadedWasCompressed;
        public int LoadedStoredBytes;
        public int LoadedDecodedBytes;
        public bool LoadedHasParticles;
        public int LoadedParticleMaximum;
        public bool LoadedHasCollider;
        public bool LoadedIsPortable;
        public bool LoadedHasProduct;
        public string LoadedProductName = "";
        public string LoadedCreatorName = "";
        public string LoadedProductUrl = "";
        public string LoadedAvatarBlueprintId = "";
        public bool LoadedTrialEnabled;
        public float AdaptiveBaselineFps;
        public float AdaptiveMeasuredFps;
        public float AdaptiveLoadScale = 0.5f;
        public int CurrentExpansionTokenBudget = 16384;
        public int CurrentChecksumByteBudget = 65536;
        [Tooltip("Maximum decoded mesh attribute/index elements per frame at full adaptive budget.")]
        [Range(16, 8192)] public int MeshElementsPerFrame = 1024;
        [Tooltip("Maximum LZ4 input/output work bytes per frame at full adaptive budget.")]
        [Range(4096, 1048576)] public int DecompressionBytesPerFrame = 262144;

        private const int HeaderBytes = 24;
        private const int TocEntryBytes = 16;
        private const int CompressedTocEntryBytes = 24;
        private const uint CompressedContainerFlag = 1u;
        private const int MetaBytes = 40;
        private const int MeshHeaderBytes = 16;
        private const int TextureHeaderBytes = 16;
        private const int MaterialBytes = 48;
        private const int VatInfoBaseBytes = 64;
        private const int VatTextureHeaderBytes = 20;
        private const int ParticleBytes = 140;
        private const int InteractionBytes = 8;
        private const int ProductHeaderBytes = 24;
        private const uint Magic = 0x32434152u;
        private const uint MetaType = 0x4154454du;
        private const uint MeshType = 0x4853454du;
        private const uint MatlType = 0x4c54414du;
        private const uint Tex0Type = 0x30584554u;
        private const uint TexnType = 0x4e584554u;
        private const uint VatiType = 0x49544156u;
        private const uint VatpType = 0x50544156u;
        private const uint VatnType = 0x4e544156u;
        private const uint PartType = 0x54524150u;
        private const uint PtexType = 0x58455450u;
        private const uint IntrType = 0x52544e49u;
        private const uint ProdType = 0x444f5250u;
        private const uint AttrNormals = 1u;
        private const uint AttrUv0 = 2u;
        private const uint AttrColors = 4u;
        private const uint AttrTangents = 8u;
        private const uint AttrUv1 = 16u;
        private const uint KnownAttributes = 31u;
        private const int StandardMaxBytes = 67108864;
        private const int StandardMaxVertices = 40000;
        private const int StandardMaxIndices = 120000;
        private const int StandardMaxTextureDimension = 1024;
        private const int StandardMaxTextureBytes = 4194304;
        private const int MinimumExpansionTokenBudgetPerFrame = 1024;
        private const int SmoothExpansionTokenBudgetPerFrame = 8192;
        private const int BalancedExpansionTokenBudgetPerFrame = 16384;
        private const int FastExpansionTokenBudgetPerFrame = 32768;
        private const int MinimumChecksumByteBudgetPerFrame = 4096;
        private const int SmoothChecksumByteBudgetPerFrame = 32768;
        private const int BalancedChecksumByteBudgetPerFrame = 65536;
        private const int FastChecksumByteBudgetPerFrame = 131072;

        private byte[] _data;
        private int _cursor;
        private Mesh _mesh;
        private Material _material;
        private Material _materialSource;
        private Texture2D _texture;
        private Texture2D _normalTexture;
        private Texture2D _vatPositionTexture;
        private Texture2D _vatNormalTexture;
        private Texture2D _particleTexture;
        private string _parseError;

        // Incremental compressed-container state. Large VAT payloads must yield
        // between chunks to stay below VRChat's per-event Udon VM time limit.
        private byte[] _expandSource;
        private byte[] _expandTarget;
        private int _expandSectionCount;
        private int _expandSectionIndex;
        private int _expandRawOffset;
        private int _expandStoredBytes;
        private int _expandPhase;
        private int _lzPhase;
        private int _lzToken;
        private int _lzRemaining;
        private int _lzReference;
        private int _expandInput;
        private int _expandInputEnd;
        private int _expandOutput;
        private int _expandOutputEnd;
        private int _expandTargetStart;
        private int _expandChecksumOffset;
        private int _expandChecksumEnd;
        private int _expandChecksumBlockEnd;
        private bool _standardChecksum;
        private int _expandChecksumA;
        private int _expandChecksumB;
        private uint _expandExpectedChecksum;
        private float _idleFrameSeconds;
        private bool _hasIdleFrameSample;
        private float _loadingFrameSeconds;
#if UNITY_EDITOR
        [NonSerialized] public byte[] EditorTestPayload;

        public void RunEditorTestPayload()
        {
            byte[] bytes = EditorTestPayload;
            if (bytes == null || bytes.Length == 0)
            {
                ReportError("RAC2 editor test payload is empty.", 0);
                return;
            }
            PrepareForLoad();
            ClearExpansionState();
            Status = StatusLoading;
            StatusMessage = "Running RAC2 Udon VM self-test...";
            LastError = "";
            _data = bytes;
            uint version = bytes.Length >= 8 ? ReadU32At(4) : 0u;
            if (bytes.Length < HeaderBytes + TocEntryBytes * 2 ||
                ReadU32At(0) != Magic ||
                (version != 2u && version != 3u) ||
                ReadU32At(8) != (uint)bytes.Length)
            {
                ReportError("RAC2 editor test header is invalid.", 0);
                return;
            }
            if ((ReadU32At(16) == CompressedContainerFlag || ReadU32At(16) == 3u))
            {
                if (!BeginCompressedExpansion(bytes))
                    ReportError(_parseError, 0);
                return;
            }
            FinishParseAndApply(bytes, false, bytes.Length);
        }
#endif

        private void Update()
        {
            if (Status == StatusLoading) return;
            float frameSeconds = Time.deltaTime;
            if (frameSeconds < 0.001f || frameSeconds > 0.25f) return;
            if (!_hasIdleFrameSample)
            {
                _idleFrameSeconds = frameSeconds;
                _hasIdleFrameSample = true;
            }
            else
            {
                _idleFrameSeconds = _idleFrameSeconds * 0.95f + frameSeconds * 0.05f;
            }
        }

        public void LoadRuntimeUrl()
        {
            if (Status == StatusLoading) return;
            if (VRCUrl.IsNullOrEmpty(RuntimeUrl)) { ReportError("Enter a RAC2 API URL first.", 0); return; }
            if (TargetMeshFilter == null || TargetRenderer == null) { ReportError("RAC2 display references are incomplete.", 0); return; }
            PrepareForLoad();
            ClearExpansionState();
            Status = StatusLoading;
            StatusMessage = "Downloading RAC2...";
            LastError = "";
            LastHttpErrorCode = 0;
            Notify(LoadStartedEvent);
            VRCStringDownloader.LoadUrl(RuntimeUrl, (IUdonEventReceiver)this);
        }

        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            byte[] bytes = result.ResultBytes;
            if (bytes == null || bytes.Length == 0) { ReportError("RAC2 response was empty.", 0); return; }
            if (bytes.Length > StandardMaxBytes) { ReportError("RAC2 exceeds the 64 MB VAT Pad limit.", 0); return; }

            _data = bytes;
            uint downloadedVersion = bytes.Length >= 8 ? ReadU32At(4) : 0u;
            if (bytes.Length < HeaderBytes + TocEntryBytes * 2 || ReadU32At(0) != Magic ||
                (downloadedVersion != 2u && downloadedVersion != 3u) || ReadU32At(8) != (uint)bytes.Length)
            {
                ReportError("RAC2 header is truncated or unsupported.", 0);
                return;
            }

            if ((ReadU32At(16) == CompressedContainerFlag || ReadU32At(16) == 3u))
            {
                if (!BeginCompressedExpansion(bytes)) ReportError(_parseError, 0);
                return;
            }

            FinishParseAndApply(bytes, false, bytes.Length);
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            ReportError("RAC2 download failed: " + result.Error, result.ErrorCode);
        }

        public void Clear()
        {
            PrepareForLoad();
            ClearExpansionState();
            if (TargetRenderer != null) TargetRenderer.enabled = false;
            if (ParticlePlayer != null) ParticlePlayer.ClearParticles();
            ClearBundleResources();
            if (_mesh != null) _mesh.Clear();
            if (_material != null)
            {
                if (_material.HasProperty(MainTextureProperty)) _material.SetTexture(MainTextureProperty, null);
                if (_material.HasProperty("_BumpMap")) _material.SetTexture("_BumpMap", null);
                if (_material.HasProperty("_UseBumpMap")) _material.SetFloat("_UseBumpMap", 0f);
                if (_material.HasProperty("_VatPositionTex")) _material.SetTexture("_VatPositionTex", null);
                if (_material.HasProperty("_VatNormalTex")) _material.SetTexture("_VatNormalTex", null);
            }
            if (_texture != null) { Destroy(_texture); _texture = null; }
            if (_normalTexture != null) { Destroy(_normalTexture); _normalTexture = null; }
            if (_vatPositionTexture != null) { Destroy(_vatPositionTexture); _vatPositionTexture = null; }
            if (_vatNormalTexture != null) { Destroy(_vatNormalTexture); _vatNormalTexture = null; }
            if (_particleTexture != null) { Destroy(_particleTexture); _particleTexture = null; }
            LoadedVertexCount = 0;
            LoadedIndexCount = 0;
            LoadedHasTexture = false;
            LoadedHasNormalMap = false;
            LoadedMaterialProfile = 0;
            LoadedHasVat = false;
            LoadedVatFrameCount = 0;
            LoadedVatFramesPerSecond = 0f;
            LoadedWasCompressed = false;
            LoadedStoredBytes = 0;
            LoadedDecodedBytes = 0;
            LoadedHasParticles = false;
            LoadedParticleMaximum = 0;
            LoadedRenderNodeCount = 0;
            LoadedMaterialCount = 0;
            LoadedParticleEmitterCount = 0;
            LoadedHasCollider = false;
            LoadedIsPortable = false;
            LoadedHasProduct = false;
            LoadedProductName = "";
            LoadedCreatorName = "";
            LoadedProductUrl = "";
            LoadedAvatarBlueprintId = "";
            LoadedTrialEnabled = false;
            Status = StatusIdle;
            StatusMessage = "Idle";
            LastError = "";
            Notify(ClearedEvent);
        }

        private bool ParseAndApply(byte[] bytes)
        {
            PrepareForLoad();
            _parseError = "Invalid RAC2.";
            LoadedWasCompressed = false;
            LoadedStoredBytes = bytes == null ? 0 : bytes.Length;
            LoadedDecodedBytes = 0;
            LoadedHasParticles = false;
            LoadedParticleMaximum = 0;
            LoadedHasCollider = false;
            LoadedIsPortable = false;
            LoadedHasProduct = false;
            LoadedProductName = "";
            LoadedCreatorName = "";
            LoadedProductUrl = "";
            LoadedAvatarBlueprintId = "";
            LoadedTrialEnabled = false;
            _data = bytes;
            if (bytes == null || bytes.Length < HeaderBytes + TocEntryBytes * 2) return Fail("RAC2 header is truncated.");
            uint formatVersion = ReadU32At(4);
            if (ReadU32At(0) != Magic || (formatVersion != 2u && formatVersion != 3u)) return Fail("RAC2 magic or version is unsupported.");
            if (ReadU32At(8) != (uint)bytes.Length) return Fail("RAC2 header is inconsistent.");

            uint containerFlags = ReadU32At(16);
            if ((containerFlags == CompressedContainerFlag || containerFlags == 3u))
            {
                byte[] expanded = ExpandCompressedContainer(bytes);
                if (expanded == null) return false;
                bytes = expanded;
                _data = expanded;
                LoadedWasCompressed = true;
                LoadedDecodedBytes = expanded.Length;
            }
            else if (containerFlags != 0u || ReadU32At(20) != 0u)
            {
                return Fail("RAC2 container flags are unsupported.");
            }
            else
            {
                LoadedDecodedBytes = bytes.Length;
            }

            if (ReadU32At(8) != (uint)bytes.Length || ReadU32At(16) != 0u || ReadU32At(20) != 0u)
                return Fail("RAC2 expanded header is inconsistent.");

            if (formatVersion == 3u) return ParseAndApplyBundle(bytes);
            ClearBundleResources();

            uint sectionCountRaw = ReadU32At(12);
            if (sectionCountRaw < 2u || sectionCountRaw > 12u) return Fail("RAC2 section count is unsupported.");
            int sectionCount = (int)sectionCountRaw;
            int expectedOffset = HeaderBytes + sectionCount * TocEntryBytes;
            int previousRank = -1;
            int metaOffset = 0;
            int metaLength = 0;
            int meshOffset = 0;
            int meshLength = 0;
            int materialOffset = 0;
            int materialLength = 0;
            int textureOffset = 0;
            int textureLength = 0;
            int normalOffset = 0;
            int normalLength = 0;
            int vatInfoOffset = 0;
            int vatInfoLength = 0;
            int vatPositionOffset = 0;
            int vatPositionLength = 0;
            int vatNormalOffset = 0;
            int vatNormalLength = 0;
            int particleOffset = 0;
            int particleLength = 0;
            int particleTextureOffset = 0;
            int particleTextureLength = 0;
            int interactionOffset = 0;
            int interactionLength = 0;
            int productOffset = 0;
            int productLength = 0;

            for (int section = 0; section < sectionCount; section++)
            {
                int toc = HeaderBytes + section * TocEntryBytes;
                uint type = ReadU32At(toc);
                int rank = SectionRank(type);
                uint offsetRaw = ReadU32At(toc + 4);
                uint lengthRaw = ReadU32At(toc + 8);
                if (rank < 0 || rank <= previousRank || ReadU32At(toc + 12) != 0u ||
                    offsetRaw != (uint)expectedOffset || lengthRaw == 0u || lengthRaw > int.MaxValue)
                {
                    return Fail("RAC2 section table is non-canonical.");
                }

                if (section == 0 && type != MetaType || section == 1 && type != MeshType)
                {
                    return Fail("RAC2 must begin with META and MESH.");
                }

                long next = (long)expectedOffset + lengthRaw;
                if (next > bytes.Length) return Fail("RAC2 section extends past end-of-file.");

                if (type == MetaType) { metaOffset = expectedOffset; metaLength = (int)lengthRaw; }
                else if (type == MeshType) { meshOffset = expectedOffset; meshLength = (int)lengthRaw; }
                else if (type == MatlType) { materialOffset = expectedOffset; materialLength = (int)lengthRaw; }
                else if (type == Tex0Type) { textureOffset = expectedOffset; textureLength = (int)lengthRaw; }
                else if (type == TexnType) { normalOffset = expectedOffset; normalLength = (int)lengthRaw; }
                else if (type == VatiType) { vatInfoOffset = expectedOffset; vatInfoLength = (int)lengthRaw; }
                else if (type == VatpType) { vatPositionOffset = expectedOffset; vatPositionLength = (int)lengthRaw; }
                else if (type == VatnType) { vatNormalOffset = expectedOffset; vatNormalLength = (int)lengthRaw; }
                else if (type == PartType) { particleOffset = expectedOffset; particleLength = (int)lengthRaw; }
                else if (type == PtexType) { particleTextureOffset = expectedOffset; particleTextureLength = (int)lengthRaw; }
                else if (type == IntrType) { interactionOffset = expectedOffset; interactionLength = (int)lengthRaw; }
                else { productOffset = expectedOffset; productLength = (int)lengthRaw; }

                previousRank = rank;
                expectedOffset = (int)next;
            }

            if (expectedOffset != bytes.Length || metaLength != MetaBytes)
            {
                return Fail("RAC2 contains a gap, trailing bytes, or invalid META size.");
            }

            Vector3 declaredCenter = new Vector3(ReadF32At(metaOffset), ReadF32At(metaOffset + 4), ReadF32At(metaOffset + 8));
            Vector3 declaredSize = new Vector3(ReadF32At(metaOffset + 12), ReadF32At(metaOffset + 16), ReadF32At(metaOffset + 20));
            Color baseColor = new Color(ReadF32At(metaOffset + 24), ReadF32At(metaOffset + 28), ReadF32At(metaOffset + 32), ReadF32At(metaOffset + 36));
            if (!FiniteVector(declaredCenter, 1000000f) || !FiniteVector(declaredSize, 2000000f) ||
                declaredSize.x < 0f || declaredSize.y < 0f || declaredSize.z < 0f || !FiniteColor(baseColor))
            {
                return Fail("RAC2 META values are invalid.");
            }

            bool hasVat = vatInfoOffset != 0;
            int vatFrameCount = 0;
            float vatFps = 0f;
            int vatWidth = 0;
            int vatRowsPerFrame = 0;
            int vatHeight = 0;
            bool vatLoop = false;
            bool vatHasNormals = false;
            Bounds vatBounds = new Bounds();
            if (hasVat)
            {
                if (vatPositionOffset == 0 || vatInfoLength < VatInfoBaseBytes || ReadU32At(vatInfoOffset) != 1u)
                    return Fail("RAC2 VAT metadata is incomplete.");
                uint vatFlags = ReadU32At(vatInfoOffset + 4);
                uint frameRaw = ReadU32At(vatInfoOffset + 8);
                vatFps = ReadF32At(vatInfoOffset + 12);
                uint widthRaw = ReadU32At(vatInfoOffset + 16);
                uint rowsRaw = ReadU32At(vatInfoOffset + 20);
                uint heightRaw = ReadU32At(vatInfoOffset + 24);
                uint positionFormat = ReadU32At(vatInfoOffset + 28);
                uint normalFormat = ReadU32At(vatInfoOffset + 32);
                uint nameLength = ReadU32At(vatInfoOffset + 36);
                Vector3 vatCenter = new Vector3(ReadF32At(vatInfoOffset + 40), ReadF32At(vatInfoOffset + 44), ReadF32At(vatInfoOffset + 48));
                Vector3 vatSize = new Vector3(ReadF32At(vatInfoOffset + 52), ReadF32At(vatInfoOffset + 56), ReadF32At(vatInfoOffset + 60));
                vatLoop = (vatFlags & 1u) != 0u;
                vatHasNormals = (vatFlags & 2u) != 0u;
                if ((vatFlags & ~3u) != 0u || frameRaw < 2u || frameRaw > 240u || !Range(vatFps, 1f, 60f) ||
                    widthRaw == 0u || widthRaw > 2048u || rowsRaw == 0u || heightRaw == 0u || heightRaw > 4096u ||
                    heightRaw != rowsRaw * frameRaw || positionFormat != 1u ||
                    normalFormat != (vatHasNormals ? 2u : 0u) || nameLength > 64u ||
                    vatInfoLength != VatInfoBaseBytes + (int)nameLength ||
                    !FiniteVector(vatCenter, 1000000f) || !FiniteVector(vatSize, 2000000f) ||
                    vatSize.x < 0f || vatSize.y < 0f || vatSize.z < 0f ||
                    vatHasNormals != (vatNormalOffset != 0))
                {
                    return Fail("RAC2 VAT metadata is invalid.");
                }
                vatFrameCount = (int)frameRaw;
                vatWidth = (int)widthRaw;
                vatRowsPerFrame = (int)rowsRaw;
                vatHeight = (int)heightRaw;
                vatBounds = new Bounds(vatCenter, vatSize);
            }
            else if (vatPositionOffset != 0 || vatNormalOffset != 0)
            {
                return Fail("RAC2 VAT texture is missing VATI metadata.");
            }

            bool hasParticles = particleOffset != 0;
            bool particleLoop = false;
            bool particleBillboard = false;
            bool particleHideBase = false;
            int particleShaderProfile = 0;
            int particleMaximum = 0;
            float particleDuration = 0f;
            float particleLifetime = 0f;
            float particleEmissionRate = 0f;
            float particleSpeedMinimum = 0f;
            float particleSpeedMaximum = 0f;
            float particleSizeMinimum = 0f;
            float particleSizeMaximum = 0f;
            float particleGravity = 0f;
            float particleAngularSpeed = 0f;
            Vector3 particleOrigin = Vector3.zero;
            Vector3 particleDirection = Vector3.up;
            int particleShape = 0;
            float particleShapeRadius = 0f;
            float particleShapeAngle = 0f;
            Vector3 particleShapeScale = Vector3.one;
            Color particleStartColor = Color.white;
            Color particleEndColor = Color.white;
            int particleColumns = 1;
            int particleRows = 1;
            if (hasParticles)
            {
                if (particleLength != ParticleBytes || ReadU32At(particleOffset) != 1u)
                    return Fail("RAC2 PART format is unsupported.");
                uint flags = ReadU32At(particleOffset + 4);
                uint shaderRaw = ReadU32At(particleOffset + 8);
                uint maximumRaw = ReadU32At(particleOffset + 12);
                particleDuration = ReadF32At(particleOffset + 16);
                particleLifetime = ReadF32At(particleOffset + 20);
                particleEmissionRate = ReadF32At(particleOffset + 24);
                particleSpeedMinimum = ReadF32At(particleOffset + 28);
                particleSpeedMaximum = ReadF32At(particleOffset + 32);
                particleSizeMinimum = ReadF32At(particleOffset + 36);
                particleSizeMaximum = ReadF32At(particleOffset + 40);
                particleGravity = ReadF32At(particleOffset + 44);
                particleAngularSpeed = ReadF32At(particleOffset + 48);
                particleOrigin = new Vector3(ReadF32At(particleOffset + 52), ReadF32At(particleOffset + 56), ReadF32At(particleOffset + 60));
                particleDirection = new Vector3(ReadF32At(particleOffset + 64), ReadF32At(particleOffset + 68), ReadF32At(particleOffset + 72));
                uint shapeRaw = ReadU32At(particleOffset + 76);
                particleShapeRadius = ReadF32At(particleOffset + 80);
                particleShapeAngle = ReadF32At(particleOffset + 84);
                particleShapeScale = new Vector3(ReadF32At(particleOffset + 88), ReadF32At(particleOffset + 92), ReadF32At(particleOffset + 96));
                particleStartColor = new Color(ReadF32At(particleOffset + 100), ReadF32At(particleOffset + 104), ReadF32At(particleOffset + 108), ReadF32At(particleOffset + 112));
                particleEndColor = new Color(ReadF32At(particleOffset + 116), ReadF32At(particleOffset + 120), ReadF32At(particleOffset + 124), ReadF32At(particleOffset + 128));
                uint columnsRaw = ReadU32At(particleOffset + 132);
                uint rowsRaw = ReadU32At(particleOffset + 136);
                if ((flags & ~7u) != 0u || shaderRaw > 1u || maximumRaw < 1u || maximumRaw > 32u ||
                    !Range(particleDuration, 0.001f, 120f) || !Range(particleLifetime, 0.02f, 30f) ||
                    !Range(particleEmissionRate, 0f, 60f) || !Range(particleSpeedMinimum, 0f, 20f) ||
                    !Range(particleSpeedMaximum, particleSpeedMinimum, 20f) ||
                    !Range(particleSizeMinimum, 0.0001f, 10f) || !Range(particleSizeMaximum, particleSizeMinimum, 10f) ||
                    !Range(particleGravity, -20f, 20f) || !Range(particleAngularSpeed, -1440f, 1440f) ||
                    !FiniteVector(particleOrigin, 1000000f) || !FiniteVector(particleDirection, 16f) ||
                    particleDirection.sqrMagnitude < 0.000001f || shapeRaw > 3u ||
                    !Range(particleShapeRadius, 0f, 10f) || !Range(particleShapeAngle, 0f, 89f) ||
                    !FiniteVector(particleShapeScale, 10f) || particleShapeScale.x < 0f ||
                    particleShapeScale.y < 0f || particleShapeScale.z < 0f ||
                    !FiniteColor(particleStartColor) || !FiniteColor(particleEndColor) ||
                    columnsRaw < 1u || columnsRaw > 16u || rowsRaw < 1u || rowsRaw > 16u ||
                    columnsRaw * rowsRaw > 256u || ParticlePlayer == null ||
                    ParticlePlayer.PoolObjects == null || ParticlePlayer.PoolObjects.Length < (int)maximumRaw ||
                    (shaderRaw == 0u ? ParticlePlayer.AlphaTemplate : ParticlePlayer.AdditiveTemplate) == null)
                {
                    return Fail("RAC2 PART metadata or Particle pool is invalid.");
                }

                particleLoop = (flags & 1u) != 0u;
                particleBillboard = (flags & 2u) != 0u;
                particleHideBase = (flags & 4u) != 0u;
                particleShaderProfile = (int)shaderRaw;
                particleMaximum = (int)maximumRaw;
                particleShape = (int)shapeRaw;
                particleColumns = (int)columnsRaw;
                particleRows = (int)rowsRaw;
            }
            else if (particleTextureOffset != 0)
            {
                return Fail("RAC2 PTEX is missing PART metadata.");
            }
            bool hasCollider = false;
            bool isPortable = false;
            if (interactionOffset != 0)
            {
                if (interactionLength != InteractionBytes || ReadU32At(interactionOffset) != 1u)
                    return Fail("RAC2 INTR format is unsupported.");
                uint interactionFlags = ReadU32At(interactionOffset + 4);
                hasCollider = (interactionFlags & 1u) != 0u;
                isPortable = (interactionFlags & 2u) != 0u;
                if ((interactionFlags & ~3u) != 0u || isPortable && !hasCollider ||
                    hasCollider && TargetCollider == null ||
                    isPortable && (TargetRigidbody == null || TargetPickup == null))
                {
                    return Fail("RAC2 INTR metadata or interaction components are invalid.");
                }
            }

            bool hasProduct = productOffset != 0;
            string productName = "";
            string creatorName = "";
            string productUrl = "";
            string avatarBlueprintId = "";
            bool trialEnabled = false;
            if (hasProduct)
            {
                if (productLength < ProductHeaderBytes || ReadU32At(productOffset) != 1u)
                    return Fail("RAC2 PROD format is unsupported.");
                uint productFlags = ReadU32At(productOffset + 4);
                uint nameLengthRaw = ReadU32At(productOffset + 8);
                uint creatorLengthRaw = ReadU32At(productOffset + 12);
                uint urlLengthRaw = ReadU32At(productOffset + 16);
                uint avatarLengthRaw = ReadU32At(productOffset + 20);
                long payloadLength = (long)nameLengthRaw + creatorLengthRaw + urlLengthRaw + avatarLengthRaw;
                if ((productFlags & ~1u) != 0u || nameLengthRaw > 128u || creatorLengthRaw > 128u ||
                    urlLengthRaw > 1024u || avatarLengthRaw > 41u || payloadLength == 0L ||
                    payloadLength != productLength - ProductHeaderBytes)
                {
                    return Fail("RAC2 PROD metadata is invalid.");
                }
                int productCursor = productOffset + ProductHeaderBytes;
                productName = Encoding.UTF8.GetString(_data, productCursor, (int)nameLengthRaw);
                productCursor += (int)nameLengthRaw;
                creatorName = Encoding.UTF8.GetString(_data, productCursor, (int)creatorLengthRaw);
                productCursor += (int)creatorLengthRaw;
                productUrl = Encoding.UTF8.GetString(_data, productCursor, (int)urlLengthRaw);
                productCursor += (int)urlLengthRaw;
                avatarBlueprintId = Encoding.UTF8.GetString(_data, productCursor, (int)avatarLengthRaw);
                trialEnabled = (productFlags & 1u) != 0u;
                bool validUrl = string.IsNullOrEmpty(productUrl) || productUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                bool validAvatar = string.IsNullOrEmpty(avatarBlueprintId) ||
                    avatarBlueprintId.Length == 41 && avatarBlueprintId.StartsWith("avtr_");
                if (!validUrl || !validAvatar || trialEnabled && avatarBlueprintId.Length != 41)
                    return Fail("RAC2 PROD URL or Avatar Blueprint ID is invalid.");
            }
            if (meshLength < MeshHeaderBytes) return Fail("RAC2 MESH header is truncated.");
            uint vertexRaw = ReadU32At(meshOffset);
            uint indexRaw = ReadU32At(meshOffset + 4);
            uint attributes = ReadU32At(meshOffset + 8);
            if (vertexRaw == 0u || vertexRaw > StandardMaxVertices || indexRaw == 0u ||
                indexRaw > StandardMaxIndices || ((int)indexRaw % 3) != 0 ||
                (attributes & ~KnownAttributes) != 0u || ReadU32At(meshOffset + 12) != 32u)
            {
                return Fail("RAC2 MESH counts or attributes exceed the Pad profile.");
            }

            int vertexCount = (int)vertexRaw;
            int indexCount = (int)indexRaw;
            bool hasNormals = (attributes & AttrNormals) != 0u;
            bool hasUv0 = (attributes & AttrUv0) != 0u;
            bool hasColors = (attributes & AttrColors) != 0u;
            bool hasTangents = (attributes & AttrTangents) != 0u;
            bool hasUv1 = (attributes & AttrUv1) != 0u;
            if (hasVat && (!hasUv1 || materialOffset == 0))
                return Fail("RAC2 VAT requires UV1 vertex lookup data and a lilToon material profile.");
            long expectedMesh = MeshHeaderBytes + (long)vertexCount * 12L + (long)indexCount * 4L;
            if (hasNormals) expectedMesh += (long)vertexCount * 12L;
            if (hasUv0) expectedMesh += (long)vertexCount * 8L;
            if (hasUv1) expectedMesh += (long)vertexCount * 8L;
            if (hasColors) expectedMesh += (long)vertexCount * 4L;
            if (hasTangents) expectedMesh += (long)vertexCount * 16L;
            if (expectedMesh != meshLength) return Fail("RAC2 MESH array sizes are inconsistent.");

            _cursor = meshOffset + MeshHeaderBytes;
            Vector3[] vertices = new Vector3[vertexCount];
            Vector3 actualMin = Vector3.zero;
            Vector3 actualMax = Vector3.zero;
            for (int index = 0; index < vertexCount; index++)
            {
                Vector3 value = new Vector3(ReadF32(), ReadF32(), ReadF32());
                if (!FiniteVector(value, 1000000f)) return Fail("RAC2 position is invalid.");
                vertices[index] = value;
                if (index == 0) { actualMin = value; actualMax = value; }
                else { actualMin = Vector3.Min(actualMin, value); actualMax = Vector3.Max(actualMax, value); }
            }

            Vector3 declaredMin = declaredCenter - declaredSize * 0.5f;
            Vector3 declaredMax = declaredCenter + declaredSize * 0.5f;
            if (!BoundsConsistent(actualMin, actualMax, declaredMin, declaredMax))
            {
                return Fail("RAC2 META bounds do not consistently contain positions.");
            }
            Vector3 boothMin = hasVat ? vatBounds.min : actualMin;
            Vector3 boothMax = hasVat ? vatBounds.max : actualMax;
            if (hasParticles && particleHideBase && !hasVat)
            {
                // Particle-only v2 uses a zero-size emitter anchor.
                // ParticleSystem never expands booth placement or dimensions.
                boothMin = particleOrigin;
                boothMax = particleOrigin;
            }
            Vector3 boothSize = boothMax - boothMin;
            Vector3 normalizedBoothMin = new Vector3(
                -boothSize.x * 0.5f, 0f, -boothSize.z * 0.5f);
            Vector3 normalizedBoothMax = normalizedBoothMin + boothSize;
            if (EnforceStandardBoothProfile &&
                !BoundsFitBooth(normalizedBoothMin, normalizedBoothMax))
            {
                return Fail("RAC2 model geometry or VAT animation is too large for the configured booth.");
            }

            Vector3[] normals = new Vector3[0];
            if (hasNormals)
            {
                normals = new Vector3[vertexCount];
                for (int index = 0; index < vertexCount; index++)
                {
                    Vector3 value = new Vector3(ReadF32(), ReadF32(), ReadF32());
                    if (!FiniteVector(value, 16f)) return Fail("RAC2 normal is invalid.");
                    normals[index] = value;
                }
            }

            Vector2[] uv = new Vector2[0];
            if (hasUv0)
            {
                uv = new Vector2[vertexCount];
                for (int index = 0; index < vertexCount; index++)
                {
                    Vector2 value = new Vector2(ReadF32(), ReadF32());
                    if (!Finite(value.x, 1000000f) || !Finite(value.y, 1000000f)) return Fail("RAC2 UV0 is invalid.");
                    uv[index] = value;
                }
            }

            Vector2[] uv1 = new Vector2[0];
            if (hasUv1)
            {
                uv1 = new Vector2[vertexCount];
                for (int index = 0; index < vertexCount; index++)
                {
                    Vector2 value = new Vector2(ReadF32(), ReadF32());
                    if (!Finite(value.x, 1000000f) || !Finite(value.y, 1000000f)) return Fail("RAC2 UV1 is invalid.");
                    uv1[index] = value;
                }
            }

            Color32[] colors = new Color32[0];            if (hasColors)
            {
                colors = new Color32[vertexCount];
                for (int index = 0; index < vertexCount; index++)
                {
                    colors[index] = new Color32(_data[_cursor], _data[_cursor + 1], _data[_cursor + 2], _data[_cursor + 3]);
                    _cursor += 4;
                }
            }

            Vector4[] tangents = new Vector4[0];
            if (hasTangents)
            {
                tangents = new Vector4[vertexCount];
                for (int index = 0; index < vertexCount; index++)
                {
                    Vector4 value = new Vector4(ReadF32(), ReadF32(), ReadF32(), ReadF32());
                    if (!Finite(value.x, 16f) || !Finite(value.y, 16f) || !Finite(value.z, 16f) || !Finite(value.w, 1.001f))
                    {
                        return Fail("RAC2 tangent is invalid.");
                    }
                    tangents[index] = value;
                }
            }

            int[] triangles = new int[indexCount];
            for (int index = 0; index < indexCount; index++)
            {
                uint value = ReadU32();
                if (value >= vertexRaw) return Fail("RAC2 index references a missing vertex.");
                triangles[index] = (int)value;
            }
            if (_cursor != meshOffset + meshLength) return Fail("RAC2 MESH cursor is inconsistent.");

            int materialProfile = 0;
            int renderMode = 0;
            int cullMode = 2;
            float cutoff = 0.5f;
            float bumpScale = 1f;
            float shadowStrength = 1f;
            float asUnlit = 0f;
            float lightMin = 0.05f;
            float lightMax = 1f;
            float monochrome = 0f;
            bool materialHasNormal = false;
            if (materialOffset != 0)
            {
                if (materialLength != MaterialBytes ||
                    ReadU32At(materialOffset) != 1u ||
                    ReadU32At(materialOffset + 4) != 1u)
                {
                    return Fail("RAC2 MATL profile is unsupported.");
                }

                materialProfile = 1;
                uint renderRaw = ReadU32At(materialOffset + 8);
                uint cullRaw = ReadU32At(materialOffset + 12);
                uint flags = ReadU32At(materialOffset + 44);
                if (renderRaw > 1u || cullRaw > 2u || (flags & ~1u) != 0u)
                {
                    return Fail("RAC2 lilToon mode, culling, or flags are unsupported.");
                }

                renderMode = (int)renderRaw;
                cullMode = (int)cullRaw;
                cutoff = ReadF32At(materialOffset + 16);
                bumpScale = ReadF32At(materialOffset + 20);
                shadowStrength = ReadF32At(materialOffset + 24);
                asUnlit = ReadF32At(materialOffset + 28);
                lightMin = ReadF32At(materialOffset + 32);
                lightMax = ReadF32At(materialOffset + 36);
                monochrome = ReadF32At(materialOffset + 40);
                if (!Range(cutoff, -0.001f, 1.001f) || !Range(bumpScale, -10f, 10f) ||
                    !Range(shadowStrength, 0f, 1f) || !Range(asUnlit, 0f, 1f) ||
                    !Range(lightMin, 0f, 1f) || !Range(lightMax, 0f, 10f) ||
                    !Range(monochrome, 0f, 1f))
                {
                    return Fail("RAC2 lilToon parameters are invalid.");
                }

                materialHasNormal = (flags & 1u) != 0u;
            }

            bool hasNormalSection = normalOffset != 0;
            if (hasNormalSection != materialHasNormal ||
                hasNormalSection && (materialProfile != 1 || !hasUv0 || !hasNormals || !hasTangents))
            {
                return Fail("RAC2 normal map requires matching lilToon metadata, UV0, normals, and tangents.");
            }

            Texture2D nextTexture = ParseTexture(textureOffset, textureLength, false, "TEX0");
            if (textureOffset != 0 && nextTexture == null) return false;
            Texture2D nextNormal = ParseTexture(normalOffset, normalLength, true, "TEXN");
            if (normalOffset != 0 && nextNormal == null)
            {
                if (nextTexture != null) Destroy(nextTexture);
                return false;
            }

            Texture2D nextVatPosition = ParseVatTexture(vatPositionOffset, vatPositionLength, 1, vatWidth, vatHeight, "VATP");
            if (vatPositionOffset != 0 && nextVatPosition == null)
            {
                if (nextTexture != null) Destroy(nextTexture);
                if (nextNormal != null) Destroy(nextNormal);
                return false;
            }
            Texture2D nextVatNormal = ParseVatTexture(vatNormalOffset, vatNormalLength, 2, vatWidth, vatHeight, "VATN");
            if (vatNormalOffset != 0 && nextVatNormal == null)
            {
                if (nextTexture != null) Destroy(nextTexture);
                if (nextNormal != null) Destroy(nextNormal);
                if (nextVatPosition != null) Destroy(nextVatPosition);
                return false;
            }
            Texture2D nextParticleTexture = ParseTexture(particleTextureOffset, particleTextureLength, false, "PTEX");
            if (particleTextureOffset != 0 && nextParticleTexture == null)
            {
                if (nextTexture != null) Destroy(nextTexture);
                if (nextNormal != null) Destroy(nextNormal);
                if (nextVatPosition != null) Destroy(nextVatPosition);
                if (nextVatNormal != null) Destroy(nextVatNormal);
                if (nextParticleTexture != null) Destroy(nextParticleTexture);
                return false;
            }

            Material selectedTemplate = hasVat
                ? renderMode == 0 ? VatOpaqueTemplate : VatCutoutTemplate
                : materialProfile == 1
                    ? renderMode == 0 ? LilToonOpaqueTemplate : LilToonCutoutTemplate
                    : MaterialTemplate;
            EnsureMaterial(selectedTemplate);
            if (_material == null)
            {
                if (nextTexture != null) Destroy(nextTexture);
                if (nextNormal != null) Destroy(nextNormal);
                if (nextVatPosition != null) Destroy(nextVatPosition);
                if (nextVatNormal != null) Destroy(nextVatNormal);
                if (nextParticleTexture != null) Destroy(nextParticleTexture);
                return Fail(hasVat
                    ? "RAC2 VAT material templates are unavailable."
                    : materialProfile == 1
                        ? "RAC2 lilToon material templates are unavailable."
                        : "RAC2 runtime material is unavailable.");
            }

            if (_mesh == null) { _mesh = new Mesh(); _mesh.name = "RAC2 Mesh"; }
            else _mesh.Clear();
            _mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _mesh.vertices = vertices;
            if (hasNormals) _mesh.normals = normals;
            if (hasUv0) _mesh.uv = uv;
            if (hasUv1) _mesh.uv2 = uv1;
            if (hasColors) _mesh.colors32 = colors;
            if (hasTangents) _mesh.tangents = tangents;
            _mesh.triangles = triangles;
            if (!hasNormals) _mesh.RecalculateNormals();
            _mesh.bounds = hasVat ? vatBounds : new Bounds((actualMin + actualMax) * 0.5f, actualMax - actualMin);

            if (_material.HasProperty(BaseColorProperty)) _material.SetColor(BaseColorProperty, baseColor);
            if (_material.HasProperty(MainTextureProperty)) _material.SetTexture(MainTextureProperty, nextTexture);
            if (materialProfile == 1)
            {
                SetFloat("_TransparentMode", renderMode);
                SetFloat("_Cull", cullMode);
                SetFloat("_Cutoff", cutoff);
                SetFloat("_BumpScale", bumpScale);
                SetFloat("_ShadowStrength", shadowStrength);
                SetFloat("_AsUnlit", asUnlit);
                SetFloat("_LightMinLimit", lightMin);
                SetFloat("_LightMaxLimit", lightMax);
                SetFloat("_MonochromeLighting", monochrome);
                SetFloat("_UseBumpMap", nextNormal != null ? 1f : 0f);
                if (_material.HasProperty("_BumpMap")) _material.SetTexture("_BumpMap", nextNormal);
            }
            if (hasVat)
            {
                if (_material.HasProperty("_VatPositionTex")) _material.SetTexture("_VatPositionTex", nextVatPosition);
                if (_material.HasProperty("_VatNormalTex")) _material.SetTexture("_VatNormalTex", nextVatNormal);
                SetFloat("_VatFrameCount", vatFrameCount);
                SetFloat("_VatRowsPerFrame", vatRowsPerFrame);
                SetFloat("_VatTextureHeight", vatHeight);
                SetFloat("_VatFps", vatFps);
                SetFloat("_VatSpeed", VatPlaybackSpeed);
                SetFloat("_VatLoop", vatLoop ? 1f : 0f);
                SetFloat("_VatHasNormal", vatHasNormals ? 1f : 0f);
                SetFloat("_VatStartTime", Time.timeSinceLevelLoad);
                if (_material.HasProperty("_VatBoundsMin")) _material.SetVector("_VatBoundsMin", vatBounds.min);
                if (_material.HasProperty("_VatBoundsSize")) _material.SetVector("_VatBoundsSize", vatBounds.size);
            }

            if (ParticlePlayer != null) ParticlePlayer.ClearParticles();
            if (hasParticles)
            {
                ParticlePlayer.ParticleMesh = _mesh;
                ParticlePlayer.ParticleTexture = nextParticleTexture;
                ParticlePlayer.Loop = particleLoop;
                ParticlePlayer.Billboard = particleBillboard;
                ParticlePlayer.ShaderProfile = particleShaderProfile;
                ParticlePlayer.MaximumParticles = particleMaximum;
                ParticlePlayer.Duration = particleDuration;
                ParticlePlayer.Lifetime = particleLifetime;
                ParticlePlayer.EmissionRate = particleEmissionRate;
                ParticlePlayer.SpeedMinimum = particleSpeedMinimum;
                ParticlePlayer.SpeedMaximum = particleSpeedMaximum;
                ParticlePlayer.SizeMinimum = particleSizeMinimum;
                ParticlePlayer.SizeMaximum = particleSizeMaximum;
                ParticlePlayer.Gravity = particleGravity;
                ParticlePlayer.AngularSpeed = particleAngularSpeed;
                ParticlePlayer.Origin = particleOrigin;
                ParticlePlayer.Direction = particleDirection;
                ParticlePlayer.Shape = particleShape;
                ParticlePlayer.ShapeRadius = particleShapeRadius;
                ParticlePlayer.ShapeAngle = particleShapeAngle;
                ParticlePlayer.ShapeScale = particleShapeScale;
                ParticlePlayer.StartColor = particleStartColor;
                ParticlePlayer.EndColor = particleEndColor;
                ParticlePlayer.FlipbookColumns = particleColumns;
                ParticlePlayer.FlipbookRows = particleRows;
                ConfigureParticleBooth(ParticlePlayer);
                ParticlePlayer.ApplyConfiguration();
                if (!ParticlePlayer.IsConfigured)
                {
                    if (nextParticleTexture != null) Destroy(nextParticleTexture);
                    return Fail("RAC2 Particle pool could not be configured.");
                }
            }
            if (_texture != null) Destroy(_texture);
            if (_normalTexture != null) Destroy(_normalTexture);
            if (_vatPositionTexture != null) Destroy(_vatPositionTexture);
            if (_vatNormalTexture != null) Destroy(_vatNormalTexture);
            if (_particleTexture != null) Destroy(_particleTexture);
            _texture = nextTexture;
            _normalTexture = nextNormal;
            _vatPositionTexture = nextVatPosition;
            _vatNormalTexture = nextVatNormal;
            _particleTexture = nextParticleTexture;
            TargetMeshFilter.sharedMesh = _mesh;
            TargetRenderer.sharedMaterial = _material;
            TargetRenderer.enabled = !particleHideBase;
            ConfigureInteraction(hasCollider, isPortable, _mesh.bounds);
            LoadedVertexCount = vertexCount;
            LoadedIndexCount = indexCount;
            LoadedHasTexture = nextTexture != null;
            LoadedHasNormalMap = nextNormal != null;
            LoadedMaterialProfile = materialProfile;
            LoadedHasVat = hasVat;
            LoadedVatFrameCount = vatFrameCount;
            LoadedVatFramesPerSecond = vatFps;
            LoadedHasParticles = hasParticles;
            LoadedParticleMaximum = particleMaximum;
            LoadedHasCollider = hasCollider;
            LoadedIsPortable = isPortable;
            LoadedHasProduct = hasProduct;
            LoadedProductName = productName;
            LoadedCreatorName = creatorName;
            LoadedProductUrl = productUrl;
            LoadedAvatarBlueprintId = avatarBlueprintId;
            LoadedTrialEnabled = trialEnabled;
            if (ProductController != null)
            {
                if (hasProduct) ProductController.ApplyProduct(productName, creatorName, productUrl, avatarBlueprintId, trialEnabled);
                else ProductController.ClearProduct();
            }
            _data = null;
            return true;
        }

        private Texture2D ParseTexture(int offset, int sectionLength, bool linear, string label)
        {
            if (offset == 0) return null;
            if (sectionLength == 8 && ReadU32At(offset) == 2u)
            {
                uint reference = ReadU32At(offset + 4);
                if (_sharedTextureCache == null || reference >= (uint)_sharedTextureCount ||
                    _sharedTextureLinear[(int)reference] != linear)
                {
                    Fail("RAC2 shared texture reference is invalid.");
                    return null;
                }
                return _sharedTextureCache[(int)reference];
            }
            if (sectionLength < TextureHeaderBytes || ReadU32At(offset) != 1u)
            {
                Fail("RAC2 " + label + " format is unsupported.");
                return null;
            }

            uint width = ReadU32At(offset + 4);
            uint height = ReadU32At(offset + 8);
            uint length = ReadU32At(offset + 12);
            long required = (long)width * height * 4L;
            if (width == 0u || height == 0u ||
                width > StandardMaxTextureDimension || height > StandardMaxTextureDimension ||
                required > StandardMaxTextureBytes || length != required ||
                sectionLength != TextureHeaderBytes + (int)length)
            {
                Fail("RAC2 " + label + " metadata is invalid.");
                return null;
            }

            byte[] raw = new byte[(int)length];
            Buffer.BlockCopy(_data, offset + TextureHeaderBytes, raw, 0, raw.Length);
            Texture2D texture = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false, linear);
            texture.name = "RAC2 " + label;
            texture.LoadRawTextureData(raw);
            texture.Apply(false, true);
            if (_sharedTextureCache != null && label.StartsWith("NODE "))
            {
                if (_sharedTextureCount >= _sharedTextureCache.Length)
                {
                    Destroy(texture);
                    Fail("RAC2 shared texture count exceeds the material profile.");
                    return null;
                }
                _sharedTextureCache[_sharedTextureCount] = texture;
                _sharedTextureLinear[_sharedTextureCount] = linear;
                _sharedTextureCount++;
            }
            return texture;
        }

        private Texture2D ParseVatTexture(int offset, int sectionLength, int expectedFormat, int expectedWidth, int expectedHeight, string label)
        {
            if (offset == 0) return null;
            if (sectionLength < VatTextureHeaderBytes || ReadU32At(offset) != 1u ||
                ReadU32At(offset + 4) != (uint)expectedFormat)
            {
                Fail("RAC2 " + label + " format is unsupported.");
                return null;
            }

            uint width = ReadU32At(offset + 8);
            uint height = ReadU32At(offset + 12);
            uint length = ReadU32At(offset + 16);
            int bytesPerPixel = expectedFormat == 1 ? 8 : 4;
            long required = (long)width * height * bytesPerPixel;
            if (width != (uint)expectedWidth || height != (uint)expectedHeight ||
                required <= 0L || required > 33554432L || length != required ||
                sectionLength != VatTextureHeaderBytes + (int)length)
            {
                Fail("RAC2 " + label + " metadata is invalid.");
                return null;
            }

            byte[] raw = new byte[(int)length];
            Buffer.BlockCopy(_data, offset + VatTextureHeaderBytes, raw, 0, raw.Length);
            TextureFormat textureFormat = expectedFormat == 1 ? TextureFormat.RGBAHalf : TextureFormat.RGBA32;
            Texture2D texture = new Texture2D((int)width, (int)height, textureFormat, false, true);
            texture.name = "RAC2 " + label;
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.LoadRawTextureData(raw);
            texture.Apply(false, true);
            return texture;
        }

        private bool BeginCompressedExpansion(byte[] source)
        {
            _standardChecksum = ReadU32At(16) == 3u;
            if (source.Length < HeaderBytes + CompressedTocEntryBytes * 2 ||
                ReadU32At(20) != (uint)CompressedTocEntryBytes)
                return Fail("RAC2 compressed directory is truncated or unsupported.");

            uint countRaw = ReadU32At(12);
            if (countRaw < 2u || countRaw > 12u)
                return Fail("RAC2 compressed section count is unsupported.");

            int count = (int)countRaw;
            int storedOffset = HeaderBytes + count * CompressedTocEntryBytes;
            long rawTotal = HeaderBytes + count * TocEntryBytes;
            for (int section = 0; section < count; section++)
            {
                int toc = HeaderBytes + section * CompressedTocEntryBytes;
                uint offsetRaw = ReadU32At(toc + 4);
                uint storedLengthRaw = ReadU32At(toc + 8);
                uint rawLengthRaw = ReadU32At(toc + 12);
                uint codec = ReadU32At(toc + 16);
                if (offsetRaw != (uint)storedOffset || storedLengthRaw == 0u || rawLengthRaw == 0u ||
                    storedLengthRaw > int.MaxValue || rawLengthRaw > StandardMaxBytes ||
                    codec > 1u || codec == 0u && storedLengthRaw != rawLengthRaw ||
                    codec == 1u && storedLengthRaw >= rawLengthRaw)
                    return Fail("RAC2 compressed section table is non-canonical.");

                long nextStored = (long)storedOffset + storedLengthRaw;
                rawTotal += rawLengthRaw;
                if (nextStored > source.Length || rawTotal > StandardMaxBytes)
                    return Fail("RAC2 compressed section exceeds the Pad memory limit.");
                storedOffset = (int)nextStored;
            }

            if (storedOffset != source.Length)
                return Fail("RAC2 compressed container has gaps or trailing bytes.");

            _expandSource = source;
            _expandTarget = new byte[(int)rawTotal];
            _expandSectionCount = count;
            _expandSectionIndex = 0;
            _expandRawOffset = HeaderBytes + count * TocEntryBytes;
            _expandStoredBytes = source.Length;
            _expandPhase = -1;

            WriteU32At(_expandTarget, 0, Magic);
            WriteU32At(_expandTarget, 4, ReadU32From(source, 4));
            WriteU32At(_expandTarget, 8, (uint)_expandTarget.Length);
            WriteU32At(_expandTarget, 12, countRaw);
            WriteU32At(_expandTarget, 16, 0u);
            WriteU32At(_expandTarget, 20, 0u);
            BeginAdaptiveLoadControl();
            StatusMessage = AdaptiveLoadingEnabled
                ? "Expanding RAC2 with adaptive frame budget..."
                : "Expanding RAC2...";
            SendCustomEventDelayedFrames("ContinueCompressedExpansion", 1);
            return true;
        }

        private int FixedExpansionTokenBudget()
        {
            if (FixedLoadingPerformanceMode <= LoadPerformanceSmooth)
                return SmoothExpansionTokenBudgetPerFrame;
            if (FixedLoadingPerformanceMode >= LoadPerformanceFast)
                return FastExpansionTokenBudgetPerFrame;
            return BalancedExpansionTokenBudgetPerFrame;
        }

        private int FixedChecksumByteBudget()
        {
            if (FixedLoadingPerformanceMode <= LoadPerformanceSmooth)
                return SmoothChecksumByteBudgetPerFrame;
            if (FixedLoadingPerformanceMode >= LoadPerformanceFast)
                return FastChecksumByteBudgetPerFrame;
            return BalancedChecksumByteBudgetPerFrame;
        }

        private void BeginAdaptiveLoadControl()
        {
            float baseline = _hasIdleFrameSample ? _idleFrameSeconds : 0.016666667f;
            if (baseline < 0.006944444f) baseline = 0.006944444f;
            if (baseline > 0.033333334f) baseline = 0.033333334f;
            _loadingFrameSeconds = baseline;
            AdaptiveBaselineFps = 1f / baseline;
            AdaptiveMeasuredFps = AdaptiveBaselineFps;
            AdaptiveLoadScale = 0.5f;
            RefreshAdaptiveLoadBudgets();
        }

        private void RefreshAdaptiveLoadBudgets()
        {
            if (!AdaptiveLoadingEnabled)
            {
                CurrentExpansionTokenBudget = FixedExpansionTokenBudget();
                CurrentChecksumByteBudget = FixedChecksumByteBudget();
                AdaptiveLoadScale = (float)CurrentExpansionTokenBudget /
                                    FastExpansionTokenBudgetPerFrame;
                return;
            }

            float frameSeconds = Time.deltaTime;
            if (frameSeconds < 0.001f) frameSeconds = _loadingFrameSeconds;
            _loadingFrameSeconds = _loadingFrameSeconds * 0.8f + frameSeconds * 0.2f;
            AdaptiveMeasuredFps = 1f / _loadingFrameSeconds;

            float allowed = AdaptiveAllowedFrameTimeIncrease;
            if (allowed < 0.05f) allowed = 0.05f;
            if (allowed > 0.5f) allowed = 0.5f;
            float baseline = AdaptiveBaselineFps > 0f
                ? 1f / AdaptiveBaselineFps : 0.016666667f;
            float target = baseline * (1f + allowed);

            if (frameSeconds > target * 1.04f)
            {
                float reduction = target / frameSeconds;
                if (reduction < 0.5f) reduction = 0.5f;
                if (reduction > 0.9f) reduction = 0.9f;
                AdaptiveLoadScale *= reduction;
            }
            else if (_loadingFrameSeconds < target * 0.92f)
            {
                AdaptiveLoadScale += (1f - AdaptiveLoadScale) * 0.025f + 0.0025f;
            }

            float minimumScale = (float)MinimumExpansionTokenBudgetPerFrame /
                                 FastExpansionTokenBudgetPerFrame;
            if (AdaptiveLoadScale < minimumScale) AdaptiveLoadScale = minimumScale;
            if (AdaptiveLoadScale > 1f) AdaptiveLoadScale = 1f;
            CurrentExpansionTokenBudget =
                (int)(FastExpansionTokenBudgetPerFrame * AdaptiveLoadScale);
            CurrentChecksumByteBudget =
                (int)(FastChecksumByteBudgetPerFrame * AdaptiveLoadScale);
            if (CurrentExpansionTokenBudget < MinimumExpansionTokenBudgetPerFrame)
                CurrentExpansionTokenBudget = MinimumExpansionTokenBudgetPerFrame;
            if (CurrentChecksumByteBudget < MinimumChecksumByteBudgetPerFrame)
                CurrentChecksumByteBudget = MinimumChecksumByteBudgetPerFrame;
        }

        public void ContinueCompressedExpansion()
        {
            if (Status != StatusLoading || _expandSource == null || _expandTarget == null) return;
            RefreshAdaptiveLoadBudgets();

            if (_expandSectionIndex >= _expandSectionCount)
            {
                if (_expandRawOffset != _expandTarget.Length)
                {
                    ReportError("RAC2 expanded section sizes are inconsistent.", 0);
                    return;
                }

                byte[] expanded = _expandTarget;
                int storedBytes = _expandStoredBytes;
                ClearExpansionState();
                FinishParseAndApply(expanded, true, storedBytes);
                return;
            }

            if (_expandPhase < 0)
            {
                int toc = HeaderBytes + _expandSectionIndex * CompressedTocEntryBytes;
                uint type = ReadU32From(_expandSource, toc);
                int sourceOffset = (int)ReadU32From(_expandSource, toc + 4);
                int storedLength = (int)ReadU32From(_expandSource, toc + 8);
                int rawLength = (int)ReadU32From(_expandSource, toc + 12);
                uint codec = ReadU32From(_expandSource, toc + 16);
                _expandExpectedChecksum = ReadU32From(_expandSource, toc + 20);

                int legacyToc = HeaderBytes + _expandSectionIndex * TocEntryBytes;
                WriteU32At(_expandTarget, legacyToc, type);
                WriteU32At(_expandTarget, legacyToc + 4, (uint)_expandRawOffset);
                WriteU32At(_expandTarget, legacyToc + 8, (uint)rawLength);
                WriteU32At(_expandTarget, legacyToc + 12, 0u);

                _expandTargetStart = _expandRawOffset;
                _expandOutput = _expandRawOffset;
                _expandOutputEnd = _expandRawOffset + rawLength;
                if (codec == 0u)
                {
                    _expandInput = sourceOffset;
                    _expandPhase = 2;
                }
                else
                {
                    _expandInput = sourceOffset;
                    _expandInputEnd = sourceOffset + storedLength;
                    _lzPhase = 0;
                    _lzRemaining = 0;
                    _expandPhase = 0;
                    StatusMessage = "Expanding RAC2 section " +
                                    (_expandSectionIndex + 1) + "/" +
                                    _expandSectionCount + "...";
                }
            }

            int workBudget = Mathf.Max(1024, Mathf.RoundToInt(DecompressionBytesPerFrame * AdaptiveLoadScale));
            if (_expandPhase == 2)
            {
                int copy = Mathf.Min(workBudget, _expandOutputEnd - _expandOutput);
                Buffer.BlockCopy(_expandSource, _expandInput, _expandTarget, _expandOutput, copy);
                _expandInput += copy;
                _expandOutput += copy;
                if (_expandOutput < _expandOutputEnd)
                {
                    SendCustomEventDelayedFrames("ContinueCompressedExpansion", 1);
                    return;
                }
                BeginIncrementalChecksum();
                SendCustomEventDelayedFrames("ContinueCompressedExpansion", 1);
                return;
            }
            if (_expandPhase == 0)
            {
                int result = DecompressLz4Slice(workBudget);
                if (result < 0)
                {
                    ReportError("RAC2 LZ4 section is malformed.", 0);
                    return;
                }
                if (result == 0)
                {
                    SendCustomEventDelayedFrames("ContinueCompressedExpansion", 1);
                    return;
                }
                BeginIncrementalChecksum();
                SendCustomEventDelayedFrames("ContinueCompressedExpansion", 1);
                return;
            }

            if (_expandPhase == 1)
            {
                ContinueIncrementalChecksum(CurrentChecksumByteBudget);
                if (_expandChecksumOffset < _expandChecksumEnd)
                {
                    SendCustomEventDelayedFrames("ContinueCompressedExpansion", 1);
                    return;
                }

                // Udon uses a checked int-to-uint conversion. Adler's signed
                // accumulators can be negative after unchecked overflow, so
                // isolate their low 16 bits before converting to uint.
                uint actual = ((uint)(_expandChecksumB & 0xFFFF) << 16) |
                              (uint)(_expandChecksumA & 0xFFFF);
                if (actual != _expandExpectedChecksum)
                {
                    ReportError("RAC2 section checksum mismatch.", 0);
                    return;
                }

                _expandRawOffset = _expandOutputEnd;
                _expandSectionIndex++;
                _expandPhase = -1;
                StatusMessage = "Expanding RAC2 " + _expandSectionIndex + "/" + _expandSectionCount + "...";
                SendCustomEventDelayedFrames("ContinueCompressedExpansion", 1);
            }
        }

        private int DecompressLz4Slice(int byteBudget)
        {
            // Every phase can yield, including length extensions and overlapping matches.
            int remainingBudget = byteBudget;
            while (remainingBudget > 0)
            {
                if (_lzPhase == 0)
                {
                    if (_expandInput == _expandInputEnd)
                        return _expandOutput == _expandOutputEnd ? 1 : -1;
                    _lzToken = _expandSource[_expandInput++];
                    _lzRemaining = _lzToken >> 4;
                    _lzPhase = _lzRemaining == 15 ? 1 : 2;
                    remainingBudget--;
                }
                else if (_lzPhase == 1 || _lzPhase == 4)
                {
                    if (_expandInput >= _expandInputEnd) return -1;
                    int extension = _expandSource[_expandInput++];
                    remainingBudget--;
                    int reserve = _lzPhase == 4 ? 4 : 0;
                    if (_lzRemaining > _expandOutputEnd - _expandOutput - reserve - extension) return -1;
                    _lzRemaining += extension;
                    if (extension != 255)
                    {
                        if (_lzPhase == 1) _lzPhase = 2;
                        else { _lzRemaining += 4; _lzPhase = 5; }
                    }
                }
                else if (_lzPhase == 2)
                {
                    if (_lzRemaining > _expandInputEnd - _expandInput ||
                        _lzRemaining > _expandOutputEnd - _expandOutput) return -1;
                    int copy = Mathf.Min(_lzRemaining, remainingBudget);
                    if (copy > 0)
                    {
                        Buffer.BlockCopy(_expandSource, _expandInput, _expandTarget, _expandOutput, copy);
                        _expandInput += copy;
                        _expandOutput += copy;
                        _lzRemaining -= copy;
                        remainingBudget -= copy;
                    }
                    if (_lzRemaining == 0)
                    {
                        if (_expandInput == _expandInputEnd)
                            return _expandOutput == _expandOutputEnd ? 1 : -1;
                        _lzPhase = 3;
                    }
                }
                else if (_lzPhase == 3)
                {
                    if (_expandInput + 2 > _expandInputEnd) return -1;
                    int distance = _expandSource[_expandInput] | _expandSource[_expandInput + 1] << 8;
                    _expandInput += 2;
                    remainingBudget -= 2;
                    if (distance == 0 || distance > _expandOutput - _expandTargetStart) return -1;
                    _lzReference = _expandOutput - distance;
                    _lzRemaining = _lzToken & 15;
                    if (_lzRemaining == 15) _lzPhase = 4;
                    else { _lzRemaining += 4; _lzPhase = 5; }
                }
                else
                {
                    if (_lzRemaining > _expandOutputEnd - _expandOutput) return -1;
                    int available = _expandOutput - _lzReference;
                    int copy = Mathf.Min(_lzRemaining, Mathf.Min(available, remainingBudget));
                    if (copy <= 0) return -1;
                    Buffer.BlockCopy(_expandTarget, _lzReference, _expandTarget, _expandOutput, copy);
                    // A partial period must resume at its next source byte.
                    if (copy < available) _lzReference += copy;
                    _expandOutput += copy;
                    _lzRemaining -= copy;
                    remainingBudget -= copy;
                    if (_lzRemaining == 0) _lzPhase = 0;
                }
            }
            return 0;
        }

        private void BeginIncrementalChecksum()
        {
            _expandChecksumOffset = _expandTargetStart;
            _expandChecksumEnd = _expandOutputEnd;
            _expandChecksumBlockEnd = _expandChecksumOffset + (_standardChecksum ? 2776 : 5552);
            if (_expandChecksumBlockEnd > _expandChecksumEnd) _expandChecksumBlockEnd = _expandChecksumEnd;
            _expandChecksumA = 1;
            _expandChecksumB = 0;
            _expandPhase = 1;
            StatusMessage = "Verifying RAC2 section " +
                            (_expandSectionIndex + 1) + "/" +
                            _expandSectionCount + "...";
        }

        private void ContinueIncrementalChecksum(int byteBudget)
        {
            const int modulus = 65521;
            int sliceEnd = _expandChecksumOffset + byteBudget;
            if (sliceEnd > _expandChecksumEnd) sliceEnd = _expandChecksumEnd;
            while (_expandChecksumOffset < sliceEnd)
            {
                int copyEnd = _expandChecksumBlockEnd;
                if (copyEnd > sliceEnd) copyEnd = sliceEnd;
                // BitConverter is a single Udon extern for four source bytes.
                // Updating Adler-32 four bytes at a time avoids millions of
                // interpreted array reads for VAT textures while producing the
                // exact same accumulator as the byte-at-a-time definition.
                while (_expandChecksumOffset + 4 <= copyEnd)
                {
                    int packed = BitConverter.ToInt32(
                        _expandTarget, _expandChecksumOffset);
                    int byte0 = packed & 255;
                    int byte1 = packed >> 8 & 255;
                    int byte2 = packed >> 16 & 255;
                    int byte3 = packed >> 24 & 255;
                    _expandChecksumB += (_expandChecksumA << 2) +
                                        (byte0 << 2) + byte1 * 3 +
                                        (byte2 << 1) + byte3;
                    _expandChecksumA += byte0 + byte1 + byte2 + byte3;
                    _expandChecksumOffset += 4;
                }
                while (_expandChecksumOffset < copyEnd)
                {
                    _expandChecksumA += _expandTarget[_expandChecksumOffset++];
                    _expandChecksumB += _expandChecksumA;
                }

                // Preserve the exporter's exact 5552-byte Adler blocks across
                // frame boundaries. Reducing at an arbitrary frame boundary
                // changes the checked 32-bit accumulator result used by RAC2.
                if (_expandChecksumOffset == _expandChecksumBlockEnd)
                {
                    _expandChecksumA %= modulus;
                    _expandChecksumB %= modulus;
                    _expandChecksumBlockEnd += _standardChecksum ? 2776 : 5552;
                    if (_expandChecksumBlockEnd > _expandChecksumEnd) _expandChecksumBlockEnd = _expandChecksumEnd;
                }
            }
        }

        private void FinishParseAndApply(byte[] bytes, bool wasCompressed, int storedBytes)
        {
            BeginAdaptiveLoadControl();
            _bundleRestoreWasCompressed = wasCompressed;
            _bundleRestoreStoredBytes = storedBytes;
            _bundleRestoreDecodedBytes = bytes.Length;
            if (!ParseAndApply(bytes))
            {
                ReportError(_parseError, 0);
                return;
            }
            if (_bundleRestorePending) return;
            CompleteLoadSuccess();
        }

        public void ContinueBundleRestore()
        {
            if (Status != StatusLoading || !_bundleRestorePending || _data == null) return;
            RefreshAdaptiveLoadBudgets();
            if (!ParseAndApplyBundle(_data))
            {
                ReportError(_parseError, 0);
                return;
            }
            if (_bundleRestorePending) return;
            CompleteLoadSuccess();
        }

        private void CompleteLoadSuccess()
        {
            LoadedWasCompressed = _bundleRestoreWasCompressed;
            LoadedStoredBytes = _bundleRestoreStoredBytes;
            LoadedDecodedBytes = _bundleRestoreDecodedBytes;
            Status = StatusReady;
            StatusMessage = "Ready";
            Notify(LoadSucceededEvent);
        }

        private void ClearExpansionState()
        {
            _expandSource = null;
            _expandTarget = null;
            _expandSectionCount = 0;
            _expandSectionIndex = 0;
            _expandPhase = -1;
        }

        private uint ReadU32From(byte[] data, int offset)
        {
            return (uint)data[offset] |
                   ((uint)data[offset + 1] << 8) |
                   ((uint)data[offset + 2] << 16) |
                   ((uint)data[offset + 3] << 24);
        }

        private byte[] ExpandCompressedContainer(byte[] source)        {
            _standardChecksum = ReadU32At(16) == 3u;
            if (source.Length < HeaderBytes + CompressedTocEntryBytes * 2 ||
                ReadU32At(20) != (uint)CompressedTocEntryBytes)
            {
                Fail("RAC2 compressed directory is truncated or unsupported.");
                return null;
            }

            uint countRaw = ReadU32At(12);
            if (countRaw < 2u || countRaw > 12u)
            {
                Fail("RAC2 compressed section count is unsupported.");
                return null;
            }

            int count = (int)countRaw;
            int storedOffset = HeaderBytes + count * CompressedTocEntryBytes;
            long rawTotal = HeaderBytes + count * TocEntryBytes;
            for (int section = 0; section < count; section++)
            {
                int toc = HeaderBytes + section * CompressedTocEntryBytes;
                uint offsetRaw = ReadU32At(toc + 4);
                uint storedLengthRaw = ReadU32At(toc + 8);
                uint rawLengthRaw = ReadU32At(toc + 12);
                uint codec = ReadU32At(toc + 16);
                if (offsetRaw != (uint)storedOffset || storedLengthRaw == 0u || rawLengthRaw == 0u ||
                    storedLengthRaw > int.MaxValue || rawLengthRaw > StandardMaxBytes ||
                    codec > 1u || codec == 0u && storedLengthRaw != rawLengthRaw ||
                    codec == 1u && storedLengthRaw >= rawLengthRaw)
                {
                    Fail("RAC2 compressed section table is non-canonical.");
                    return null;
                }

                long nextStored = (long)storedOffset + storedLengthRaw;
                rawTotal += rawLengthRaw;
                if (nextStored > source.Length || rawTotal > StandardMaxBytes)
                {
                    Fail("RAC2 compressed section exceeds the Pad memory limit.");
                    return null;
                }
                storedOffset = (int)nextStored;
            }

            if (storedOffset != source.Length)
            {
                Fail("RAC2 compressed container has gaps or trailing bytes.");
                return null;
            }

            byte[] expanded = new byte[(int)rawTotal];
            WriteU32At(expanded, 0, Magic);
            WriteU32At(expanded, 4, ReadU32At(4));
            WriteU32At(expanded, 8, (uint)expanded.Length);
            WriteU32At(expanded, 12, countRaw);
            WriteU32At(expanded, 16, 0u);
            WriteU32At(expanded, 20, 0u);

            int rawOffset = HeaderBytes + count * TocEntryBytes;
            for (int section = 0; section < count; section++)
            {
                int toc = HeaderBytes + section * CompressedTocEntryBytes;
                uint type = ReadU32At(toc);
                int sectionStoredOffset = (int)ReadU32At(toc + 4);
                int storedLength = (int)ReadU32At(toc + 8);
                int rawLength = (int)ReadU32At(toc + 12);
                uint codec = ReadU32At(toc + 16);
                uint checksum = ReadU32At(toc + 20);

                int legacyToc = HeaderBytes + section * TocEntryBytes;
                WriteU32At(expanded, legacyToc, type);
                WriteU32At(expanded, legacyToc + 4, (uint)rawOffset);
                WriteU32At(expanded, legacyToc + 8, (uint)rawLength);
                WriteU32At(expanded, legacyToc + 12, 0u);

                if (codec == 0u)
                {
                    Buffer.BlockCopy(source, sectionStoredOffset, expanded, rawOffset, rawLength);
                }
                else if (!DecompressLz4Block(source, sectionStoredOffset, storedLength, expanded, rawOffset, rawLength))
                {
                    Fail("RAC2 LZ4 section is malformed.");
                    return null;
                }

                if (Adler32(expanded, rawOffset, rawLength) != checksum)
                {
                    Fail("RAC2 section checksum mismatch.");
                    return null;
                }
                rawOffset += rawLength;
            }

            if (rawOffset != expanded.Length)
            {
                Fail("RAC2 expanded section sizes are inconsistent.");
                return null;
            }
            return expanded;
        }

        private bool DecompressLz4Block(byte[] source, int sourceOffset, int sourceLength, byte[] target, int targetOffset, int targetLength)
        {
            int input = sourceOffset;
            int inputEnd = sourceOffset + sourceLength;
            int output = targetOffset;
            int outputEnd = targetOffset + targetLength;
            while (input < inputEnd)
            {
                int token = source[input++];
                int literalLength = token >> 4;
                if (literalLength == 15)
                {
                    int extension;
                    do
                    {
                        if (input >= inputEnd) return false;
                        extension = source[input++];
                        literalLength += extension;
                    }
                    while (extension == 255);
                }

                if (literalLength > inputEnd - input || literalLength > outputEnd - output) return false;
                Buffer.BlockCopy(source, input, target, output, literalLength);
                input += literalLength;
                output += literalLength;
                if (input == inputEnd) return output == outputEnd;
                if (input + 2 > inputEnd) return false;

                int distance = source[input] | source[input + 1] << 8;
                input += 2;
                if (distance == 0 || distance > output - targetOffset) return false;

                int matchLength = token & 15;
                if (matchLength == 15)
                {
                    int extension;
                    do
                    {
                        if (input >= inputEnd) return false;
                        extension = source[input++];
                        matchLength += extension;
                    }
                    while (extension == 255);
                }
                matchLength += 4;
                if (matchLength > outputEnd - output) return false;
                int reference = output - distance;
                int remaining = matchLength;
                while (remaining > 0)
                {
                    // Copy only bytes that already exist, then grow the repeated
                    // region exponentially. This preserves LZ4 overlap semantics
                    // without executing one Udon VM loop per output byte.
                    int available = output - reference;
                    int copyLength = remaining < available ? remaining : available;
                    Buffer.BlockCopy(target, reference, target, output, copyLength);
                    output += copyLength;
                    remaining -= copyLength;
                }
            }
            return output == outputEnd;
        }

        private uint Adler32(byte[] data, int offset, int length)
        {
            const int modulus = 65521;
            int a = 1;
            int b = 0;
            int end = offset + length;
            while (offset < end)
            {
                int blockEnd = offset + (_standardChecksum ? 2776 : 5552);
                if (blockEnd > end) blockEnd = end;
                while (offset < blockEnd)
                {
                    a += data[offset++];
                    b += a;
                }
                a %= modulus;
                b %= modulus;
            }
            // Preserve the exporter two's-complement low words without a checked conversion.
            return ((uint)(b & 0xFFFF) << 16) | (uint)(a & 0xFFFF);
        }

        private void WriteU32At(byte[] data, int offset, uint value)
        {
            // Udon uses a checked uint-to-byte conversion, so mask first.
            data[offset] = (byte)(value & 0xFFu);
            data[offset + 1] = (byte)((value >> 8) & 0xFFu);
            data[offset + 2] = (byte)((value >> 16) & 0xFFu);
            data[offset + 3] = (byte)((value >> 24) & 0xFFu);
        }
        private void PrepareForLoad()
        {
            _bundleRestorePending = false;
            _bundleRestoreNodeCursor = 0;
            RestoredRenderNodeCount = 0;
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer != null && !Networking.IsOwner(gameObject)) Networking.SetOwner(localPlayer, gameObject);
            if (TargetPickup != null)
            {
                if (TargetPickup.IsHeld) TargetPickup.Drop();
                TargetPickup.pickupable = false;
            }
            if (TargetCollider != null) TargetCollider.enabled = false;
            if (TargetRigidbody != null)
            {
                TargetRigidbody.isKinematic = true;
                TargetRigidbody.velocity = Vector3.zero;
                TargetRigidbody.angularVelocity = Vector3.zero;
            }
            if (TargetObjectSync != null) TargetObjectSync.Respawn();
            if (ProductController != null) ProductController.ClearProduct();
            transform.localPosition = InitialLocalPosition;
            transform.localRotation = Quaternion.Euler(InitialLocalEulerAngles);
        }

        private void ConfigureInteraction(bool hasCollider, bool isPortable, Bounds bounds)
        {
            if (TargetCollider != null)
            {
                Vector3 size = bounds.size;
                if (size.x < 0.01f) size.x = 0.01f;
                if (size.y < 0.01f) size.y = 0.01f;
                if (size.z < 0.01f) size.z = 0.01f;
                TargetCollider.center = bounds.center;
                TargetCollider.size = size;
                TargetCollider.enabled = hasCollider;
            }
            if (TargetRigidbody != null)
            {
                TargetRigidbody.useGravity = false;
                TargetRigidbody.velocity = Vector3.zero;
                TargetRigidbody.angularVelocity = Vector3.zero;
                TargetRigidbody.isKinematic = !isPortable;
            }
            if (TargetPickup != null) TargetPickup.pickupable = isPortable;
        }

        private int SectionRank(uint type)
        {
            if (type == MetaType) return 0;
            if (type == MeshType) return 1;
            if (type == MatlType) return 2;
            if (type == Tex0Type) return 3;
            if (type == TexnType) return 4;
            if (type == VatiType) return 5;
            if (type == VatpType) return 6;
            if (type == VatnType) return 7;
            if (type == PartType) return 8;
            if (type == PtexType) return 9;
            if (type == IntrType) return 10;
            if (type == ProdType) return 11;
            return -1;
        }

        private void EnsureMaterial(Material template)
        {
            if (_material != null && _materialSource == template) return;
            if (_material != null) Destroy(_material);
            _material = null;
            _materialSource = template;
            if (template == null) return;
            TargetRenderer.sharedMaterial = template;
            _material = TargetRenderer.material;
            _material.name = "RAC2 Material";
        }

        private void SetFloat(string property, float value)
        {
            if (_material.HasProperty(property)) _material.SetFloat(property, value);
        }

        private bool BoundsFitBooth(Vector3 min, Vector3 max)
        {
            float x = BoothWidth * 0.5f;
            float z = BoothDepth * 0.5f;
            float tolerance = BoothBoundaryTolerance;
            return min.x >= -x - tolerance && max.x <= x + tolerance &&
                   min.y >= -tolerance && max.y <= BoothHeight + tolerance &&
                   min.z >= -z - tolerance && max.z <= z + tolerance;
        }

        private void ConfigureParticleBooth(Rac2ParticlePlayer player)
        {
            if (player == null) return;
            player.ClipToBooth = EnforceStandardBoothProfile;
            player.BoothWidth = BoothWidth;
            player.BoothDepth = BoothDepth;
            player.BoothHeight = BoothHeight;
            player.BoothBoundaryTolerance = BoothBoundaryTolerance;
        }

        private bool BoundsContain(
            Vector3 actualMin, Vector3 actualMax,
            Vector3 declaredMin, Vector3 declaredMax)
        {
            const float tolerance = 0.001f;
            return
                actualMin.x >= declaredMin.x - tolerance &&
                actualMin.y >= declaredMin.y - tolerance &&
                actualMin.z >= declaredMin.z - tolerance &&
                actualMax.x <= declaredMax.x + tolerance &&
                actualMax.y <= declaredMax.y + tolerance &&
                actualMax.z <= declaredMax.z + tolerance;
        }

        private bool BoundsConsistent(Vector3 actualMin, Vector3 actualMax, Vector3 declaredMin, Vector3 declaredMax)
        {
            return FaceOk(actualMin.x, actualMax.x, declaredMin.x, declaredMax.x) &&
                   FaceOk(actualMin.y, actualMax.y, declaredMin.y, declaredMax.y) &&
                   FaceOk(actualMin.z, actualMax.z, declaredMin.z, declaredMax.z);
        }

        private bool FaceOk(float actualMin, float actualMax, float declaredMin, float declaredMax)
        {
            float low = actualMin - declaredMin;
            float high = declaredMax - actualMax;
            return low >= -0.001f && high >= -0.001f && low <= 0.101f && high <= 0.101f;
        }

        private bool FiniteVector(Vector3 value, float cap)
        {
            return Finite(value.x, cap) && Finite(value.y, cap) && Finite(value.z, cap);
        }

        private bool FiniteColor(Color value)
        {
            return Finite(value.r, 1024f) && Finite(value.g, 1024f) &&
                   Finite(value.b, 1024f) && Finite(value.a, 1024f);
        }

        private bool Range(float value, float minimum, float maximum)
        {
            return value == value && value >= minimum && value <= maximum;
        }

        private bool Finite(float value, float cap)
        {
            return value == value && value <= cap && value >= -cap;
        }

        private uint ReadU32At(int offset)
        {
            return (uint)_data[offset] |
                   ((uint)_data[offset + 1] << 8) |
                   ((uint)_data[offset + 2] << 16) |
                   ((uint)_data[offset + 3] << 24);
        }

        private uint ReadU32()
        {
            uint value = ReadU32At(_cursor);
            _cursor += 4;
            return value;
        }

        private float ReadF32At(int offset)
        {
            return BitConverter.ToSingle(_data, offset);
        }

        private float ReadF32()
        {
            float value = ReadF32At(_cursor);
            _cursor += 4;
            return value;
        }

        private float DecodeVatUnitHalfAt(int offset)
        {
            int bits = (int)_data[offset] | ((int)_data[offset + 1] << 8);
            if ((bits & 32768) != 0) return -1f;
            int exponent = (bits >> 10) & 31;
            int mantissa = bits & 1023;
            if (exponent == 0) return mantissa * 0.0000000596046448f;
            if (exponent > 15) return -1f;
            return (1024f + mantissa) / (float)(1 << (25 - exponent));
        }

        private bool Fail(string message)
        {
            _parseError = message;
            _data = null;
            return false;
        }

        private void ReportError(string message, int code)
        {
            _data = null;
            ClearExpansionState();
            LastError = message;
            LastHttpErrorCode = code;
            Status = StatusError;
            StatusMessage = message;
            Debug.LogError("[RAC2] " + message);
            Notify(LoadFailedEvent);
        }

        private void Notify(string eventName)
        {
            if (CallbackReceiver != null && eventName != null && eventName.Length > 0)
            {
                CallbackReceiver.SendCustomEvent(eventName);
            }
        }
        [Header("RAC2 v3 scene pool")]
        [Tooltip("Optional owning objects for the preallocated renderer slots.")]
        public GameObject[] SceneObjects;
        public MeshFilter[] SceneMeshFilters;
        public MeshRenderer[] SceneRenderers;
        [Tooltip("One preallocated player per particle emitter (maximum 4).")]
        public Rac2ParticlePlayer[] ParticlePlayers;

        [Header("RAC2 v3 diagnostics")]
        public int LoadedRenderNodeCount;
        public int LoadedMaterialCount;
        public int LoadedParticleEmitterCount;

        private const uint BundleSceneType = 0x454e4353u;
        private const uint BundleNodeType = 0x45444f4eu;
        private const int BundleSceneBytes = 32;
        private const int BundleNodeHeaderBytes = 80;
        private const int BundleMeshHeaderBytes = 24;
        private const int BundleMaterialRecordHeaderBytes = 36;
        private const int BundleParticleRecordHeaderBytes = 32;
        private const int BundleMaxRenderNodes = 16;
        private const int BundleMaxMaterials = 64;
        private const int BundleMaxParticleEmitters = 4;

        private Mesh[] _bundleMeshes;
        private Material[] _bundleMaterials;
        private Texture2D[] _bundleTextures;
        private Texture2D[] _bundleNormalTextures;
        private Texture2D[] _bundleVatPositionTextures;
        private Texture2D[] _bundleVatNormalTextures;
        private Mesh[] _bundleParticleMeshes;
        private Texture2D[] _bundleParticleTextures;
        private int _bundleMaterialCursor;
        private bool _bundleHasActualBounds;
        private Vector3 _bundleActualMin;
        private Vector3 _bundleActualMax;
        private bool _bundleSawGenericMaterial;
        private bool _bundleSawLilToonMaterial;
        private bool _bundleRestorePending;
        private int _bundleRestoreNodeIndex;
        private int _bundleRestoreNodeCursor;
        private int _bundleRestoreVertexTotal;
        private int _bundleRestoreIndexTotal;
        private int _bundleRestoreVatTotal;
        private bool _bundleRestoreWasCompressed;
        private int _bundleRestoreStoredBytes;
        private int _bundleRestoreDecodedBytes;
        public int RestoredRenderNodeCount;

        private Mesh _bundleParsedMesh;
        private int _bundleParsedVertexCount;
        private int _bundleParsedIndexCount;
        private bool _bundleParsedHasNormals;
        private bool _bundleParsedHasUv0;
        private bool _bundleParsedHasUv1;
        private bool _bundleParsedHasColors;
        private bool _bundleParsedHasTangents;
        private Vector3 _bundleParsedMin;
        private Vector3 _bundleParsedMax;

        private int _bundleVatFrameCount;
        private float _bundleVatFps;
        private int _bundleVatWidth;
        private int _bundleVatRowsPerFrame;
        private int _bundleVatHeight;
        private bool _bundleVatLoop;
        private bool _bundleVatHasNormals;
        private Bounds _bundleVatBounds;
        private int _nodeRestorePhase;
        private Texture2D[] _sharedTextureCache;
        private bool[] _sharedTextureLinear;
        private int _sharedTextureCount;
        private int _nodeMaterialCursor;
        private int _nodeMaterialIndex;
        private int _nodeMaterialStage;
        private bool _nodeMaterialsPending;
        private Material[] _nodeInstances;
        private int[] _nodeProfiles;
        private int[] _nodeRenderModes;
        private int[] _nodeCullModes;
        private float[] _nodeCutoffs;
        private float[] _nodeBumpScales;
        private float[] _nodeShadowStrengths;
        private float[] _nodeUnlitValues;
        private float[] _nodeLightMinimums;
        private float[] _nodeLightMaximums;
        private float[] _nodeMonochromes;
        private Color[] _nodeColors;
        private Texture2D[] _nodeTextures;
        private Texture2D[] _nodeNormals;
        private Material[] _nodeTemplates;
        private bool _bundleMeshPending;
        private int _decodePhase = -1;
        private int _decodeIndex;
        private int _decodeCursor;
        private int _decodeSubmesh;
        private Mesh _decodeMesh;
        private Vector3[] _decodeVertices;
        private Vector3[] _decodeNormals;
        private Vector2[] _decodeUv0;
        private Vector2[] _decodeUv1;
        private Color32[] _decodeColors;
        private Vector4[] _decodeTangents;
        private int[] _decodeCounts;
        private int[] _decodeTriangles;

        private bool ParseAndApplyBundle(byte[] bytes)
        {
            bool resumingRestore = _bundleRestorePending;
            if (!resumingRestore)
            {
                ClearBundleResources();
                if (TargetRenderer != null) TargetRenderer.enabled = false;
                if (ParticlePlayer != null) ParticlePlayer.ClearParticles();
                _bundleRestoreNodeIndex = 0;
                _bundleRestoreNodeCursor = 0;
                _bundleRestoreVertexTotal = 0;
                _bundleRestoreIndexTotal = 0;
                _bundleRestoreVatTotal = 0;
                RestoredRenderNodeCount = 0;
            }

            uint sectionCountRaw = ReadU32At(12);
            if (sectionCountRaw < 3u || sectionCountRaw > 6u)
                return BundleFail("RAC2 v3 section count is unsupported.");
            int sectionCount = (int)sectionCountRaw;
            int expectedOffset = HeaderBytes + sectionCount * TocEntryBytes;
            int previousRank = -1;
            int metaOffset = 0;
            int metaLength = 0;
            int sceneOffset = 0;
            int sceneLength = 0;
            int nodeOffset = 0;
            int nodeLength = 0;
            int particleOffset = 0;
            int particleLength = 0;
            int interactionOffset = 0;
            int interactionLength = 0;
            int productOffset = 0;
            int productLength = 0;

            for (int section = 0; section < sectionCount; section++)
            {
                int toc = HeaderBytes + section * TocEntryBytes;
                uint type = ReadU32At(toc);
                int rank = BundleSectionRank(type);
                uint offsetRaw = ReadU32At(toc + 4);
                uint lengthRaw = ReadU32At(toc + 8);
                if (rank < 0 || rank <= previousRank || ReadU32At(toc + 12) != 0u ||
                    offsetRaw != (uint)expectedOffset || lengthRaw == 0u || lengthRaw > int.MaxValue)
                    return BundleFail("RAC2 v3 section table is non-canonical.");
                if ((section == 0 && type != MetaType) ||
                    (section == 1 && type != BundleSceneType) ||
                    (section == 2 && type != BundleNodeType))
                    return BundleFail("RAC2 v3 must begin with META, SCNE, and NODE.");

                long next = (long)expectedOffset + lengthRaw;
                if (next > bytes.Length) return BundleFail("RAC2 v3 section extends past end-of-file.");
                if (type == MetaType) { metaOffset = expectedOffset; metaLength = (int)lengthRaw; }
                else if (type == BundleSceneType) { sceneOffset = expectedOffset; sceneLength = (int)lengthRaw; }
                else if (type == BundleNodeType) { nodeOffset = expectedOffset; nodeLength = (int)lengthRaw; }
                else if (type == PartType) { particleOffset = expectedOffset; particleLength = (int)lengthRaw; }
                else if (type == IntrType) { interactionOffset = expectedOffset; interactionLength = (int)lengthRaw; }
                else { productOffset = expectedOffset; productLength = (int)lengthRaw; }
                previousRank = rank;
                expectedOffset = (int)next;
            }
            if (expectedOffset != bytes.Length || metaLength != MetaBytes || sceneLength != BundleSceneBytes)
                return BundleFail("RAC2 v3 contains a gap, trailing bytes, or invalid META/SCNE size.");

            Vector3 declaredCenter = new Vector3(
                ReadF32At(metaOffset), ReadF32At(metaOffset + 4), ReadF32At(metaOffset + 8));
            Vector3 declaredSize = new Vector3(
                ReadF32At(metaOffset + 12), ReadF32At(metaOffset + 16), ReadF32At(metaOffset + 20));
            if (!FiniteVector(declaredCenter, 1000000f) || !FiniteVector(declaredSize, 2000000f) ||
                declaredSize.x < 0f || declaredSize.y < 0f || declaredSize.z < 0f)
                return BundleFail("RAC2 v3 META bounds are invalid.");

            if (ReadU32At(sceneOffset) != 1u || ReadU32At(sceneOffset + 28) != 0u)
                return BundleFail("RAC2 v3 SCNE format is unsupported.");
            uint renderCountRaw = ReadU32At(sceneOffset + 4);
            uint materialCountRaw = ReadU32At(sceneOffset + 8);
            uint emitterCountRaw = ReadU32At(sceneOffset + 12);
            uint vertexCountRaw = ReadU32At(sceneOffset + 16);
            uint indexCountRaw = ReadU32At(sceneOffset + 20);
            uint vatCountRaw = ReadU32At(sceneOffset + 24);
            if (renderCountRaw > BundleMaxRenderNodes || materialCountRaw > BundleMaxMaterials ||
                emitterCountRaw > BundleMaxParticleEmitters || vertexCountRaw > StandardMaxVertices ||
                indexCountRaw > StandardMaxIndices || vatCountRaw > renderCountRaw ||
                renderCountRaw == 0u && emitterCountRaw == 0u)
                return BundleFail("RAC2 v3 SCNE counts exceed the Pad profile.");

            int renderCount = (int)renderCountRaw;
            int materialCount = (int)materialCountRaw;
            int emitterCount = (int)emitterCountRaw;
            if (!BundleRendererPoolAvailable(renderCount))
                return BundleFail("RAC2 v3 renderer pool is too small. Rebuild the ImagePad prefab.");
            if (!BundleParticlePoolAvailable(emitterCount))
                return BundleFail("RAC2 v3 particle pool is too small. Rebuild the ImagePad prefab.");

            if (!resumingRestore)
            {
                _bundleMeshes = new Mesh[renderCount];
                _sharedTextureCache = new Texture2D[BundleMaxMaterials * 2];
                _sharedTextureLinear = new bool[BundleMaxMaterials * 2];
                _sharedTextureCount = 0;
                _bundleMaterials = new Material[materialCount];
                _bundleTextures = new Texture2D[materialCount];
                _bundleNormalTextures = new Texture2D[materialCount];
                _bundleVatPositionTextures = new Texture2D[renderCount];
                _bundleVatNormalTextures = new Texture2D[renderCount];
                _bundleParticleMeshes = new Mesh[emitterCount];
                _bundleParticleTextures = new Texture2D[emitterCount];
                _bundleMaterialCursor = 0;
                _bundleHasActualBounds = false;
                _bundleSawGenericMaterial = false;
                _bundleSawLilToonMaterial = false;
                LoadedHasTexture = false;
                LoadedHasNormalMap = false;
                LoadedVatFrameCount = 0;
                LoadedVatFramesPerSecond = 0f;
            }

            int parsedVertices;
            int parsedIndices;
            int parsedVatNodes;
            if (!ParseBundleNodes(nodeOffset, nodeLength, renderCount, materialCount,
                    out parsedVertices, out parsedIndices, out parsedVatNodes))
                return BundleFail(_parseError);
            if (_bundleRestoreNodeIndex < renderCount)
            {
                _bundleRestorePending = true;
                StatusMessage = "Restoring RAC2 mesh " +
                                _bundleRestoreNodeIndex + "/" + renderCount + "...";
                SendCustomEventDelayedFrames("ContinueBundleRestore", 1);
                return true;
            }
            _bundleRestorePending = false;
            if (parsedVertices != (int)vertexCountRaw || parsedIndices != (int)indexCountRaw ||
                parsedVatNodes != (int)vatCountRaw || _bundleMaterialCursor != materialCount)
                return BundleFail("RAC2 v3 NODE totals do not match SCNE.");

            int particleMaximum = 0;
            if (emitterCount > 0)
            {
                if (particleOffset == 0 ||
                    !ParseBundleParticles(particleOffset, particleLength, emitterCount, out particleMaximum))
                    return BundleFail(string.IsNullOrEmpty(_parseError) ? "RAC2 v3 PART is missing." : _parseError);
            }
            else if (particleOffset != 0)
                return BundleFail("RAC2 v3 PART exists without particle emitters.");

            bool hasCollider = false;
            bool isPortable = false;
            if (interactionOffset != 0)
            {
                if (interactionLength != InteractionBytes || ReadU32At(interactionOffset) != 1u)
                    return BundleFail("RAC2 v3 INTR format is unsupported.");
                uint flags = ReadU32At(interactionOffset + 4);
                hasCollider = (flags & 1u) != 0u;
                isPortable = (flags & 2u) != 0u;
                if ((flags & ~3u) != 0u || isPortable && !hasCollider ||
                    hasCollider && TargetCollider == null ||
                    isPortable && (TargetRigidbody == null || TargetPickup == null))
                    return BundleFail("RAC2 v3 interaction metadata or prefab components are invalid.");
            }

            bool hasProduct;
            string productName;
            string creatorName;
            string productUrl;
            string avatarBlueprintId;
            bool trialEnabled;
            if (!ParseBundleProduct(productOffset, productLength, out hasProduct, out productName,
                    out creatorName, out productUrl, out avatarBlueprintId, out trialEnabled))
                return BundleFail(_parseError);

            if (!_bundleHasActualBounds)
                return BundleFail("RAC2 v3 product has no bounded content.");
            Vector3 declaredMin = declaredCenter - declaredSize * 0.5f;
            Vector3 declaredMax = declaredCenter + declaredSize * 0.5f;
            bool bundleBoundsMatch = emitterCount > 0
                ? BoundsContain(_bundleActualMin, _bundleActualMax, declaredMin, declaredMax)
                : BoundsConsistent(_bundleActualMin, _bundleActualMax, declaredMin, declaredMax);
            if (!bundleBoundsMatch)
                return BundleFail("RAC2 v3 META bounds do not contain the model or VAT geometry.");
            Vector3 bundleSize = _bundleActualMax - _bundleActualMin;
            Vector3 normalizedBundleMin = new Vector3(
                -bundleSize.x * 0.5f, 0f, -bundleSize.z * 0.5f);
            Vector3 normalizedBundleMax = normalizedBundleMin + bundleSize;
            if (EnforceStandardBoothProfile &&
                !BoundsFitBooth(normalizedBundleMin, normalizedBundleMax))
                return BundleFail("RAC2 model geometry or VAT animation is too large for the configured booth.");

            ConfigureInteraction(hasCollider, isPortable,
                new Bounds(
                    (_bundleActualMin + _bundleActualMax) * 0.5f,
                    _bundleActualMax - _bundleActualMin));
            LoadedVertexCount = parsedVertices;
            LoadedIndexCount = parsedIndices;
            LoadedMaterialProfile = _bundleSawGenericMaterial && _bundleSawLilToonMaterial
                ? 2 : _bundleSawLilToonMaterial ? 1 : 0;
            LoadedHasVat = parsedVatNodes > 0;
            LoadedHasParticles = emitterCount > 0;
            LoadedParticleMaximum = particleMaximum;
            LoadedRenderNodeCount = renderCount;
            LoadedMaterialCount = materialCount;
            LoadedParticleEmitterCount = emitterCount;
            LoadedHasCollider = hasCollider;
            LoadedIsPortable = isPortable;
            LoadedHasProduct = hasProduct;
            LoadedProductName = productName;
            LoadedCreatorName = creatorName;
            LoadedProductUrl = productUrl;
            LoadedAvatarBlueprintId = avatarBlueprintId;
            LoadedTrialEnabled = trialEnabled;
            if (ProductController != null)
            {
                if (hasProduct) ProductController.ApplyProduct(
                    productName, creatorName, productUrl, avatarBlueprintId, trialEnabled);
                else ProductController.ClearProduct();
            }
            float playbackStart = Time.timeSinceLevelLoad;
            for (int materialIndex = 0; materialIndex < _bundleMaterials.Length; materialIndex++)
                BundleSetFloat(_bundleMaterials[materialIndex], "_VatStartTime", playbackStart);
            for (int nodeIndex = 0; nodeIndex < renderCount; nodeIndex++)
            {
                GameObject sceneObject = BundleSceneObject(nodeIndex);
                if (sceneObject != null) sceneObject.SetActive(true);
                MeshRenderer sceneRenderer = BundleMeshRenderer(nodeIndex);
                if (sceneRenderer != null) sceneRenderer.enabled = true;
            }
            RestoredRenderNodeCount = renderCount;
            _data = null;
            return true;
        }

        private bool ParseBundleNodes(
            int sectionOffset, int sectionLength, int expectedNodes, int expectedMaterials,
            out int vertexTotal, out int indexTotal, out int vatTotal)
        {
            vertexTotal = _bundleRestoreVertexTotal;
            indexTotal = _bundleRestoreIndexTotal;
            vatTotal = _bundleRestoreVatTotal;
            if (_bundleRestoreNodeCursor == 0)
            {
                if (sectionLength < 8 || ReadU32At(sectionOffset) != 1u ||
                    ReadU32At(sectionOffset + 4) != (uint)expectedNodes)
                    return Fail("RAC2 v3 NODE format is unsupported.");
                _bundleRestoreNodeCursor = sectionOffset + 8;
            }

            int cursor = _bundleRestoreNodeCursor;
            int sectionEnd = sectionOffset + sectionLength;
            int firstNode = _bundleRestoreNodeIndex;
            int lastNode = firstNode + 1;
            if (lastNode > expectedNodes) lastNode = expectedNodes;
            for (int nodeIndex = firstNode; nodeIndex < lastNode; nodeIndex++)
            {
                if (cursor > sectionEnd - BundleNodeHeaderBytes)
                    return Fail("RAC2 v3 NODE record is truncated.");
                uint recordBytesRaw = ReadU32At(cursor);
                uint flags = ReadU32At(cursor + 4);
                uint materialCountRaw = ReadU32At(cursor + 8);
                uint nameLengthRaw = ReadU32At(cursor + 12);
                uint meshLengthRaw = ReadU32At(cursor + 16);
                uint materialsLengthRaw = ReadU32At(cursor + 20);
                uint vatInfoLengthRaw = ReadU32At(cursor + 24);
                uint vatPositionLengthRaw = ReadU32At(cursor + 28);
                uint vatNormalLengthRaw = ReadU32At(cursor + 32);
                if (ReadU32At(cursor + 36) != 0u || (flags & ~3u) != 0u ||
                    recordBytesRaw < BundleNodeHeaderBytes || recordBytesRaw > int.MaxValue ||
                    materialCountRaw < 1u || materialCountRaw > 16u ||
                    nameLengthRaw > 128u || meshLengthRaw < BundleMeshHeaderBytes ||
                    materialsLengthRaw < 8u || vatInfoLengthRaw > int.MaxValue ||
                    vatPositionLengthRaw > int.MaxValue || vatNormalLengthRaw > int.MaxValue)
                    return Fail("RAC2 v3 NODE header is invalid.");

                long payloadBytes = (long)nameLengthRaw + meshLengthRaw + materialsLengthRaw +
                                    vatInfoLengthRaw + vatPositionLengthRaw + vatNormalLengthRaw;
                long recordEndLong = (long)cursor + recordBytesRaw;
                if (recordEndLong > sectionEnd || recordBytesRaw != BundleNodeHeaderBytes + payloadBytes)
                    return Fail("RAC2 v3 NODE record lengths are inconsistent.");
                int recordEnd = (int)recordEndLong;

                Vector3 localPosition = new Vector3(
                    ReadF32At(cursor + 40), ReadF32At(cursor + 44), ReadF32At(cursor + 48));
                Quaternion localRotation = new Quaternion(
                    ReadF32At(cursor + 52), ReadF32At(cursor + 56),
                    ReadF32At(cursor + 60), ReadF32At(cursor + 64));
                Vector3 localScale = new Vector3(
                    ReadF32At(cursor + 68), ReadF32At(cursor + 72), ReadF32At(cursor + 76));
                float rotationLength = localRotation.x * localRotation.x +
                                       localRotation.y * localRotation.y +
                                       localRotation.z * localRotation.z +
                                       localRotation.w * localRotation.w;
                if (!FiniteVector(localPosition, 1000000f) || !FiniteVector(localScale, 10000f) ||
                    !Finite(localRotation.x, 1.001f) || !Finite(localRotation.y, 1.001f) ||
                    !Finite(localRotation.z, 1.001f) || !Finite(localRotation.w, 1.001f) ||
                    rotationLength < 0.5f || rotationLength > 1.5f)
                    return Fail("RAC2 v3 NODE transform is invalid.");

                int payload = cursor + BundleNodeHeaderBytes;
                string nodeName = nameLengthRaw == 0u
                    ? "Renderer" : Encoding.UTF8.GetString(_data, payload, (int)nameLengthRaw);
                int meshOffset = payload + (int)nameLengthRaw;
                int materialsOffset = meshOffset + (int)meshLengthRaw;
                int vatInfoOffset = materialsOffset + (int)materialsLengthRaw;
                int vatPositionOffset = vatInfoOffset + (int)vatInfoLengthRaw;
                int vatNormalOffset = vatPositionOffset + (int)vatPositionLengthRaw;
                bool hasVat = (flags & 1u) != 0u;
                bool positionsFromVatFrameZero = (flags & 2u) != 0u;
                if (positionsFromVatFrameZero && !hasVat)
                    return Fail("RAC2 v3 NODE requests VAT frame-zero positions without VAT.");
                if (hasVat &&
                    !ParseBundleVat(vatInfoOffset, (int)vatInfoLengthRaw,
                        vatPositionOffset, (int)vatPositionLengthRaw,
                        vatNormalOffset, (int)vatNormalLengthRaw))
                    return false;

                if (_nodeRestorePhase == 0)
                {
                    if (!ParseBundleMesh(meshOffset, (int)meshLengthRaw, (int)materialCountRaw, nodeName,
                            positionsFromVatFrameZero, vatPositionOffset, (int)vatPositionLengthRaw))
                        return false;
                    if (_bundleMeshPending) return true;
                    _bundleMeshes[nodeIndex] = _bundleParsedMesh;
                    _nodeRestorePhase = 1;
                    return true;
                }
                Mesh nodeMesh = _bundleMeshes[nodeIndex];
                int nodeVertices = _bundleParsedVertexCount;
                int nodeIndices = _bundleParsedIndexCount;
                Vector3 nodeMin = _bundleParsedMin;
                Vector3 nodeMax = _bundleParsedMax;
                Bounds contentBounds = hasVat ? _bundleVatBounds
                    : new Bounds((nodeMin + nodeMax) * 0.5f, nodeMax - nodeMin);
                if (_nodeRestorePhase == 1)
                {
                    if (hasVat)
                    {
                        if (!_bundleParsedHasUv1) return Fail("RAC2 v3 VAT node has no vertex lookup UV.");
                        if (!BoundsContain(nodeMin, nodeMax, _bundleVatBounds.min, _bundleVatBounds.max))
                            return Fail("RAC2 v3 VAT bounds do not contain the base mesh.");
                        _bundleVatPositionTextures[nodeIndex] = ParseVatTexture(vatPositionOffset,
                            (int)vatPositionLengthRaw, 1, _bundleVatWidth, _bundleVatHeight, "NODE VATP");
                        if (_bundleVatPositionTextures[nodeIndex] == null) return false;
                        if (LoadedVatFrameCount == 0)
                        {
                            LoadedVatFrameCount = _bundleVatFrameCount;
                            LoadedVatFramesPerSecond = _bundleVatFps;
                        }
                    }
                    else if (vatInfoLengthRaw != 0u || vatPositionLengthRaw != 0u || vatNormalLengthRaw != 0u)
                        return Fail("RAC2 v3 NODE has VAT payloads without the VAT flag.");
                    _nodeRestorePhase = 2;
                    return true;
                }
                if (_nodeRestorePhase == 2)
                {
                    if (hasVat && _bundleVatHasNormals)
                    {
                        _bundleVatNormalTextures[nodeIndex] = ParseVatTexture(vatNormalOffset,
                            (int)vatNormalLengthRaw, 2, _bundleVatWidth, _bundleVatHeight, "NODE VATN");
                        if (_bundleVatNormalTextures[nodeIndex] == null) return false;
                    }
                    _nodeRestorePhase = 3;
                    return true;
                }
                MeshFilter filter = BundleMeshFilter(nodeIndex);
                MeshRenderer renderer = BundleMeshRenderer(nodeIndex);
                if (filter == null || renderer == null)
                    return Fail("RAC2 v3 renderer slot is incomplete.");
                if (_nodeRestorePhase == 3)
                {
                    if (!ParseBundleMaterials(materialsOffset, (int)materialsLengthRaw,
                            (int)materialCountRaw, renderer, hasVat,
                            _bundleVatPositionTextures[nodeIndex], _bundleVatNormalTextures[nodeIndex]))
                        return false;
                    if (_nodeMaterialsPending) return true;
                    _nodeRestorePhase = 4;
                    return true;
                }
                nodeMesh.bounds = contentBounds;
                filter.sharedMesh = nodeMesh;
                _nodeRestorePhase = 0;

                Transform nodeTransform = BundleSceneTransform(nodeIndex, filter);
                if (nodeTransform != null)
                {
                    nodeTransform.localPosition = localPosition;
                    nodeTransform.localRotation = localRotation;
                    nodeTransform.localScale = localScale;
                }
                GameObject sceneObject = BundleSceneObject(nodeIndex);
                if (sceneObject != null) sceneObject.SetActive(false);
                renderer.enabled = false;
                IncludeBundleBounds(contentBounds.min, contentBounds.max,
                    localPosition, localRotation, localScale);

                _bundleRestoreVertexTotal += nodeVertices;
                _bundleRestoreIndexTotal += nodeIndices;
                _bundleRestoreVatTotal += hasVat ? 1 : 0;
                if (_bundleRestoreVertexTotal > StandardMaxVertices ||
                    _bundleRestoreIndexTotal > StandardMaxIndices ||
                    _bundleMaterialCursor > expectedMaterials)
                    return Fail("RAC2 v3 NODE totals exceed the Pad profile.");
                cursor = recordEnd;
                _bundleRestoreNodeIndex = nodeIndex + 1;
                RestoredRenderNodeCount = _bundleRestoreNodeIndex;
            }
            _bundleRestoreNodeCursor = cursor;
            vertexTotal = _bundleRestoreVertexTotal;
            indexTotal = _bundleRestoreIndexTotal;
            vatTotal = _bundleRestoreVatTotal;
            if (_bundleRestoreNodeIndex == expectedNodes && cursor != sectionEnd)
                return Fail("RAC2 v3 NODE cursor is inconsistent.");
            return true;
        }

        private bool ParseBundleMesh(int offset, int length, int expectedSubmeshes, string label,
            bool positionsFromVatFrameZero, int vatPositionOffset, int vatPositionLength)
        {
            _bundleParsedMesh = null;
            if (length < BundleMeshHeaderBytes)
                return Fail("RAC2 v3 mesh format is unsupported for " + label + ".");
            uint meshVersion = ReadU32At(offset);
            if (meshVersion != (positionsFromVatFrameZero ? 2u : 1u))
                return Fail("RAC2 v3 mesh position encoding is unsupported for " + label + ".");
            uint vertexRaw = ReadU32At(offset + 4);
            uint submeshRaw = ReadU32At(offset + 8);
            uint attributes = ReadU32At(offset + 12);
            uint indexBits = ReadU32At(offset + 16);
            uint indexRaw = ReadU32At(offset + 20);
            if (vertexRaw == 0u || vertexRaw > StandardMaxVertices ||
                submeshRaw != (uint)expectedSubmeshes || submeshRaw < 1u || submeshRaw > 16u ||
                indexRaw == 0u || indexRaw > StandardMaxIndices || (int)indexRaw % 3 != 0 ||
                (attributes & ~KnownAttributes) != 0u || indexBits != 32u)
                return Fail("RAC2 v3 mesh counts or attributes are invalid for " + label + ".");

            int vertexCount = (int)vertexRaw;
            int submeshCount = (int)submeshRaw;
            int indexCount = (int)indexRaw;
            bool hasNormals = (attributes & AttrNormals) != 0u;
            bool hasUv0 = (attributes & AttrUv0) != 0u;
            bool hasUv1 = (attributes & AttrUv1) != 0u;
            bool hasColors = (attributes & AttrColors) != 0u;
            bool hasTangents = (attributes & AttrTangents) != 0u;
            long expectedLength = BundleMeshHeaderBytes + (positionsFromVatFrameZero ? 0L : (long)vertexCount * 12L) +
                                  (hasNormals ? (long)vertexCount * 12L : 0L) +
                                  (hasUv0 ? (long)vertexCount * 8L : 0L) +
                                  (hasUv1 ? (long)vertexCount * 8L : 0L) +
                                  (hasColors ? (long)vertexCount * 4L : 0L) +
                                  (hasTangents ? (long)vertexCount * 16L : 0L) +
                                  (long)submeshCount * 4L + (long)indexCount * 4L;
            if (expectedLength != length)
                return Fail("RAC2 v3 mesh byte length is inconsistent for " + label + ".");


            if (_decodePhase < 0)
            {
                _decodeCursor = offset + BundleMeshHeaderBytes;
                _decodeVertices = new Vector3[vertexCount];
                _decodeNormals = hasNormals ? new Vector3[vertexCount] : null;
                _decodeUv0 = hasUv0 ? new Vector2[vertexCount] : null;
                _decodeUv1 = hasUv1 ? new Vector2[vertexCount] : null;
                _decodeColors = hasColors ? new Color32[vertexCount] : null;
                _decodeTangents = hasTangents ? new Vector4[vertexCount] : null;
                _decodePhase = 0;
                _decodeIndex = 0;
                _decodeSubmesh = 0;
                _bundleParsedMesh = null;
                _bundleParsedMin = Vector3.zero;
                _bundleParsedMax = Vector3.zero;
                if (positionsFromVatFrameZero)
                {
                    long expectedRaw = (long)_bundleVatWidth * _bundleVatHeight * 8L;
                    if (vatPositionLength < VatTextureHeaderBytes ||
                        ReadU32At(vatPositionOffset) != 1u || ReadU32At(vatPositionOffset + 4) != 1u ||
                        ReadU32At(vatPositionOffset + 8) != (uint)_bundleVatWidth ||
                        ReadU32At(vatPositionOffset + 12) != (uint)_bundleVatHeight ||
                        ReadU32At(vatPositionOffset + 16) != (uint)expectedRaw ||
                        vatPositionLength != VatTextureHeaderBytes + expectedRaw ||
                        (long)_bundleVatWidth * _bundleVatRowsPerFrame < vertexCount)
                        return Fail("RAC2 v3 VAT frame-zero positions are invalid.");
                }
            }
            _bundleMeshPending = true;
            _cursor = _decodeCursor;
            int budget = AdaptiveLoadingEnabled
                ? Mathf.Max(16, Mathf.RoundToInt(MeshElementsPerFrame * AdaptiveLoadScale))
                : Mathf.Max(16, MeshElementsPerFrame);
            int stop = Mathf.Min(vertexCount, _decodeIndex + budget);
            if (_decodePhase <= 5)
            {
                bool present = _decodePhase == 0 || _decodePhase == 1 && hasNormals ||
                    _decodePhase == 2 && hasUv0 || _decodePhase == 3 && hasUv1 ||
                    _decodePhase == 4 && hasColors || _decodePhase == 5 && hasTangents;
                if (present)
                {
                    for (int index = _decodeIndex; index < stop; index++)
                    {
                        if (_decodePhase == 0)
                        {
                            Vector3 value;
                            if (positionsFromVatFrameZero)
                            {
                                int pixel = vatPositionOffset + VatTextureHeaderBytes + index * 8;
                                float x = DecodeVatUnitHalfAt(pixel);
                                float y = DecodeVatUnitHalfAt(pixel + 2);
                                float z = DecodeVatUnitHalfAt(pixel + 4);
                                if (x < 0f || x > 1f || y < 0f || y > 1f || z < 0f || z > 1f)
                                    return Fail("RAC2 v3 VAT frame-zero half value is invalid.");
                                Vector3 minimum = _bundleVatBounds.min;
                                Vector3 size = _bundleVatBounds.size;
                                value = new Vector3(minimum.x + x * size.x, minimum.y + y * size.y, minimum.z + z * size.z);
                            }
                            else value = new Vector3(ReadF32(), ReadF32(), ReadF32());
                            if (!FiniteVector(value, 1000000f)) return Fail("RAC2 v3 mesh position is invalid.");
                            _decodeVertices[index] = value;
                            if (index == 0) { _bundleParsedMin = value; _bundleParsedMax = value; }
                            else
                            {
                                _bundleParsedMin = Vector3.Min(_bundleParsedMin, value);
                                _bundleParsedMax = Vector3.Max(_bundleParsedMax, value);
                            }
                        }
                        else if (_decodePhase == 1)
                        {
                            Vector3 value = new Vector3(ReadF32(), ReadF32(), ReadF32());
                            if (!FiniteVector(value, 16f)) return Fail("RAC2 v3 mesh normal is invalid.");
                            _decodeNormals[index] = value;
                        }
                        else if (_decodePhase == 2 || _decodePhase == 3)
                        {
                            Vector2 value = new Vector2(ReadF32(), ReadF32());
                            if (!Finite(value.x, 1000000f) || !Finite(value.y, 1000000f))
                                return Fail("RAC2 v3 mesh UV is invalid.");
                            if (_decodePhase == 2) _decodeUv0[index] = value;
                            else _decodeUv1[index] = value;
                        }
                        else if (_decodePhase == 4)
                        {
                            _decodeColors[index] = new Color32(_data[_cursor], _data[_cursor + 1],
                                _data[_cursor + 2], _data[_cursor + 3]);
                            _cursor += 4;
                        }
                        else
                        {
                            Vector4 value = new Vector4(ReadF32(), ReadF32(), ReadF32(), ReadF32());
                            if (!Finite(value.x, 16f) || !Finite(value.y, 16f) ||
                                !Finite(value.z, 16f) || !Finite(value.w, 1.001f))
                                return Fail("RAC2 v3 mesh tangent is invalid.");
                            _decodeTangents[index] = value;
                        }
                    }
                }
                _decodeIndex = present ? stop : vertexCount;
                if (_decodeIndex == vertexCount) { _decodePhase++; _decodeIndex = 0; }
            }
            else if (_decodePhase == 6)
            {
                _decodeCounts = new int[submeshCount];
                int total = 0;
                for (int submesh = 0; submesh < submeshCount; submesh++)
                {
                    uint count = ReadU32();
                    if (count == 0u || count > StandardMaxIndices || (int)count % 3 != 0)
                        return Fail("RAC2 v3 submesh index count is invalid.");
                    total += (int)count;
                    _decodeCounts[submesh] = (int)count;
                }
                if (total != indexCount) return Fail("RAC2 v3 submesh totals are inconsistent.");
                _decodeMesh = new Mesh();
                _decodeMesh.name = "RAC2 v3 " + label;
                _decodeMesh.indexFormat = IndexFormat.UInt16;
                _decodeMesh.subMeshCount = submeshCount;
                _decodePhase++;
            }
            else if (_decodePhase < 13)
            {
                // At most one native Mesh upload per continuation.
                if (_decodePhase == 7) _decodeMesh.vertices = _decodeVertices;
                else if (_decodePhase == 8 && hasNormals) _decodeMesh.normals = _decodeNormals;
                else if (_decodePhase == 9 && hasUv0) _decodeMesh.uv = _decodeUv0;
                else if (_decodePhase == 10 && hasUv1) _decodeMesh.uv2 = _decodeUv1;
                else if (_decodePhase == 11 && hasColors) _decodeMesh.colors32 = _decodeColors;
                else if (_decodePhase == 12 && hasTangents) _decodeMesh.tangents = _decodeTangents;
                _decodePhase++;
            }
            else if (_decodePhase == 13)
            {
                int count = _decodeCounts[_decodeSubmesh];
                if (_decodeTriangles == null) _decodeTriangles = new int[count];
                int end = Mathf.Min(count, _decodeIndex + budget);
                for (int index = _decodeIndex; index < end; index++)
                {
                    uint value = ReadU32();
                    if (value >= vertexRaw) return Fail("RAC2 v3 index references a missing vertex.");
                    _decodeTriangles[index] = (int)value;
                }
                _decodeIndex = end;
                if (end == count)
                {
                    _decodeMesh.SetTriangles(_decodeTriangles, _decodeSubmesh, false);
                    _decodeTriangles = null;
                    _decodeIndex = 0;
                    _decodeSubmesh++;
                    if (_decodeSubmesh == submeshCount) _decodePhase++;
                }
            }
            else
            {
                if (_cursor != offset + length) return Fail("RAC2 v3 mesh cursor is inconsistent.");
                if (!hasNormals) _decodeMesh.RecalculateNormals();
                _bundleParsedMesh = _decodeMesh;
                _decodeMesh = null; // ownership moves to the node, not the decode scratch state
                _bundleParsedVertexCount = vertexCount;
                _bundleParsedIndexCount = indexCount;
                _bundleParsedHasNormals = hasNormals;
                _bundleParsedHasUv0 = hasUv0;
                _bundleParsedHasUv1 = hasUv1;
                _bundleParsedHasColors = hasColors;
                _bundleParsedHasTangents = hasTangents;
                ResetMeshDecode();
                return true;
            }
            _decodeCursor = _cursor;
            return true;
        }

        private void ResetMeshDecode()
        {
            if (_decodeMesh != null) Destroy(_decodeMesh);
            _decodeMesh = null;
            _decodeVertices = null;
            _decodeNormals = null;
            _decodeUv0 = null;
            _decodeUv1 = null;
            _decodeColors = null;
            _decodeTangents = null;
            _decodeCounts = null;
            _decodeTriangles = null;
            _decodePhase = -1;
            _decodeIndex = 0;
            _decodeSubmesh = 0;
            _bundleMeshPending = false;
        }

        private bool ParseBundleVat(
            int infoOffset, int infoLength, int positionOffset, int positionLength,
            int normalOffset, int normalLength)
        {
            if (infoLength < VatInfoBaseBytes || positionLength < VatTextureHeaderBytes ||
                ReadU32At(infoOffset) != 1u)
                return Fail("RAC2 v3 VAT metadata is incomplete.");
            uint flags = ReadU32At(infoOffset + 4);
            uint frameRaw = ReadU32At(infoOffset + 8);
            float fps = ReadF32At(infoOffset + 12);
            uint widthRaw = ReadU32At(infoOffset + 16);
            uint rowsRaw = ReadU32At(infoOffset + 20);
            uint heightRaw = ReadU32At(infoOffset + 24);
            uint positionFormat = ReadU32At(infoOffset + 28);
            uint normalFormat = ReadU32At(infoOffset + 32);
            uint nameLengthRaw = ReadU32At(infoOffset + 36);
            Vector3 center = new Vector3(
                ReadF32At(infoOffset + 40), ReadF32At(infoOffset + 44), ReadF32At(infoOffset + 48));
            Vector3 size = new Vector3(
                ReadF32At(infoOffset + 52), ReadF32At(infoOffset + 56), ReadF32At(infoOffset + 60));
            bool loop = (flags & 1u) != 0u;
            bool hasNormals = (flags & 2u) != 0u;
            if ((flags & ~3u) != 0u || frameRaw < 2u || frameRaw > 240u ||
                !Range(fps, 1f, 60f) || widthRaw == 0u || widthRaw > 2048u ||
                rowsRaw == 0u || rowsRaw > 4096u || heightRaw == 0u || heightRaw > 4096u ||
                heightRaw != rowsRaw * frameRaw || positionFormat != 1u ||
                normalFormat != (hasNormals ? 2u : 0u) || nameLengthRaw > 64u ||
                infoLength != VatInfoBaseBytes + (int)nameLengthRaw ||
                !FiniteVector(center, 1000000f) || !FiniteVector(size, 2000000f) ||
                size.x < 0f || size.y < 0f || size.z < 0f ||
                hasNormals != (normalLength != 0))
                return Fail("RAC2 v3 VAT metadata is invalid.");

            _bundleVatFrameCount = (int)frameRaw;
            _bundleVatFps = fps;
            _bundleVatWidth = (int)widthRaw;
            _bundleVatRowsPerFrame = (int)rowsRaw;
            _bundleVatHeight = (int)heightRaw;
            _bundleVatLoop = loop;
            _bundleVatHasNormals = hasNormals;
            _bundleVatBounds = new Bounds(center, size);
            return true;
        }

        private bool ParseBundleMaterials(
            int offset, int length, int expectedCount, MeshRenderer renderer,
            bool hasVat, Texture2D vatPosition, Texture2D vatNormal)
        {
            if (length < 8 || ReadU32At(offset) != 1u ||
                ReadU32At(offset + 4) != (uint)expectedCount)
                return Fail("RAC2 v3 material table is unsupported.");

            _nodeMaterialsPending = true;
            if (_nodeMaterialCursor == 0)
            {
                _nodeProfiles = new int[expectedCount];
                _nodeRenderModes = new int[expectedCount];
                _nodeCullModes = new int[expectedCount];
                _nodeCutoffs = new float[expectedCount];
                _nodeBumpScales = new float[expectedCount];
                _nodeShadowStrengths = new float[expectedCount];
                _nodeUnlitValues = new float[expectedCount];
                _nodeLightMinimums = new float[expectedCount];
                _nodeLightMaximums = new float[expectedCount];
                _nodeMonochromes = new float[expectedCount];
                _nodeColors = new Color[expectedCount];
                _nodeTextures = new Texture2D[expectedCount];
                _nodeNormals = new Texture2D[expectedCount];
                _nodeTemplates = new Material[expectedCount];
                _nodeMaterialCursor = offset + 8;
                _nodeMaterialIndex = 0;
                _nodeMaterialStage = 0;
            }
            int cursor = _nodeMaterialCursor;
            int end = offset + length;
            if (_nodeMaterialStage == 0)
            {
            int stop = Mathf.Min(expectedCount, _nodeMaterialIndex + 1);
            for (int materialIndex = _nodeMaterialIndex; materialIndex < stop; materialIndex++)
            {
                if (cursor > end - BundleMaterialRecordHeaderBytes)
                    return Fail("RAC2 v3 material record is truncated.");
                uint recordBytesRaw = ReadU32At(cursor);
                uint profileRaw = ReadU32At(cursor + 4);
                Color color = new Color(
                    ReadF32At(cursor + 8), ReadF32At(cursor + 12),
                    ReadF32At(cursor + 16), ReadF32At(cursor + 20));
                uint materialLengthRaw = ReadU32At(cursor + 24);
                uint textureLengthRaw = ReadU32At(cursor + 28);
                uint normalLengthRaw = ReadU32At(cursor + 32);
                long payload = (long)materialLengthRaw + textureLengthRaw + normalLengthRaw;
                if (recordBytesRaw < BundleMaterialRecordHeaderBytes ||
                    recordBytesRaw != BundleMaterialRecordHeaderBytes + payload ||
                    (long)cursor + recordBytesRaw > end || profileRaw > 1u ||
                    !FiniteColor(color) || materialLengthRaw > int.MaxValue ||
                    textureLengthRaw > int.MaxValue || normalLengthRaw > int.MaxValue)
                    return Fail("RAC2 v3 material header is invalid.");

                int materialOffset = cursor + BundleMaterialRecordHeaderBytes;
                int textureOffset = materialOffset + (int)materialLengthRaw;
                int normalOffset = textureOffset + (int)textureLengthRaw;
                int profile = (int)profileRaw;
                int renderMode = 0;
                int cullMode = 2;
                float cutoff = 0.5f;
                float bumpScale = 1f;
                float shadowStrength = 1f;
                float unlit = 0f;
                float lightMinimum = 0.05f;
                float lightMaximum = 1f;
                float monochrome = 0f;
                bool materialHasNormal = false;

                if (profile == 1)
                {
                    if (materialLengthRaw != MaterialBytes ||
                        ReadU32At(materialOffset) != 1u || ReadU32At(materialOffset + 4) != 1u)
                        return Fail("RAC2 v3 lilToon material profile is unsupported.");
                    uint renderRaw = ReadU32At(materialOffset + 8);
                    uint cullRaw = ReadU32At(materialOffset + 12);
                    uint flags = ReadU32At(materialOffset + 44);
                    if (renderRaw > 1u || cullRaw > 2u || (flags & ~1u) != 0u)
                        return Fail("RAC2 v3 lilToon mode, culling, or flags are unsupported.");
                    renderMode = (int)renderRaw;
                    cullMode = (int)cullRaw;
                    cutoff = ReadF32At(materialOffset + 16);
                    bumpScale = ReadF32At(materialOffset + 20);
                    shadowStrength = ReadF32At(materialOffset + 24);
                    unlit = ReadF32At(materialOffset + 28);
                    lightMinimum = ReadF32At(materialOffset + 32);
                    lightMaximum = ReadF32At(materialOffset + 36);
                    monochrome = ReadF32At(materialOffset + 40);
                    materialHasNormal = (flags & 1u) != 0u;
                    if (!Range(cutoff, -0.001f, 1.001f) || !Range(bumpScale, -10f, 10f) ||
                        !Range(shadowStrength, 0f, 1f) || !Range(unlit, 0f, 1f) ||
                        !Range(lightMinimum, 0f, 1f) || !Range(lightMaximum, 0f, 10f) ||
                        !Range(monochrome, 0f, 1f))
                        return Fail("RAC2 v3 lilToon parameters are invalid.");
                    _bundleSawLilToonMaterial = true;
                }
                else
                {
                    if (materialLengthRaw != 0u || normalLengthRaw != 0u)
                        return Fail("RAC2 v3 generic material has unsupported metadata.");
                    _bundleSawGenericMaterial = true;
                }

                if ((normalLengthRaw != 0u) != materialHasNormal ||
                    normalLengthRaw != 0u &&
                    (!_bundleParsedHasUv0 || !_bundleParsedHasNormals || !_bundleParsedHasTangents))
                    return Fail("RAC2 v3 normal map requires matching UV0, normals, and tangents.");

                Texture2D texture = ParseTexture(textureLengthRaw == 0u ? 0 : textureOffset,
                    (int)textureLengthRaw, false, "NODE TEX0");
                if (textureLengthRaw != 0u && texture == null) return false;
                Texture2D normal = ParseTexture(normalLengthRaw == 0u ? 0 : normalOffset,
                    (int)normalLengthRaw, true, "NODE TEXN");
                if (normalLengthRaw != 0u && normal == null)
                {
                    return false;
                }

                int resourceIndex = _bundleMaterialCursor + materialIndex;
                if (resourceIndex >= _bundleTextures.Length)
                {
                    return Fail("RAC2 v3 material count exceeds SCNE.");
                }
                _bundleTextures[resourceIndex] = texture;
                _bundleNormalTextures[resourceIndex] = normal;
                _nodeProfiles[materialIndex] = profile;
                _nodeRenderModes[materialIndex] = renderMode;
                _nodeCullModes[materialIndex] = cullMode;
                _nodeCutoffs[materialIndex] = cutoff;
                _nodeBumpScales[materialIndex] = bumpScale;
                _nodeShadowStrengths[materialIndex] = shadowStrength;
                _nodeUnlitValues[materialIndex] = unlit;
                _nodeLightMinimums[materialIndex] = lightMinimum;
                _nodeLightMaximums[materialIndex] = lightMaximum;
                _nodeMonochromes[materialIndex] = monochrome;
                _nodeColors[materialIndex] = color;
                _nodeTextures[materialIndex] = texture;
                _nodeNormals[materialIndex] = normal;

                Material template = hasVat
                    ? renderMode == 0 ? VatOpaqueTemplate : VatCutoutTemplate
                    : profile == 1
                        ? renderMode == 0 ? LilToonOpaqueTemplate : LilToonCutoutTemplate
                        : MaterialTemplate;
                if (template == null)
                    return Fail(hasVat
                        ? "RAC2 v3 VAT material templates are unavailable."
                        : profile == 1
                            ? "RAC2 v3 lilToon material templates are unavailable."
                            : "RAC2 v3 generic material template is unavailable.");
                _nodeTemplates[materialIndex] = template;
                cursor += (int)recordBytesRaw;
                _nodeMaterialIndex = materialIndex + 1;
            }
            _nodeMaterialCursor = cursor;
            if (_nodeMaterialIndex < expectedCount) return true;
            if (cursor != end) return Fail("RAC2 v3 material cursor is inconsistent.");

            _nodeMaterialStage = 1;
            return true;
            }
            if (_nodeMaterialStage == 1)
            {
            renderer.sharedMaterials = _nodeTemplates;
            _nodeInstances = renderer.materials;
            if (_nodeInstances == null || _nodeInstances.Length != expectedCount)
                return Fail("RAC2 v3 could not instantiate the material array.");
            for (int index = 0; index < expectedCount; index++)
                _bundleMaterials[_bundleMaterialCursor + index] = _nodeInstances[index];
            _nodeMaterialIndex = 0;
            _nodeMaterialStage = 2;
            return true;
            }
            int applyStop = Mathf.Min(expectedCount, _nodeMaterialIndex + 1);
            for (int materialIndex = _nodeMaterialIndex; materialIndex < applyStop; materialIndex++)
            {
                Material material = _nodeInstances[materialIndex];
                if (material == null) return Fail("RAC2 v3 material instance is missing.");
                material.name = "RAC2 v3 Material " + (_bundleMaterialCursor + materialIndex);
                if (material.HasProperty(BaseColorProperty))
                    material.SetColor(BaseColorProperty, _nodeColors[materialIndex]);
                if (material.HasProperty(MainTextureProperty))
                    material.SetTexture(MainTextureProperty, _nodeTextures[materialIndex]);
                if (_nodeProfiles[materialIndex] == 1)
                {
                    BundleSetFloat(material, "_TransparentMode", _nodeRenderModes[materialIndex]);
                    BundleSetFloat(material, "_Cull", _nodeCullModes[materialIndex]);
                    BundleSetFloat(material, "_Cutoff", _nodeCutoffs[materialIndex]);
                    BundleSetFloat(material, "_BumpScale", _nodeBumpScales[materialIndex]);
                    BundleSetFloat(material, "_ShadowStrength", _nodeShadowStrengths[materialIndex]);
                    BundleSetFloat(material, "_AsUnlit", _nodeUnlitValues[materialIndex]);
                    BundleSetFloat(material, "_LightMinLimit", _nodeLightMinimums[materialIndex]);
                    BundleSetFloat(material, "_LightMaxLimit", _nodeLightMaximums[materialIndex]);
                    BundleSetFloat(material, "_MonochromeLighting", _nodeMonochromes[materialIndex]);
                    BundleSetFloat(material, "_UseBumpMap", _nodeNormals[materialIndex] != null ? 1f : 0f);
                    if (material.HasProperty("_BumpMap"))
                        material.SetTexture("_BumpMap", _nodeNormals[materialIndex]);
                }
                if (hasVat)
                {
                    if (material.HasProperty("_VatPositionTex"))
                        material.SetTexture("_VatPositionTex", vatPosition);
                    if (material.HasProperty("_VatNormalTex"))
                        material.SetTexture("_VatNormalTex", vatNormal);
                    BundleSetFloat(material, "_VatFrameCount", _bundleVatFrameCount);
                    BundleSetFloat(material, "_VatRowsPerFrame", _bundleVatRowsPerFrame);
                    BundleSetFloat(material, "_VatTextureHeight", _bundleVatHeight);
                    BundleSetFloat(material, "_VatFps", _bundleVatFps);
                    BundleSetFloat(material, "_VatSpeed", VatPlaybackSpeed);
                    BundleSetFloat(material, "_VatLoop", _bundleVatLoop ? 1f : 0f);
                    BundleSetFloat(material, "_VatHasNormal", _bundleVatHasNormals ? 1f : 0f);
                    BundleSetFloat(material, "_VatStartTime", Time.timeSinceLevelLoad);
                    if (material.HasProperty("_VatBoundsMin"))
                        material.SetVector("_VatBoundsMin", _bundleVatBounds.min);
                    if (material.HasProperty("_VatBoundsSize"))
                        material.SetVector("_VatBoundsSize", _bundleVatBounds.size);
                }
                _bundleMaterials[_bundleMaterialCursor + materialIndex] = material;
                if (_nodeTextures[materialIndex] != null) LoadedHasTexture = true;
                if (_nodeNormals[materialIndex] != null) LoadedHasNormalMap = true;
                _nodeMaterialIndex = materialIndex + 1;
            }
            if (_nodeMaterialIndex < expectedCount) return true;
            _bundleMaterialCursor += expectedCount;
            ResetMaterialDecode();
            return true;
        }

        private void ResetMaterialDecode()
        {
            _nodeMaterialCursor = 0;
            _nodeMaterialIndex = 0;
            _nodeMaterialStage = 0;
            _nodeMaterialsPending = false;
            _nodeInstances = null;
            _nodeProfiles = null;
            _nodeRenderModes = null;
            _nodeCullModes = null;
            _nodeCutoffs = null;
            _nodeBumpScales = null;
            _nodeShadowStrengths = null;
            _nodeUnlitValues = null;
            _nodeLightMinimums = null;
            _nodeLightMaximums = null;
            _nodeMonochromes = null;
            _nodeColors = null;
            _nodeTextures = null;
            _nodeNormals = null;
            _nodeTemplates = null;
        }

        private bool ParseBundleParticles(
            int offset, int length, int expectedEmitters, out int particleMaximum)
        {
            particleMaximum = 0;
            if (length < 8 || ReadU32At(offset) != 2u ||
                ReadU32At(offset + 4) != (uint)expectedEmitters)
                return Fail("RAC2 v3 PART format is unsupported.");

            int cursor = offset + 8;
            int end = offset + length;
            for (int emitterIndex = 0; emitterIndex < expectedEmitters; emitterIndex++)
            {
                if (cursor > end - BundleParticleRecordHeaderBytes)
                    return Fail("RAC2 v3 particle record is truncated.");
                uint recordBytesRaw = ReadU32At(cursor);
                uint nameLengthRaw = ReadU32At(cursor + 4);
                uint meshLengthRaw = ReadU32At(cursor + 8);
                uint particleLengthRaw = ReadU32At(cursor + 12);
                uint textureLengthRaw = ReadU32At(cursor + 16);
                long payloadLength = (long)nameLengthRaw + meshLengthRaw +
                                     particleLengthRaw + textureLengthRaw;
                if (ReadU32At(cursor + 20) != 0u || ReadU32At(cursor + 24) != 0u ||
                    ReadU32At(cursor + 28) != 0u ||
                    recordBytesRaw < BundleParticleRecordHeaderBytes ||
                    nameLengthRaw > 128u || meshLengthRaw < MeshHeaderBytes ||
                    particleLengthRaw != ParticleBytes || textureLengthRaw > int.MaxValue ||
                    recordBytesRaw != BundleParticleRecordHeaderBytes + payloadLength ||
                    (long)cursor + recordBytesRaw > end)
                    return Fail("RAC2 v3 particle record header is invalid.");

                int payload = cursor + BundleParticleRecordHeaderBytes;
                string emitterName = nameLengthRaw == 0u
                    ? "Particle" : Encoding.UTF8.GetString(_data, payload, (int)nameLengthRaw);
                int meshOffset = payload + (int)nameLengthRaw;
                int particleOffset = meshOffset + (int)meshLengthRaw;
                int textureOffset = particleOffset + (int)particleLengthRaw;
                if (!ParseBundleParticleMesh(meshOffset, (int)meshLengthRaw, emitterName))
                    return false;
                Mesh particleMesh = _bundleParsedMesh;

                uint flags = ReadU32At(particleOffset + 4);
                uint shaderRaw = ReadU32At(particleOffset + 8);
                uint maximumRaw = ReadU32At(particleOffset + 12);
                float duration = ReadF32At(particleOffset + 16);
                float lifetime = ReadF32At(particleOffset + 20);
                float emissionRate = ReadF32At(particleOffset + 24);
                float speedMinimum = ReadF32At(particleOffset + 28);
                float speedMaximum = ReadF32At(particleOffset + 32);
                float sizeMinimum = ReadF32At(particleOffset + 36);
                float sizeMaximum = ReadF32At(particleOffset + 40);
                float gravity = ReadF32At(particleOffset + 44);
                float angularSpeed = ReadF32At(particleOffset + 48);
                Vector3 origin = new Vector3(
                    ReadF32At(particleOffset + 52), ReadF32At(particleOffset + 56),
                    ReadF32At(particleOffset + 60));
                Vector3 direction = new Vector3(
                    ReadF32At(particleOffset + 64), ReadF32At(particleOffset + 68),
                    ReadF32At(particleOffset + 72));
                uint shapeRaw = ReadU32At(particleOffset + 76);
                float shapeRadius = ReadF32At(particleOffset + 80);
                float shapeAngle = ReadF32At(particleOffset + 84);
                Vector3 shapeScale = new Vector3(
                    ReadF32At(particleOffset + 88), ReadF32At(particleOffset + 92),
                    ReadF32At(particleOffset + 96));
                Color startColor = new Color(
                    ReadF32At(particleOffset + 100), ReadF32At(particleOffset + 104),
                    ReadF32At(particleOffset + 108), ReadF32At(particleOffset + 112));
                Color endColor = new Color(
                    ReadF32At(particleOffset + 116), ReadF32At(particleOffset + 120),
                    ReadF32At(particleOffset + 124), ReadF32At(particleOffset + 128));
                uint columnsRaw = ReadU32At(particleOffset + 132);
                uint rowsRaw = ReadU32At(particleOffset + 136);
                if (ReadU32At(particleOffset) != 1u || (flags & ~7u) != 0u ||
                    shaderRaw > 1u || maximumRaw < 1u || maximumRaw > 32u ||
                    !Range(duration, 0.001f, 120f) || !Range(lifetime, 0.02f, 30f) ||
                    !Range(emissionRate, 0f, 60f) || !Range(speedMinimum, 0f, 20f) ||
                    !Range(speedMaximum, speedMinimum, 20f) ||
                    !Range(sizeMinimum, 0.0001f, 10f) ||
                    !Range(sizeMaximum, sizeMinimum, 10f) ||
                    !Range(gravity, -20f, 20f) || !Range(angularSpeed, -1440f, 1440f) ||
                    !FiniteVector(origin, 1000000f) || !FiniteVector(direction, 16f) ||
                    direction.sqrMagnitude < 0.000001f || shapeRaw > 3u ||
                    !Range(shapeRadius, 0f, 10f) || !Range(shapeAngle, 0f, 89f) ||
                    !FiniteVector(shapeScale, 10f) || shapeScale.x < 0f ||
                    shapeScale.y < 0f || shapeScale.z < 0f ||
                    !FiniteColor(startColor) || !FiniteColor(endColor) ||
                    columnsRaw < 1u || columnsRaw > 16u ||
                    rowsRaw < 1u || rowsRaw > 16u || columnsRaw * rowsRaw > 256u)
                {
                    Destroy(particleMesh);
                    return Fail("RAC2 v3 particle metadata is invalid.");
                }

                Rac2ParticlePlayer player = BundleParticlePlayer(emitterIndex);
                if (player == null || player.PoolObjects == null ||
                    player.PoolObjects.Length < (int)maximumRaw ||
                    (shaderRaw == 0u ? player.AlphaTemplate : player.AdditiveTemplate) == null)
                {
                    Destroy(particleMesh);
                    return Fail("RAC2 v3 particle pool or material template is invalid.");
                }

                Texture2D particleTexture = ParseTexture(
                    textureLengthRaw == 0u ? 0 : textureOffset,
                    (int)textureLengthRaw, false, "PART TEX");
                if (textureLengthRaw != 0u && particleTexture == null)
                {
                    Destroy(particleMesh);
                    return false;
                }
                _bundleParticleMeshes[emitterIndex] = particleMesh;
                _bundleParticleTextures[emitterIndex] = particleTexture;

                player.ClearParticles();
                player.ParticleMesh = particleMesh;
                player.ParticleTexture = particleTexture;
                player.Loop = (flags & 1u) != 0u;
                player.Billboard = (flags & 2u) != 0u;
                player.ShaderProfile = (int)shaderRaw;
                player.MaximumParticles = (int)maximumRaw;
                player.Duration = duration;
                player.Lifetime = lifetime;
                player.EmissionRate = emissionRate;
                player.SpeedMinimum = speedMinimum;
                player.SpeedMaximum = speedMaximum;
                player.SizeMinimum = sizeMinimum;
                player.SizeMaximum = sizeMaximum;
                player.Gravity = gravity;
                player.AngularSpeed = angularSpeed;
                player.Origin = origin;
                player.Direction = direction;
                player.Shape = (int)shapeRaw;
                player.ShapeRadius = shapeRadius;
                player.ShapeAngle = shapeAngle;
                player.ShapeScale = shapeScale;
                player.StartColor = startColor;
                player.EndColor = endColor;
                player.FlipbookColumns = (int)columnsRaw;
                player.FlipbookRows = (int)rowsRaw;
                ConfigureParticleBooth(player);
                player.ApplyConfiguration();
                if (!player.IsConfigured)
                    return Fail("RAC2 v3 particle emitter could not be configured.");

                IncludeBundleParticleAnchorIfEmpty(origin);
                particleMaximum += (int)maximumRaw;
                cursor += (int)recordBytesRaw;
            }
            if (cursor != end) return Fail("RAC2 v3 particle cursor is inconsistent.");
            return true;
        }

        private bool ParseBundleParticleMesh(int offset, int length, string label)
        {
            _bundleParsedMesh = null;
            if (length < MeshHeaderBytes) return Fail("RAC2 v3 particle mesh is truncated.");
            uint vertexRaw = ReadU32At(offset);
            uint indexRaw = ReadU32At(offset + 4);
            uint attributes = ReadU32At(offset + 8);
            if (vertexRaw == 0u || vertexRaw > StandardMaxVertices ||
                indexRaw == 0u || indexRaw > StandardMaxIndices || (int)indexRaw % 3 != 0 ||
                (attributes & ~KnownAttributes) != 0u || ReadU32At(offset + 12) != 32u)
                return Fail("RAC2 v3 particle mesh counts are invalid.");

            int vertexCount = (int)vertexRaw;
            int indexCount = (int)indexRaw;
            bool hasNormals = (attributes & AttrNormals) != 0u;
            bool hasUv0 = (attributes & AttrUv0) != 0u;
            bool hasUv1 = (attributes & AttrUv1) != 0u;
            bool hasColors = (attributes & AttrColors) != 0u;
            bool hasTangents = (attributes & AttrTangents) != 0u;
            long expectedLength = MeshHeaderBytes + (long)vertexCount * 12L +
                                  (hasNormals ? (long)vertexCount * 12L : 0L) +
                                  (hasUv0 ? (long)vertexCount * 8L : 0L) +
                                  (hasUv1 ? (long)vertexCount * 8L : 0L) +
                                  (hasColors ? (long)vertexCount * 4L : 0L) +
                                  (hasTangents ? (long)vertexCount * 16L : 0L) +
                                  (long)indexCount * 4L;
            if (expectedLength != length)
                return Fail("RAC2 v3 particle mesh byte length is inconsistent.");

            _cursor = offset + MeshHeaderBytes;
            Vector3[] vertices = new Vector3[vertexCount];
            Vector3 min = Vector3.zero;
            Vector3 max = Vector3.zero;
            for (int index = 0; index < vertexCount; index++)
            {
                Vector3 value = new Vector3(ReadF32(), ReadF32(), ReadF32());
                if (!FiniteVector(value, 1000000f))
                    return Fail("RAC2 v3 particle mesh position is invalid.");
                vertices[index] = value;
                if (index == 0) { min = value; max = value; }
                else { min = Vector3.Min(min, value); max = Vector3.Max(max, value); }
            }
            Vector3[] normals = new Vector3[0];
            if (hasNormals)
            {
                normals = new Vector3[vertexCount];
                for (int index = 0; index < vertexCount; index++)
                {
                    Vector3 value = new Vector3(ReadF32(), ReadF32(), ReadF32());
                    if (!FiniteVector(value, 16f)) return Fail("RAC2 v3 particle normal is invalid.");
                    normals[index] = value;
                }
            }
            Vector2[] uv0 = new Vector2[0];
            if (hasUv0)
            {
                uv0 = new Vector2[vertexCount];
                for (int index = 0; index < vertexCount; index++)
                {
                    Vector2 value = new Vector2(ReadF32(), ReadF32());
                    if (!Finite(value.x, 1000000f) || !Finite(value.y, 1000000f))
                        return Fail("RAC2 v3 particle UV0 is invalid.");
                    uv0[index] = value;
                }
            }
            Vector2[] uv1 = new Vector2[0];
            if (hasUv1)
            {
                uv1 = new Vector2[vertexCount];
                for (int index = 0; index < vertexCount; index++)
                {
                    Vector2 value = new Vector2(ReadF32(), ReadF32());
                    if (!Finite(value.x, 1000000f) || !Finite(value.y, 1000000f))
                        return Fail("RAC2 v3 particle UV1 is invalid.");
                    uv1[index] = value;
                }
            }
            Color32[] colors = new Color32[0];
            if (hasColors)
            {
                colors = new Color32[vertexCount];
                for (int index = 0; index < vertexCount; index++)
                {
                    colors[index] = new Color32(
                        _data[_cursor], _data[_cursor + 1], _data[_cursor + 2], _data[_cursor + 3]);
                    _cursor += 4;
                }
            }
            Vector4[] tangents = new Vector4[0];
            if (hasTangents)
            {
                tangents = new Vector4[vertexCount];
                for (int index = 0; index < vertexCount; index++)
                {
                    Vector4 value = new Vector4(ReadF32(), ReadF32(), ReadF32(), ReadF32());
                    if (!Finite(value.x, 16f) || !Finite(value.y, 16f) ||
                        !Finite(value.z, 16f) || !Finite(value.w, 1.001f))
                        return Fail("RAC2 v3 particle tangent is invalid.");
                    tangents[index] = value;
                }
            }
            int[] triangles = new int[indexCount];
            for (int index = 0; index < indexCount; index++)
            {
                uint value = ReadU32();
                if (value >= vertexRaw) return Fail("RAC2 v3 particle index is invalid.");
                triangles[index] = (int)value;
            }
            if (_cursor != offset + length)
                return Fail("RAC2 v3 particle mesh cursor is inconsistent.");

            Mesh mesh = new Mesh();
            mesh.name = "RAC2 v3 " + label;
            mesh.indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = vertices;
            if (hasNormals) mesh.normals = normals;
            if (hasUv0) mesh.uv = uv0;
            if (hasUv1) mesh.uv2 = uv1;
            if (hasColors) mesh.colors32 = colors;
            if (hasTangents) mesh.tangents = tangents;
            mesh.triangles = triangles;
            if (!hasNormals) mesh.RecalculateNormals();
            mesh.bounds = new Bounds((min + max) * 0.5f, max - min);
            _bundleParsedMesh = mesh;
            return true;
        }

        private bool ParseBundleProduct(
            int offset, int length, out bool hasProduct, out string productName,
            out string creatorName, out string productUrl, out string avatarBlueprintId,
            out bool trialEnabled)
        {
            hasProduct = offset != 0;
            productName = "";
            creatorName = "";
            productUrl = "";
            avatarBlueprintId = "";
            trialEnabled = false;
            if (!hasProduct) return true;
            if (length < ProductHeaderBytes || ReadU32At(offset) != 1u)
                return Fail("RAC2 v3 PROD format is unsupported.");

            uint flags = ReadU32At(offset + 4);
            uint nameLengthRaw = ReadU32At(offset + 8);
            uint creatorLengthRaw = ReadU32At(offset + 12);
            uint urlLengthRaw = ReadU32At(offset + 16);
            uint avatarLengthRaw = ReadU32At(offset + 20);
            long payloadLength = (long)nameLengthRaw + creatorLengthRaw + urlLengthRaw + avatarLengthRaw;
            if ((flags & ~1u) != 0u || nameLengthRaw > 128u || creatorLengthRaw > 128u ||
                urlLengthRaw > 1024u || avatarLengthRaw > 41u || payloadLength == 0L ||
                payloadLength != length - ProductHeaderBytes)
                return Fail("RAC2 v3 PROD metadata is invalid.");

            int cursor = offset + ProductHeaderBytes;
            productName = Encoding.UTF8.GetString(_data, cursor, (int)nameLengthRaw);
            cursor += (int)nameLengthRaw;
            creatorName = Encoding.UTF8.GetString(_data, cursor, (int)creatorLengthRaw);
            cursor += (int)creatorLengthRaw;
            productUrl = Encoding.UTF8.GetString(_data, cursor, (int)urlLengthRaw);
            cursor += (int)urlLengthRaw;
            avatarBlueprintId = Encoding.UTF8.GetString(_data, cursor, (int)avatarLengthRaw);
            trialEnabled = (flags & 1u) != 0u;
            bool validUrl = string.IsNullOrEmpty(productUrl) ||
                            productUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            bool validAvatar = string.IsNullOrEmpty(avatarBlueprintId) ||
                               avatarBlueprintId.Length == 41 && avatarBlueprintId.StartsWith("avtr_");
            if (!validUrl || !validAvatar || trialEnabled && avatarBlueprintId.Length != 41)
                return Fail("RAC2 v3 PROD URL or Avatar Blueprint ID is invalid.");
            return true;
        }

        private void IncludeBundleParticleAnchorIfEmpty(Vector3 origin)
        {
            if (!_bundleHasActualBounds) IncludeBundlePoint(origin);
        }

        private void IncludeBundleBounds(
            Vector3 min, Vector3 max, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = new Vector3(
                    (corner & 1) == 0 ? min.x : max.x,
                    (corner & 2) == 0 ? min.y : max.y,
                    (corner & 4) == 0 ? min.z : max.z);
                IncludeBundlePoint(position + rotation * Vector3.Scale(local, scale));
            }
        }

        private void IncludeBundlePoint(Vector3 value)
        {
            if (!_bundleHasActualBounds)
            {
                _bundleActualMin = value;
                _bundleActualMax = value;
                _bundleHasActualBounds = true;
            }
            else
            {
                _bundleActualMin = Vector3.Min(_bundleActualMin, value);
                _bundleActualMax = Vector3.Max(_bundleActualMax, value);
            }
        }

        private bool BundleRendererPoolAvailable(int count)
        {
            if (count == 0) return true;
            if (SceneMeshFilters != null && SceneRenderers != null &&
                SceneMeshFilters.Length >= count && SceneRenderers.Length >= count)
                return true;
            return count == 1 && TargetMeshFilter != null && TargetRenderer != null;
        }

        private bool BundleParticlePoolAvailable(int count)
        {
            if (count == 0) return true;
            if (ParticlePlayers != null && ParticlePlayers.Length >= count) return true;
            return count == 1 && ParticlePlayer != null;
        }

        private MeshFilter BundleMeshFilter(int index)
        {
            if (SceneMeshFilters != null && index < SceneMeshFilters.Length)
                return SceneMeshFilters[index];
            return index == 0 ? TargetMeshFilter : null;
        }

        private MeshRenderer BundleMeshRenderer(int index)
        {
            if (SceneRenderers != null && index < SceneRenderers.Length)
                return SceneRenderers[index];
            return index == 0 ? TargetRenderer : null;
        }

        private GameObject BundleSceneObject(int index)
        {
            if (SceneObjects != null && index < SceneObjects.Length)
                return SceneObjects[index];
            return null;
        }

        private Transform BundleSceneTransform(int index, MeshFilter filter)
        {
            GameObject item = BundleSceneObject(index);
            if (item != null) return item.transform;
            return filter == null ? null : filter.transform;
        }

        private Rac2ParticlePlayer BundleParticlePlayer(int index)
        {
            if (ParticlePlayers != null && index < ParticlePlayers.Length)
                return ParticlePlayers[index];
            return index == 0 ? ParticlePlayer : null;
        }

        private int BundleSectionRank(uint type)
        {
            if (type == MetaType) return 0;
            if (type == BundleSceneType) return 1;
            if (type == BundleNodeType) return 2;
            if (type == PartType) return 3;
            if (type == IntrType) return 4;
            if (type == ProdType) return 5;
            return -1;
        }

        private void BundleSetFloat(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private bool BundleFail(string message)
        {
            string resolved = string.IsNullOrEmpty(message) ? "Invalid RAC2 v3." : message;
            ClearBundleResources();
            return Fail(resolved);
        }

        private void ClearBundleResources()
        {
            ResetMeshDecode();
            _nodeRestorePhase = 0;
            ResetMaterialDecode();
            if (SceneRenderers != null)
            {
                for (int index = 0; index < SceneRenderers.Length; index++)
                {
                    MeshRenderer renderer = SceneRenderers[index];
                    if (renderer != null)
                    {
                        renderer.enabled = false;
                        renderer.sharedMaterials = new Material[0];
                    }
                }
            }
            if (SceneMeshFilters != null)
            {
                for (int index = 0; index < SceneMeshFilters.Length; index++)
                    if (SceneMeshFilters[index] != null) SceneMeshFilters[index].sharedMesh = null;
            }
            if (SceneObjects != null)
            {
                for (int index = 0; index < SceneObjects.Length; index++)
                    if (SceneObjects[index] != null) SceneObjects[index].SetActive(false);
            }
            if (ParticlePlayers != null)
            {
                for (int index = 0; index < ParticlePlayers.Length; index++)
                    if (ParticlePlayers[index] != null) ParticlePlayers[index].ClearParticles();
            }

            DestroyBundleMeshes(_bundleMeshes);
            DestroyBundleMaterials(_bundleMaterials);
            // Full NODE textures are owned once by the cache; references borrow them.
            DestroyBundleTextures(_sharedTextureCache);
            _sharedTextureCache = null;
            _sharedTextureLinear = null;
            _sharedTextureCount = 0;
            DestroyBundleTextures(_bundleVatPositionTextures);
            DestroyBundleTextures(_bundleVatNormalTextures);
            DestroyBundleMeshes(_bundleParticleMeshes);
            DestroyBundleTextures(_bundleParticleTextures);
            _bundleMeshes = null;
            _bundleMaterials = null;
            _bundleTextures = null;
            _bundleNormalTextures = null;
            _bundleVatPositionTextures = null;
            _bundleVatNormalTextures = null;
            _bundleParticleMeshes = null;
            _bundleParticleTextures = null;
            _bundleParsedMesh = null;
            _bundleMaterialCursor = 0;
            _bundleHasActualBounds = false;
        }

        private void DestroyBundleMeshes(Mesh[] values)
        {
            if (values == null) return;
            for (int index = 0; index < values.Length; index++)
                if (values[index] != null) Destroy(values[index]);
        }

        private void DestroyBundleMaterials(Material[] values)
        {
            if (values == null) return;
            for (int index = 0; index < values.Length; index++)
                if (values[index] != null) Destroy(values[index]);
        }

        private void DestroyBundleTextures(Texture2D[] values)
        {
            if (values == null) return;
            for (int index = 0; index < values.Length; index++)
                if (values[index] != null) Destroy(values[index]);
        }
    }
}
