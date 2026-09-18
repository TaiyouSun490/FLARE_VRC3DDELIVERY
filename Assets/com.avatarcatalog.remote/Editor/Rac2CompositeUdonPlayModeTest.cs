using System;
using System.IO;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

namespace AvatarCatalog.Remote
{
    /// <summary>
    /// Editor-only regression test that sends a compressed composite RAC2 payload
    /// through the real compiled UdonBehaviour while Play Mode is running.
    /// </summary>
    [InitializeOnLoad]
    public static class Rac2CompositeUdonPlayModeTest
    {
        private const string LogPrefix = "[RAC2 Composite Udon VM]";
        private const string RequestPath = "Library/Rac2CompositeUdonPlayModeTest.run";
        private const string ResultPath = "Library/Rac2CompositeUdonPlayModeTest.result";
        private const string PayloadPath = "Library/Rac2CompositeUdonPlayModeTest.rac2";
        private const string PrefabPath =
            "Assets/NightSlotMall/Prefabs/RAC-3D-ImagePad.prefab";
        private const string ProvidedCompositePath =
            "Assets/Textures/Maria WProp J J OngXXXX.rac2";
        private const string SessionKey =
            "AvatarCatalog.Rac2CompositeUdonPlayModeTest.State.v1";

        private const string StateWaitingForPlay = "waiting-for-play";
        private const string StateWarmup = "warmup";
        private const string StateWaitingForResult = "waiting-for-result";
        private const string StateWaitingForSakuraResult = "waiting-for-sakura-result";
        private const string StateWaitingForProvidedResult = "waiting-for-provided-result";
        private const string StateReturningToEdit = "returning-to-edit";

        private static GameObject _instance;
        private static Rac2RuntimeLoader _loaderProxy;
        private static UdonBehaviour _loaderUdon;
        private static int _warmupFrames;
        private static double _deadline;
        private static double _providedStartedAt;
        private static Vector3 _expectedLocalPosition;
        private static Quaternion _expectedLocalRotation;

        static Rac2CompositeUdonPlayModeTest()
        {
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        [MenuItem("Tools/Avatar Catalog/Developer/Tests/Run Complete RAC2 Udon VM")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException(
                    "Exit Play Mode before starting the RAC2 Udon VM test.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException(
                    "Wait for Unity compilation/import to finish.");

            DeleteIfPresent(ResultPath);
            DeleteIfPresent(PayloadPath);
            try
            {
                BuildCompressedPayload(Path.GetFullPath(PayloadPath));
                SessionState.SetString(SessionKey, StateWaitingForPlay);
                Debug.Log(LogPrefix +
                          " compressed v3 payload built; entering Play Mode.");
                EditorApplication.isPlaying = true;
            }
            catch (Exception exception)
            {
                WriteResult(false, exception.ToString());
                CleanupSession();
                Debug.LogException(exception);
                throw;
            }
        }

        private static void OnEditorUpdate()
        {
            string state = SessionState.GetString(SessionKey, string.Empty);
            if (string.IsNullOrEmpty(state))
            {
                ConsumeRequest();
                return;
            }

            try
            {
                if (!EditorApplication.isPlaying)
                {
                    if (state == StateReturningToEdit)
                    {
                        CleanupSession();
                        return;
                    }

                    if (state == StateWaitingForPlay &&
                        !EditorApplication.isPlayingOrWillChangePlaymode &&
                        !EditorApplication.isCompiling)
                    {
                        EditorApplication.isPlaying = true;
                    }

                    return;
                }

                if (state == StateWaitingForPlay)
                {
                    InstantiateHarness();
                    SessionState.SetString(SessionKey, StateWarmup);
                    _warmupFrames = 0;
                    return;
                }

                if (state == StateWarmup)
                {
                    _warmupFrames++;
                    if (_warmupFrames < 5) return;
                    FirePayload();
                    SessionState.SetString(SessionKey, StateWaitingForResult);
                    _deadline = EditorApplication.timeSinceStartup + 60d;
                    return;
                }

                if (state == StateWaitingForResult ||
                    state == StateWaitingForSakuraResult ||
                    state == StateWaitingForProvidedResult)
                    MonitorResult();
            }
            catch (Exception exception)
            {
                Finish(false, exception.ToString());
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            string state = SessionState.GetString(SessionKey, string.Empty);
            if (change == PlayModeStateChange.ExitingPlayMode &&
                !string.IsNullOrEmpty(state) &&
                state != StateReturningToEdit)
            {
                WriteResult(false,
                    "Play Mode exited before the RAC2 Udon VM test completed.");
                SessionState.SetString(SessionKey, StateReturningToEdit);
            }
            else if (change == PlayModeStateChange.EnteredEditMode &&
                     state == StateReturningToEdit)
            {
                CleanupSession();
            }
        }

        private static void ConsumeRequest()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling ||
                EditorApplication.isUpdating)
                return;

            string request = Path.GetFullPath(RequestPath);
            if (!File.Exists(request)) return;
            File.Delete(request);
            try
            {
                Run();
            }
            catch (Exception exception)
            {
                WriteResult(false, exception.ToString());
            }
        }

