using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    /// <summary>Behavioural checks for incremental RAC2 decode, shared resources and deterministic particles.</summary>
    [InitializeOnLoad]
    public static class Rac2AlgorithmRegression
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int _checks;
        private const string BatchVmKey = "Rac2AlgorithmRegression.BatchVm";

        static Rac2AlgorithmRegression()
        {
            EditorApplication.update += PollVmBatch;
        }

        private static void PollVmBatch()
        {
            if (!SessionState.GetBool(BatchVmKey, false)) return;
            string resultPath = "Library/Rac2CompositeUdonPlayModeTest.result";
            if (File.Exists(resultPath) && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SessionState.SetBool(BatchVmKey, false);
                EditorApplication.Exit(File.ReadAllText(resultPath).StartsWith("PASS") ? 0 : 1);
            }
            else if (EditorApplication.timeSinceStartup > SessionState.GetFloat(BatchVmKey + ".deadline", float.MaxValue))
            {
                File.WriteAllText("Library/Rac2AlgorithmRegression-vm-timeout.result", "FAIL: batch Play Mode exceeded 420 seconds.");
                SessionState.SetBool(BatchVmKey, false);
                EditorApplication.Exit(2);
            }
        }

        public static void RunBatch()
        {
            try
            {
                UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
                if (UdonSharpProgramAsset.AnyUdonSharpScriptHasError())
                    throw new InvalidOperationException("UdonSharp compilation failed; see the Unity log.");
                Run();
                File.WriteAllText("Library/Rac2AlgorithmRegression.result", "PASS " + _checks + " assertions; native Unity editor, not VRChat performance.\n");
                EditorSceneManager.OpenScene("Assets/NightSlotMall/Scenes/NightSlot-Mall-Prototype.unity");
                SessionState.SetBool(BatchVmKey, true);
                SessionState.SetFloat(BatchVmKey + ".deadline", (float)EditorApplication.timeSinceStartup + 420f);
                Rac2CompositeUdonPlayModeTest.Run();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                File.WriteAllText("Library/Rac2AlgorithmRegression.result", "FAIL\n" + error);
                EditorApplication.Exit(1);
            }
        }

        [MenuItem("Tools/FLARE/Developer/Tests/Run Algorithm Regression")]
        public static void Run()
        {
            _checks = 0;
            TestLz4();
            TestChecksum();
            TestParticles();
            TestAnimatedTransform();
            TestSharedTextures();
            Rac2CompositeSelfTest.Run();
            Rac2ProductSelfTest.Run();
            Debug.Log("[RAC2 Algorithm] PASS " + _checks);
        }

        private static void Check(bool condition, string label)
        {
            _checks++;
            if (!condition) throw new InvalidOperationException(label);
        }

        private static object Get(object target, string name) =>
            target.GetType().GetField(name, Private).GetValue(target);
        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, Private).SetValue(target, value);
        private static object Call(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, Private).Invoke(target, args);

        public static void DrainBundle(Rac2RuntimeLoader loader)
        {
            int iterations = 0;
            while ((bool)Get(loader, "_bundleRestorePending"))
            {
                if (++iterations > 100000) throw new InvalidOperationException("Bundle failed to terminate.");
                byte[] payload = (byte[])Get(loader, "_data");
                if (!(bool)Call(loader, "ParseAndApplyBundle", payload))
                    throw new InvalidOperationException((string)Get(loader, "_parseError"));
            }
        }

        private static void TestLz4()
        {
            var root = new GameObject("RAC2 LZ4 regression");
            root.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var loader = root.AddUdonSharpComponent<Rac2RuntimeLoader>();
                MethodInfo compress = typeof(Rac2BinaryExporter).GetMethod("CompressLz4Block",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var random = new System.Random(8123);
                foreach (int length in new[] { 1, 15, 16, 255, 4096, 131072 })
                {
                    foreach (int pattern in new[] { 0, 1, 2 })
                    {
                        byte[] input = new byte[length];
                        random.NextBytes(input);
                        if (pattern != 0)
                            for (int i = 0; i < length; i++) input[i] = (byte)(pattern == 1 ? 255 : i % 3);
                        byte[] encoded = (byte[])compress.Invoke(null, new object[] { input });
                        foreach (int budget in new[] { 1, 7, 1024 })
                        {
                            Set(loader, "_expandSource", encoded);
                            byte[] output = new byte[length];
                            Set(loader, "_expandTarget", output);
                            Set(loader, "_expandInput", 0);
                            Set(loader, "_expandInputEnd", encoded.Length);
                            Set(loader, "_expandOutput", 0);
                            Set(loader, "_expandOutputEnd", length);
                            Set(loader, "_expandTargetStart", 0);
                            Set(loader, "_lzPhase", 0);
                            int result = 0, count = 0;
                            while (result == 0 && ++count < length * 4 + 100)
                                result = (int)Call(loader, "DecompressLz4Slice", budget);
                            Check(result == 1 && input.SequenceEqual(output),
                                "LZ4 boundary/overlap round trip length=" + length + " pattern=" + pattern + " budget=" + budget);
                        }
                    }
                }
                // A cut length extension must report failure, not throw or loop.
                Set(loader, "_expandSource", new byte[] { 0xF0, 255 });
                Set(loader, "_expandTarget", new byte[4096]);
                Set(loader, "_expandInput", 0);
                Set(loader, "_expandInputEnd", 2);
                Set(loader, "_expandOutput", 0);
                Set(loader, "_expandOutputEnd", 4096);
                Set(loader, "_lzPhase", 0);
                int malformed = 0;
                for (int i = 0; i < 10 && malformed == 0; i++)
                    malformed = (int)Call(loader, "DecompressLz4Slice", 1);
                Check(malformed == -1, "Malformed LZ4 length fails cleanly.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void TestChecksum()
        {
            byte[] input = Enumerable.Repeat((byte)255, 5552).ToArray();
            MethodInfo standard = typeof(Rac2BinaryExporter).GetMethod("StandardAdler32", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo legacy = typeof(Rac2BinaryExporter).GetMethod("Adler32", BindingFlags.Static | BindingFlags.NonPublic);
            Check((uint)standard.Invoke(null, new object[] { input, 0, input.Length }) == 0xF18F9B8Cu, "Standard Adler vector.");
            Check((uint)legacy.Invoke(null, new object[] { input, 0, input.Length }) == 0xF0BD9B8Cu, "Legacy Adler compatibility vector.");
            var root = new GameObject("RAC2 checksum regression");
            root.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var loader = root.AddUdonSharpComponent<Rac2RuntimeLoader>();
                foreach (bool modern in new[] { false, true })
                foreach (int budget in new[] { 1, 7, 2776, 5552, 65536 })
                {
                    Set(loader, "_standardChecksum", modern);
                    Set(loader, "_expandTarget", input);
                    Set(loader, "_expandTargetStart", 0);
                    Set(loader, "_expandOutputEnd", input.Length);
                    Call(loader, "BeginIncrementalChecksum");
                    while ((int)Get(loader, "_expandChecksumOffset") < input.Length)
                        Call(loader, "ContinueIncrementalChecksum", budget);
                    uint actual = ((uint)((int)Get(loader, "_expandChecksumB") & 65535) << 16) |
                        (uint)((int)Get(loader, "_expandChecksumA") & 65535);
                    Check(actual == (modern ? 0xF18F9B8Cu : 0xF0BD9B8Cu), "Checksum across frame boundary " + budget);
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void TestParticles()
        {
            var root = new GameObject("RAC2 particle regression");
            root.hideFlags = HideFlags.HideAndDontSave;
            Mesh mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            Material template = new Material(Shader.Find("Avatar Catalog/RAC2 Particle Alpha"));
            try
            {
                var player = root.AddUdonSharpComponent<Rac2ParticlePlayer>();
                var particle = new GameObject("particle", typeof(MeshFilter), typeof(MeshRenderer));
                particle.transform.SetParent(root.transform);
                player.PoolObjects = new[] { particle };
                player.ParticleMesh = mesh;
                player.AlphaTemplate = template;
                player.MaximumParticles = 1;
                player.Lifetime = 10;
                player.Duration = 2;
                player.SizeMinimum = player.SizeMaximum = 1;
                player.ClipToBooth = false;
                player.Billboard = true;
                player.AngularSpeed = 90;
                player.StartColor = Color.white;
                player.EndColor = Color.white;
                player.EmissionRate = 0;
                player.ApplyConfiguration();
                player.SimulateStep(1);
                Check(!particle.GetComponent<MeshRenderer>().enabled && player.ActiveParticleCount == 0,
                    "Zero emission stays empty.");
                foreach (float gravity in new[] { -9.81f, 0f, 9.81f })
                foreach (int fps in new[] { 15, 30, 90 })
                {
                    player.EmissionRate = 0.1f;
                    player.Gravity = gravity;
                    player.ApplyConfiguration();
                    float initialSpin = particle.GetComponent<MeshRenderer>().sharedMaterial.GetFloat("_Spin");
                    for (int frame = 0; frame < fps; frame++) player.SimulateStep(1f / fps);
                    Check(Mathf.Abs(particle.transform.localPosition.y + 0.5f * gravity) < 0.0001f,
                        "Analytic particle trajectory FPS=" + fps + " gravity=" + gravity);
                    float spin = particle.GetComponent<MeshRenderer>().sharedMaterial.GetFloat("_Spin");
                    Check(Mathf.Abs((spin - initialSpin) - Mathf.PI * 0.5f) < 0.0001f,
                        "Billboard angular speed reaches shader.");
                    player.ClearParticles();
                }
                var second = new GameObject("particle 2", typeof(MeshFilter), typeof(MeshRenderer));
                second.transform.SetParent(root.transform);
                player.PoolObjects = new[] { particle, second };
                player.MaximumParticles = 2;
                player.EmissionRate = 10;
                player.Loop = true;
                player.ApplyConfiguration();
                player.SimulateStep(0.2f);
                player.SimulateStep(0.2f);
                float[] births = (float[])Get(player, "_birthTimes");
                Check(Mathf.Abs(Mathf.Min(births[0], births[1]) - 0.3f) < 0.0001f &&
                    Mathf.Abs(Mathf.Max(births[0], births[1]) - 0.4f) < 0.0001f,
                    "Saturated particle pool retains the newest cohort within a single frame.");
                player.ClearParticles();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(template);
            }
        }

        private static void TestAnimatedTransform()
        {
            var root = new GameObject("RAC2 VAT transform regression");
            root.hideFlags = HideFlags.HideAndDontSave;
            var child = new GameObject("animated");
            child.transform.SetParent(root.transform);
            var renderer = child.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right * 0.1f, Vector3.up * 0.1f };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.boneWeights = new[] {
                new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                new BoneWeight { boneIndex0 = 0, weight0 = 1 } };
            mesh.bindposes = new[] { Matrix4x4.identity };
            renderer.sharedMesh = mesh;
            renderer.bones = new[] { child.transform };
            renderer.rootBone = child.transform;
            var material = new Material(Shader.Find("Standard"));
            renderer.sharedMaterial = material;
            var clip = new AnimationClip { legacy = true };
            clip.SetCurve("animated", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
            var window = ScriptableObject.CreateInstance<Rac2CreatorWindow>();
            var temporary = new List<Mesh>();
            try
            {
                Set(window, "_root", root);
                Set(window, "_clip", clip);
                Set(window, "_loop", false);
                AnimationMode.StartAnimationMode();
                var bundle = new Rac2BinaryExporter.BundleData();
                Call(window, "CollectSkinnedRenderers", bundle, temporary, 3);
                Check(bundle.RenderNodes.Count == 1, "VAT animated renderer exported.");
                var node = bundle.RenderNodes[0];
                Check(Mathf.Abs(node.Vat.Positions[2][0].x - node.Vat.Positions[0][0].x - 1f) < 0.001f,
                    "Renderer transform animation is baked into VAT.");
                Check(node.LocalPosition == Vector3.zero && node.LocalScale == Vector3.one &&
                    node.LocalRotation == Quaternion.identity, "VAT node uses fixed exhibit coordinates.");
            }
            finally
            {
                AnimationMode.StopAnimationMode();
                foreach (Mesh item in temporary) UnityEngine.Object.DestroyImmediate(item);
                UnityEngine.Object.DestroyImmediate(window);
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        private static void TestSharedTextures()
        {
            Mesh mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(0, 0, 0), new Vector3(.1f, 0, 0), new Vector3(0, .1f, 0) };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateNormals();
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            Texture2D image = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            image.SetPixels(Enumerable.Repeat(Color.red, 256).ToArray());
            image.Apply();
            Material material = new Material(Shader.Find("Standard"));
            material.mainTexture = image;
            GameObject instance = null;
            try
            {
                var bundle = new Rac2BinaryExporter.BundleData {
                    Bounds = new Bounds(new Vector3(.05f, .05f, 0), new Vector3(.1f, .1f, 0)),
                    Product = new Rac2BinaryExporter.ProductData { ProductName = "Shared product", CreatorName = "Regression" } };
                for (int i = 0; i < 2; i++) bundle.RenderNodes.Add(new Rac2BinaryExporter.RenderNodeData {
                    Mesh = mesh, Materials = new[] { material },
                    Vat = new Rac2BinaryExporter.VatClipData {
                        Name = "sync", FramesPerSecond = 30, Loop = false,
                        Positions = new[] { mesh.vertices, mesh.vertices } } });
                var shared = Rac2BinaryExporter.ExportBundle(bundle, "Library/Rac2-shared-test.rac2", 16, false, true);
                var legacy = Rac2BinaryExporter.ExportBundle(bundle, "Library/Rac2-inline-test.rac2", 16, false, false);
                Check(legacy.FileSize - shared.FileSize == 16 * 16 * 4 + 16 - 8,
                    "Shared texture saves exactly one payload minus its reference.");
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/RemoteAvatarCatalogDistribution/Prefabs/RAC2-ImagePad.prefab");
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.hideFlags = HideFlags.HideAndDontSave;
                var loader = instance.GetComponentInChildren<Rac2RuntimeLoader>(true);
                loader.TargetObjectSync = null;
                loader.ProductController = null;
                loader.MeshElementsPerFrame = 16;
                byte[] bytes = File.ReadAllBytes("Library/Rac2-shared-test.rac2");
                Check((bool)Call(loader, "ParseAndApply", bytes), "Shared bundle starts.");
                int iterations = 0;
                while ((bool)Get(loader, "_bundleRestorePending"))
                {
                    Material[] active = (Material[])Get(loader, "_bundleMaterials");
                    if (active != null)
                        foreach (Material item in active)
                            if (item != null) item.SetFloat("_VatStartTime", -1000 - iterations);
                    Check(++iterations < 1000, "Incremental bundle terminates.");
                    Check((bool)Call(loader, "ParseAndApplyBundle", bytes), "Incremental bundle succeeds.");
                }
                Material a = loader.SceneRenderers[0].sharedMaterial;
                Material b = loader.SceneRenderers[1].sharedMaterial;
                Check(a.mainTexture == b.mainTexture, "Shared Texture2D identity.");
                Check(!((Texture2D)a.mainTexture).isReadable, "CPU texture storage released.");
                Check(a.GetFloat("_VatStartTime") == b.GetFloat("_VatStartTime") &&
                    a.GetFloat("_VatStartTime") > -100, "All VAT starts reset at final publication.");
                loader.Clear();
                Check(loader.LoadedRenderNodeCount == 0, "Clear resets bundle.");
                Check((bool)Call(loader, "ParseAndApply", File.ReadAllBytes("Library/Rac2-inline-test.rac2")),
                    "Old inline texture encoding remains loadable.");
                DrainBundle(loader);
                Check(loader.LoadedRenderNodeCount == 2, "Reload after shared texture cleanup.");
                loader.Clear();
                Rac2BinaryExporter.ExportBundle(bundle, "Library/Rac2-shared-compressed-test.rac2", 16, true, true);
                byte[] modern = File.ReadAllBytes("Library/Rac2-shared-compressed-test.rac2");
                Check(BitConverter.ToUInt32(modern, 16) == 3u, "Standard checksum signalled explicitly.");
                Check((bool)Call(loader, "ParseAndApply", modern), "Compressed shared bundle accepted.");
                DrainBundle(loader);
                Check(loader.LoadedRenderNodeCount == 2, "Compressed shared bundle restored.");
                loader.Clear();
                var pedestalRoot = new GameObject("RAC2 metadata regression");
                pedestalRoot.hideFlags = HideFlags.HideAndDontSave;
                try
                {
                    var product = pedestalRoot.AddUdonSharpComponent<Rac2ProductLoader>();
                    product.ProductController = pedestalRoot.AddUdonSharpComponent<Rac2ProductController>();
                    Check((bool)Call(product, "ParseAndApply", modern), "Standalone pedestal reads new composite metadata.");
                    Check(product.LoadedProductName == "Shared product", "Composite product name preserved.");
                }
                finally { UnityEngine.Object.DestroyImmediate(pedestalRoot); }
            }
            finally
            {
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(image);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