        private static void InstantiateHarness()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null)
                throw new InvalidOperationException(
                    "Complete RAC2 ImagePad prefab is missing: " + PrefabPath);

            _instance = UnityEngine.Object.Instantiate(prefab);
            _instance.name = "RAC2 Composite Udon VM Test";
            _loaderProxy =
                _instance.GetComponentInChildren<Rac2RuntimeLoader>(true);
            if (_loaderProxy == null)
                throw new InvalidOperationException(
                    "Rac2RuntimeLoader is missing from the complete ImagePad.");

            _loaderUdon =
                UdonSharpEditorUtility.GetBackingUdonBehaviour(_loaderProxy);
            if (_loaderUdon == null)
                throw new InvalidOperationException(
                    "The RAC2 loader has no backing UdonBehaviour.");
            _loaderUdon.InitializeUdonContent();
            bool adaptiveLoading;
            Assert(_loaderUdon.TryGetProgramVariable(
                       "AdaptiveLoadingEnabled", out adaptiveLoading) &&
                   adaptiveLoading,
                "adaptive loading enabled by default");
            int fixedLoadingPerformance;
            Assert(_loaderUdon.TryGetProgramVariable(
                       "FixedLoadingPerformanceMode", out fixedLoadingPerformance) &&
                   fixedLoadingPerformance ==
                       Rac2RuntimeLoader.LoadPerformanceBalanced,
                "default fixed debug loading profile");

            MeshFilter[] sceneFilters = _loaderProxy.SceneMeshFilters;
            MeshRenderer[] sceneRenderers = _loaderProxy.SceneRenderers;
            Rac2ParticlePlayer[] particles = _loaderProxy.ParticlePlayers;
            Assert(sceneFilters != null && sceneFilters.Length >= 16,
                "16-renderer proxy pool");
            Assert(sceneRenderers != null && sceneRenderers.Length >= 16,
                "16-renderer material pool");
            Assert(particles != null && particles.Length >= 4,
                "4-emitter proxy pool");

            for (int index = 0; index < particles.Length; index++)
            {
                Rac2ParticlePlayer particle = particles[index];
                if (particle == null) continue;
                UdonBehaviour particleUdon =
                    UdonSharpEditorUtility.GetBackingUdonBehaviour(particle);
                if (particleUdon == null)
                    throw new InvalidOperationException(
                        "Particle pool " + index + " has no backing UdonBehaviour.");
                particleUdon.InitializeUdonContent();
            }

            _expectedLocalPosition = _loaderProxy.InitialLocalPosition;
            _expectedLocalRotation =
                Quaternion.Euler(_loaderProxy.InitialLocalEulerAngles);
            _loaderProxy.transform.localPosition =
                _expectedLocalPosition + new Vector3(0.35f, 0.2f, -0.15f);
            _loaderProxy.transform.localRotation =
                Quaternion.Euler(17f, 29f, 11f) * _expectedLocalRotation;
        }

        private static void FirePayload()
        {
            byte[] payload = File.ReadAllBytes(Path.GetFullPath(PayloadPath));
            if (payload.Length == 0)
                throw new InvalidOperationException("The RAC2 payload is empty.");

            _loaderUdon.SetProgramVariable("EditorTestPayload", payload);
            _loaderUdon.SendCustomEvent("RunEditorTestPayload");

            int status;
            if (!_loaderUdon.TryGetProgramVariable("Status", out status))
                throw new InvalidOperationException(
                    "Compiled RAC2 Udon has no Status variable.");
            if (status == Rac2RuntimeLoader.StatusError)
                throw new InvalidOperationException(
                    "RAC2 failed immediately: " + ReadString("LastError"));

            Debug.Log(LogPrefix +
                      " sent RunEditorTestPayload to the compiled Udon VM.");
        }

        private static void FireSakuraPayload()
        {
            string path = Path.GetFullPath(
                "Assets/Textures/Sakura Emitter.rac2");
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    "Sakura RAC2 test asset is missing.");
            byte[] payload = File.ReadAllBytes(path);
            _loaderUdon.SetProgramVariable("EditorTestPayload", payload);
            _loaderUdon.SendCustomEvent("RunEditorTestPayload");
            int status;
            if (!_loaderUdon.TryGetProgramVariable("Status", out status) ||
                status == Rac2RuntimeLoader.StatusError)
                throw new InvalidOperationException(
                    "Sakura RAC2 failed immediately: " + ReadString("LastError"));
            Debug.Log(LogPrefix + " sent the real Sakura v2 payload.");
        }

        private static void FireProvidedCompositePayload()
        {
            // Keep the generated and Sakura payloads on the adaptive path, then
            // use the fixed fast budget for the 8 MB Maria regression timeout.
            _loaderUdon.SetProgramVariable("AdaptiveLoadingEnabled", false);
            _loaderUdon.SetProgramVariable(
                "FixedLoadingPerformanceMode",
                Rac2RuntimeLoader.LoadPerformanceFast);
            string path = Path.GetFullPath(ProvidedCompositePath);
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    "Provided composite RAC2 is missing: " +
                    ProvidedCompositePath);
            byte[] payload = File.ReadAllBytes(path);
            _providedStartedAt = EditorApplication.timeSinceStartup;
            _loaderUdon.SetProgramVariable("EditorTestPayload", payload);
            _loaderUdon.SendCustomEvent("RunEditorTestPayload");
            int status;
            if (!_loaderUdon.TryGetProgramVariable("Status", out status) ||
                status == Rac2RuntimeLoader.StatusError)
                throw new InvalidOperationException(
                    "Provided composite RAC2 failed immediately: " +
                    ReadString("LastError"));
            Debug.Log(LogPrefix +
                      " sent the provided Maria composite v3 payload.");
        }

        private static void MonitorResult()
        {
            int status;
            if (!_loaderUdon.TryGetProgramVariable("Status", out status))
                throw new InvalidOperationException(
                    "The live RAC2 Udon Status is unavailable.");

            if (status == Rac2RuntimeLoader.StatusReady)
            {
                string state = SessionState.GetString(SessionKey, string.Empty);
                if (state == StateWaitingForResult)
                {
                    ValidateLiveResult();
                    FireSakuraPayload();
                    SessionState.SetString(
                        SessionKey, StateWaitingForSakuraResult);
                    _warmupFrames = 0;
                    _deadline = EditorApplication.timeSinceStartup + 60d;
                    return;
                }
                _warmupFrames++;
                if (_warmupFrames < 10) return;
                if (state == StateWaitingForSakuraResult)
                {
                    ValidateSakuraLiveResult();
                    FireProvidedCompositePayload();
                    SessionState.SetString(
                        SessionKey, StateWaitingForProvidedResult);
                    _warmupFrames = 0;
                    _deadline = EditorApplication.timeSinceStartup + 180d;
                    return;
                }
                ValidateProvidedCompositeLiveResult();
                Finish(true,
                    "PASS - generated composite v3, real Sakura v2, and " +
                    "provided Maria composite v3 parsed by Udon VM; " +
                    "Maria VAT, particle, renderer, and load reset are valid. " +
                    "Maria load: " + Math.Round(
                        EditorApplication.timeSinceStartup - _providedStartedAt, 1) + "s.");
                return;
            }

            if (status == Rac2RuntimeLoader.StatusError)
            {
                throw new InvalidOperationException(
                    "Compiled Udon rejected RAC2: " + ReadString("LastError") +
                    " (" + ReadString("StatusMessage") + ")");
            }

            if (EditorApplication.timeSinceStartup > _deadline)
            {
                string state = SessionState.GetString(SessionKey, string.Empty);
                int timeout = state == StateWaitingForProvidedResult ? 180 : 60;
                throw new TimeoutException(
                    "RAC2 Udon VM did not finish within " + timeout +
                    " seconds. Status=" + status +
                    ", message=" + ReadString("StatusMessage"));
            }
        }

        private static void ValidateLiveResult()
        {
            Assert(ReadInt("LoadedRenderNodeCount") == 2,
                "runtime renderer count");
            Assert(ReadInt("RestoredRenderNodeCount") == 2,
                "incremental renderer restoration count");
            Assert(ReadInt("LoadedMaterialCount") == 3,
                "runtime material count");
            Assert(ReadInt("LoadedParticleEmitterCount") == 1,
                "runtime particle count");
            Assert(ReadBool("LoadedHasVat"), "runtime VAT flag");
            Assert(ReadBool("LoadedHasParticles"), "runtime particle flag");
            Assert(ReadBool("LoadedWasCompressed"), "compressed expansion path");
            Assert(ReadBool("LoadedIsPortable"), "portable metadata");
            Assert(ReadBool("LoadedHasProduct"), "product metadata");
            Assert(ReadString("LoadedProductName") == "Udon Composite Test",
                "product name");
            Assert(ReadString("LoadedCreatorName") == "RAC2",
                "creator name");
            Assert(ReadInt("LoadedVatFrameCount") == 16,
                "VAT frame count");
            float baselineFps;
            bool hasBaseline = _loaderUdon.TryGetProgramVariable(
                "AdaptiveBaselineFps", out baselineFps);
            Debug.Log(LogPrefix + " adaptive baseline=" + baselineFps +
                      " FPS, budget=" + ReadInt("CurrentExpansionTokenBudget") + ".");
            Assert(hasBaseline && baselineFps > 0f && baselineFps <= 240f,
                "adaptive baseline FPS (" + baselineFps + ")");
            int adaptiveBudget = ReadInt("CurrentExpansionTokenBudget");
            Assert(adaptiveBudget >= 1024 && adaptiveBudget <= 32768,
                "adaptive expansion budget");

            MeshFilter[] filters;
            MeshRenderer[] renderers;
            if (!_loaderUdon.TryGetProgramVariable("SceneMeshFilters", out filters) ||
                filters == null || filters.Length < 2)
                throw new InvalidOperationException(
                    "Live Udon renderer MeshFilter pool is unavailable.");
            if (!_loaderUdon.TryGetProgramVariable("SceneRenderers", out renderers) ||
                renderers == null || renderers.Length < 2)
                throw new InvalidOperationException(
                    "Live Udon renderer material pool is unavailable.");

            Assert(filters[0] != null && filters[0].sharedMesh != null,
                "first runtime mesh");
            Assert(filters[1] != null && filters[1].sharedMesh != null,
                "second runtime mesh");
            Assert(filters[0].sharedMesh.subMeshCount == 2,
                "multi-submesh reconstruction");
            Assert(renderers[0] != null &&
                   renderers[0].sharedMaterials.Length == 2,
                "multi-material reconstruction");

            Rac2ParticlePlayer particleProxy =
                _loaderProxy.ParticlePlayers != null &&
                _loaderProxy.ParticlePlayers.Length > 0
                    ? _loaderProxy.ParticlePlayers[0]
                    : null;
            Assert(particleProxy != null, "particle player proxy");
            UdonBehaviour particleUdon =
                UdonSharpEditorUtility.GetBackingUdonBehaviour(particleProxy);
            Assert(particleUdon != null, "particle backing UdonBehaviour");

            bool configured;
            Mesh particleMesh;
            Assert(particleUdon.TryGetProgramVariable(
                       "IsConfigured", out configured) && configured,
                "particle Udon configuration");
            Assert(particleUdon.TryGetProgramVariable(
                       "ParticleMesh", out particleMesh) &&
                   particleMesh != null,
                "particle Udon mesh");
            Assert(particleMesh != filters[0].sharedMesh,
                "particle mesh is independent from render mesh");

            Assert(Vector3.Distance(
                       _loaderProxy.transform.localPosition,
                       _expectedLocalPosition) < 0.0001f,
                "local position reset on load");
            Assert(Quaternion.Angle(
                       _loaderProxy.transform.localRotation,
                       _expectedLocalRotation) < 0.01f,
                "local rotation reset on load");
        }

        private static void ValidateSakuraLiveResult()
        {
            Assert(ReadBool("LoadedHasParticles"),
                "Sakura runtime particle flag");
            Rac2ParticlePlayer particleProxy = _loaderProxy.ParticlePlayer;
            Assert(particleProxy != null, "Sakura particle player proxy");
            UdonBehaviour particleUdon =
                UdonSharpEditorUtility.GetBackingUdonBehaviour(particleProxy);
            Assert(particleUdon != null, "Sakura particle backing UdonBehaviour");
            bool configured;
            int activeCount;
            Assert(particleUdon.TryGetProgramVariable(
                       "IsConfigured", out configured) && configured,
                "Sakura particle configuration");
            Assert(particleUdon.TryGetProgramVariable(
                       "ActiveParticleCount", out activeCount) && activeCount > 0,
                "Sakura active particle count");
            GameObject first = particleProxy.PoolObjects[0];
            MeshRenderer renderer = first == null
                ? null : first.GetComponent<MeshRenderer>();
            Assert(first != null && first.activeInHierarchy,
                "Sakura pool object active");
            Assert(renderer != null && renderer.enabled,
                "Sakura renderer enabled");
            Assert(renderer.sharedMaterial != null &&
                   renderer.sharedMaterial.mainTexture != null,
                "Sakura texture assigned");
        }

        private static void ValidateProvidedCompositeLiveResult()
        {
            int rendererCount = ReadInt("LoadedRenderNodeCount");
            int materialCount = ReadInt("LoadedMaterialCount");
            int emitterCount = ReadInt("LoadedParticleEmitterCount");
            Assert(rendererCount > 0, "Maria runtime renderer count");
            Assert(materialCount > 0, "Maria runtime material count");
            Assert(emitterCount > 0, "Maria runtime particle count");
            Assert(ReadBool("LoadedHasVat"), "Maria runtime VAT flag");
            Assert(ReadBool("LoadedHasParticles"),
                "Maria runtime particle flag");

            MeshFilter[] filters;
            MeshRenderer[] renderers;
            Assert(_loaderUdon.TryGetProgramVariable(
                       "SceneMeshFilters", out filters) &&
                   filters != null && filters.Length >= rendererCount,
                "Maria runtime MeshFilter pool");
            Assert(_loaderUdon.TryGetProgramVariable(
                       "SceneRenderers", out renderers) &&
                   renderers != null && renderers.Length >= rendererCount,
                "Maria runtime renderer pool");
            Assert(filters[0] != null && filters[0].sharedMesh != null,
                "Maria runtime mesh");
            Assert(renderers[0] != null && renderers[0].enabled &&
                   renderers[0].sharedMaterial != null,
                "Maria runtime material");

            Rac2ParticlePlayer[] players = _loaderProxy.ParticlePlayers;
            Assert(players != null && players.Length >= emitterCount &&
                   players[0] != null,
                "Maria particle player pool");
            UdonBehaviour particleUdon =
                UdonSharpEditorUtility.GetBackingUdonBehaviour(players[0]);
            Assert(particleUdon != null,
                "Maria particle backing UdonBehaviour");
            bool configured;
            Assert(particleUdon.TryGetProgramVariable(
                       "IsConfigured", out configured) && configured,
                "Maria particle configuration");

            Assert(Vector3.Distance(
                       _loaderProxy.transform.localPosition,
                       _expectedLocalPosition) < 0.0001f,
                "Maria local position reset on load");
            Assert(Quaternion.Angle(
                       _loaderProxy.transform.localRotation,
                       _expectedLocalRotation) < 0.01f,
                "Maria local rotation reset on load");
        }


        private static int ReadInt(string variable)
        {
            int value;
            if (!_loaderUdon.TryGetProgramVariable(variable, out value))
                throw new InvalidOperationException(
                    "Missing live Udon integer: " + variable);
            return value;
        }

        private static bool ReadBool(string variable)
        {
            bool value;
            if (!_loaderUdon.TryGetProgramVariable(variable, out value))
                throw new InvalidOperationException(
                    "Missing live Udon boolean: " + variable);
            return value;
        }

        private static string ReadString(string variable)
        {
            string value;
            if (!_loaderUdon.TryGetProgramVariable(variable, out value))
                return "<missing " + variable + ">";
            return value ?? string.Empty;
        }

        private static void Finish(bool succeeded, string message)
        {
            WriteResult(succeeded, message);
            if (succeeded)
                Debug.Log(LogPrefix + " " + message);
            else
                Debug.LogError(LogPrefix + " FAIL\n" + message);

            SessionState.SetString(SessionKey, StateReturningToEdit);
            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;
            else
                CleanupSession();
        }

        private static void WriteResult(bool succeeded, string message)
        {
            string text = (succeeded ? "PASS\n" : "FAIL\n") + message;
            File.WriteAllText(Path.GetFullPath(ResultPath), text);
        }

        private static void CleanupSession()
        {
            _providedStartedAt = 0d;
            SessionState.EraseString(SessionKey);
            DeleteIfPresent(PayloadPath);
            _instance = null;
            _loaderProxy = null;
            _loaderUdon = null;
            _warmupFrames = 0;
            _deadline = 0d;
        }

        private static void DeleteIfPresent(string relativePath)
        {
            string full = Path.GetFullPath(relativePath);
            if (File.Exists(full)) File.Delete(full);
        }

        private static void BuildCompressedPayload(string output)
        {
            Mesh multiMesh = null;
            Mesh staticMesh = null;
            Mesh particleMesh = null;
            Material red = null;
            Material blue = null;
            Material white = null;
            try
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null)
                    throw new InvalidOperationException(
                        "Standard test shader is unavailable.");

                red = new Material(shader)
                    { name = "Udon Test Red", color = Color.red };
                blue = new Material(shader)
                    { name = "Udon Test Blue", color = Color.blue };
                white = new Material(shader)
                    { name = "Udon Test White", color = Color.white };
                multiMesh = CreateMultiMaterialMesh();
                staticMesh = CreateStaticMesh();
                particleMesh = CreateParticleQuad();

                Vector3[] basePositions = multiMesh.vertices;
                Vector3[] baseNormals = multiMesh.normals;
                var positions = new Vector3[16][];
                var normals = new Vector3[16][];
                for (int frame = 0; frame < positions.Length; frame++)
                {
                    Vector3[] framePositions =
                        (Vector3[])basePositions.Clone();
                    float lift = frame < 8 ? frame * 0.0125f :
                        (15 - frame) * 0.0125f;
                    for (int vertex = 0; vertex < framePositions.Length;
                         vertex++)
                        framePositions[vertex].y += lift;
                    positions[frame] = framePositions;
                    normals[frame] = (Vector3[])baseNormals.Clone();
                }

                float particleExtent = Mathf.Sqrt(0.5f) * 0.1f;
                Vector3 boundsMinimum =
                    new Vector3(-0.4f, 0.1f, -particleExtent);
                Vector3 boundsMaximum =
                    new Vector3(0.85f, 1f + particleExtent, particleExtent);

                var bundle = new Rac2BinaryExporter.BundleData
                {
                    Bounds = new Bounds(
                        (boundsMinimum + boundsMaximum) * 0.5f,
                        boundsMaximum - boundsMinimum),
                    Interaction = new Rac2BinaryExporter.InteractionData
                    {
                        HasCollider = true,
                        IsPortable = true,
                    },
                    Product = new Rac2BinaryExporter.ProductData
                    {
                        ProductName = "Udon Composite Test",
                        CreatorName = "RAC2",
                        ProductUrl = "https://example.com/rac2",
                        AvatarBlueprintId =
                            "avtr_00000000-0000-0000-0000-000000000001",
                        TrialEnabled = true,
                    },
                };
                bundle.RenderNodes.Add(
                    new Rac2BinaryExporter.RenderNodeData
                    {
                        Name = "Animated Multi Material",
                        Mesh = multiMesh,
                        Materials = new[] { red, blue },
                        LocalPosition = Vector3.zero,
                        LocalRotation = Quaternion.identity,
                        LocalScale = Vector3.one,
                        Vat = new Rac2BinaryExporter.VatClipData
                        {
                            Name = "Udon Test VAT",
                            FramesPerSecond = 8f,
                            Loop = true,
                            Positions = positions,
                            Normals = normals,
                        },
                    });
                bundle.RenderNodes.Add(
                    new Rac2BinaryExporter.RenderNodeData
                    {
                        Name = "Static Secondary",
                        Mesh = staticMesh,
                        Materials = new[] { white },
                        LocalPosition = Vector3.zero,
                        LocalRotation = Quaternion.identity,
                        LocalScale = Vector3.one,
                    });
                bundle.ParticleEmitters.Add(
                    new Rac2BinaryExporter.ParticleEmitterData
                    {
                        Name = "Independent Sakura Test",
                        Mesh = particleMesh,
                        Particle = new Rac2BinaryExporter.ParticleData
                        {
                            Loop = true,
                            Billboard = true,
                            HideBaseMesh = true,
                            ShaderProfile = 0,
                            MaximumParticles = 4,
                            Duration = 2f,
                            Lifetime = 1f,
                            EmissionRate = 2f,
                            SpeedMinimum = 0f,
                            SpeedMaximum = 0f,
                            SizeMinimum = 0.1f,
                            SizeMaximum = 0.1f,
                            Gravity = 0f,
                            AngularSpeed = 30f,
                            Origin = new Vector3(0f, 1f, 0f),
                            Direction = Vector3.up,
                            Shape = 0,
                            ShapeRadius = 0f,
                            ShapeAngle = 0f,
                            ShapeScale = Vector3.one,
                            StartColor = Color.white,
                            EndColor =
                                new Color(1f, 0.7f, 0.85f, 0f),
                            FlipbookColumns = 1,
                            FlipbookRows = 1,
                        },
                    });

                Rac2BinaryExporter.BundleExportSummary summary =
                    Rac2BinaryExporter.ExportBundle(
                        bundle, output, 64, true);
                Assert(summary.FormatVersion == 3,
                    "exported format version");
                Assert(summary.RenderNodeCount == 2,
                    "exported renderer count");
                Assert(summary.MaterialCount == 3,
                    "exported material count");
                Assert(summary.VatNodeCount == 1,
                    "exported VAT count");
                Assert(summary.ParticleEmitterCount == 1,
                    "exported particle count");
                Assert(summary.CompressedSectionCount > 0,
                    "at least one compressed RAC2 section");
            }
            finally
            {
                if (multiMesh != null)
                    UnityEngine.Object.DestroyImmediate(multiMesh);
                if (staticMesh != null)
                    UnityEngine.Object.DestroyImmediate(staticMesh);
                if (particleMesh != null)
                    UnityEngine.Object.DestroyImmediate(particleMesh);
                if (red != null)
                    UnityEngine.Object.DestroyImmediate(red);
                if (blue != null)
                    UnityEngine.Object.DestroyImmediate(blue);
                if (white != null)
                    UnityEngine.Object.DestroyImmediate(white);
            }
        }

        private static Mesh CreateMultiMaterialMesh()
        {
            Mesh mesh = new Mesh
                { name = "RAC2 Udon Composite Multi Mesh" };
            mesh.vertices = new[]
            {
                new Vector3(-0.4f, 0.1f, 0f),
                new Vector3(0.4f, 0.1f, 0f),
                new Vector3(-0.4f, 0.9f, 0f),
                new Vector3(0.4f, 0.9f, 0f),
            };
            mesh.normals = new[]
            {
                Vector3.back, Vector3.back,
                Vector3.back, Vector3.back,
            };
            mesh.uv = new[]
            {
                Vector2.zero, Vector2.right,
                Vector2.up, Vector2.one,
            };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 2, 1 }, 0);
            mesh.SetTriangles(new[] { 2, 3, 1 }, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateStaticMesh()
        {
            Mesh mesh = new Mesh
                { name = "RAC2 Udon Composite Static Mesh" };
            mesh.vertices = new[]
            {
                new Vector3(0.55f, 0.2f, 0f),
                new Vector3(0.85f, 0.2f, 0f),
                new Vector3(0.7f, 0.5f, 0f),
            };
            mesh.normals = new[]
            {
                Vector3.back, Vector3.back, Vector3.back,
            };
            mesh.uv = new[]
            {
                Vector2.zero, Vector2.right, Vector2.up,
            };
            mesh.triangles = new[] { 0, 2, 1 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateParticleQuad()
        {
            Mesh mesh = new Mesh
                { name = "RAC2 Udon Composite Particle Mesh" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.normals = new[]
            {
                Vector3.back, Vector3.back,
                Vector3.back, Vector3.back,
            };
            mesh.uv = new[]
            {
                Vector2.zero, Vector2.right,
                Vector2.up, Vector2.one,
            };
            mesh.triangles =
                new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void Assert(bool condition, string label)
        {
            if (!condition)
                throw new InvalidOperationException(
                    "RAC2 Udon VM assertion failed: " + label);
        }
    }
}
