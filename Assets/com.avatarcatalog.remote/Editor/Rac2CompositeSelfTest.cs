using System;
using System.IO;
using System.Reflection;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    /// <summary>End-to-end coverage for the public composite RAC2 workflow.</summary>
    [InitializeOnLoad]
    public static class Rac2CompositeSelfTest
    {
        private const string RequestPath = "Library/Rac2CompositeSelfTest.run";
        private const string ResultPath = "Library/Rac2CompositeSelfTest.result";
        private const string PrefabPath =
            "Assets/NightSlotMall/Prefabs/RAC-3D-ImagePad.prefab";

        static Rac2CompositeSelfTest()
        {
            EditorApplication.update += ConsumeRequest;
        }

        [MenuItem("Tools/FLARE/Developer/Tests/Run Composite RAC2 Round Trip")]
        public static void Run()
        {
            string output = Path.GetFullPath("Library/Rac2CompositeSelfTest.rac2");
            bool succeeded = false;
            Mesh multiMesh = null;
            Mesh staticMesh = null;
            Mesh particleMesh = null;
            Material red = null;
            Material blue = null;
            Material white = null;
            GameObject instance = null;
            Rac2CreatorWindow creatorWindow = null;
            try
            {
                MethodInfo boothFit = typeof(Rac2CreatorWindow).GetMethod(
                    "BoothBoundsFit",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo describeOverflow = typeof(Rac2CreatorWindow).GetMethod(
                    "DescribeBoothOverflow",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo placementOffset = typeof(Rac2CreatorWindow).GetMethod(
                    "CalculateAutomaticPlacementOffset",
                    BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo calculateBundleBounds = typeof(Rac2CreatorWindow).GetMethod(
                    "CalculateBundleBounds",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(boothFit != null && describeOverflow != null &&
                       placementOffset != null && calculateBundleBounds != null,
                    "booth guide validation entry points");
                Vector3 exampleMinimum =
                    new Vector3(-1.195f, -2.141f, -0.745f);
                Vector3 exampleMaximum =
                    new Vector3(1.430f, 2.125f, 1.341f);
                Bounds exampleOverflow = new Bounds(
                    (exampleMinimum + exampleMaximum) * 0.5f,
                    exampleMaximum - exampleMinimum);
                Assert(
                    !(bool)boothFit.Invoke(null, new object[] { exampleOverflow }),
                    "booth guide detects excessive height");
                string overflowText = (string)describeOverflow.Invoke(
                    null, new object[] { exampleOverflow });
                bool reportsHeight =
                    overflowText.Contains("高さ 4.27m");
                Assert(reportsHeight,
                    "booth guide explains excessive height");

                Bounds translatedButSmall = new Bounds(
                    new Vector3(12f, -8f, 30f),
                    new Vector3(2.5f, 2.5f, 2.5f));
                Assert(
                    (bool)boothFit.Invoke(
                        null, new object[] { translatedButSmall }),
                    "booth fit ignores Root and Pivot offset");
                Vector3 offset = (Vector3)placementOffset.Invoke(
                    null, new object[] { translatedButSmall });
                Bounds normalized = new Bounds(
                    translatedButSmall.center + offset,
                    translatedButSmall.size);
                Assert(
                    Mathf.Abs(normalized.center.x) < 0.0001f &&
                    Mathf.Abs(normalized.min.y) < 0.0001f &&
                    Mathf.Abs(normalized.center.z) < 0.0001f,
                    "automatic floor placement and centering");

                Shader shader = Shader.Find("Standard");
                if (shader == null)
                    throw new InvalidOperationException("Standard shader is unavailable.");
                red = new Material(shader) { name = "RAC2 Test Red", color = Color.red };
                blue = new Material(shader) { name = "RAC2 Test Blue", color = Color.blue };
                white = new Material(shader) { name = "RAC2 Test White", color = Color.white };
                multiMesh = CreateMultiMaterialMesh();
                staticMesh = CreateStaticMesh();
                particleMesh = CreateParticleQuad();

                Vector3[] basePositions = multiMesh.vertices;
                Vector3[] movedPositions = (Vector3[])basePositions.Clone();
                for (int index = 0; index < movedPositions.Length; index++)
                    movedPositions[index].y += 0.4f;
                Vector3[] normals = multiMesh.normals;
                var bundle = new Rac2BinaryExporter.BundleData
                {
                    Interaction = new Rac2BinaryExporter.InteractionData
                    {
                        HasCollider = true,
                        IsPortable = true,
                    },
                    Product = new Rac2BinaryExporter.ProductData
                    {
                        ProductName = "Composite Test",
                        CreatorName = "RAC2",
                        ProductUrl = "https://example.com/rac2",
                        AvatarBlueprintId = "",
                        TrialEnabled = false,
                    },
                };
                bundle.RenderNodes.Add(new Rac2BinaryExporter.RenderNodeData
                {
                    Name = "Animated Multi Material",
                    Mesh = multiMesh,
                    Materials = new[] { red, blue },
                    LocalPosition = Vector3.zero,
                    LocalRotation = Quaternion.identity,
                    LocalScale = Vector3.one,
                    Vat = new Rac2BinaryExporter.VatClipData
                    {
                        Name = "Test Clip",
                        FramesPerSecond = 2f,
                        Loop = true,
                        Positions = new[] { basePositions, movedPositions },
                        Normals = new[] { normals, normals },
                    },
                });
                bundle.RenderNodes.Add(new Rac2BinaryExporter.RenderNodeData
                {
                    Name = "Static Secondary",
                    Mesh = staticMesh,
                    Materials = new[] { white },
                    LocalPosition = Vector3.zero,
                    LocalRotation = Quaternion.identity,
                    LocalScale = Vector3.one,
                });
                bundle.ParticleEmitters.Add(new Rac2BinaryExporter.ParticleEmitterData
                {
                    Name = "Independent Particle",
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
                        EndColor = new Color(1f, 1f, 1f, 0f),
                        FlipbookColumns = 1,
                        FlipbookRows = 1,
                    },
                });

                Rac2BinaryExporter.ParticleData testParticle =
                    bundle.ParticleEmitters[0].Particle;
                Vector3 originalOrigin = testParticle.Origin;
                int originalShape = testParticle.Shape;
                Vector3 originalShapeScale = testParticle.ShapeScale;
                testParticle.Origin = new Vector3(40f, -50f, 30f);
                testParticle.Shape = 3;
                testParticle.ShapeScale = Vector3.one * 100f;
                creatorWindow =
                    ScriptableObject.CreateInstance<Rac2CreatorWindow>();
                Bounds modelOnlyBounds = (Bounds)calculateBundleBounds.Invoke(
                    creatorWindow, new object[] { bundle });
                Assert(
                    Mathf.Abs(modelOnlyBounds.min.x + 0.4f) < 0.0001f &&
                    Mathf.Abs(modelOnlyBounds.min.y - 0.1f) < 0.0001f &&
                    Mathf.Abs(modelOnlyBounds.max.x - 0.85f) < 0.0001f &&
                    Mathf.Abs(modelOnlyBounds.max.y - 1.3f) < 0.0001f,
                    "Emitter origin and shape are excluded from model bounds");
                testParticle.Origin = originalOrigin;
                testParticle.Shape = originalShape;
                testParticle.ShapeScale = originalShapeScale;
                bundle.Bounds = modelOnlyBounds;

                Rac2BinaryExporter.BundleExportSummary summary =
                    Rac2BinaryExporter.ExportBundle(bundle, output, 64, false);
                Assert(summary.FormatVersion == 3, "format version");
                Assert(summary.RenderNodeCount == 2, "renderer summary");
                Assert(summary.MaterialCount == 3, "material summary");
                Assert(summary.VatNodeCount == 1, "VAT summary");
                Assert(summary.ParticleEmitterCount == 1, "particle summary");

                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                if (prefab == null)
                    throw new InvalidOperationException("ImagePad prefab is missing.");
                instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null)
                    throw new InvalidOperationException("ImagePad test instance could not be created.");
                instance.hideFlags = HideFlags.HideAndDontSave;
                Rac2RuntimeLoader loader =
                    instance.GetComponentInChildren<Rac2RuntimeLoader>(true);
                if (loader == null)
                    throw new InvalidOperationException("ImagePad loader is missing.");
                Assert(loader.SceneMeshFilters != null &&
                       loader.SceneMeshFilters.Length >= 16,
                    "prefab renderer proxy pool");
                VRC.Udon.UdonBehaviour backing =
                    UdonSharpEditorUtility.GetBackingUdonBehaviour(loader);
                object backingFilterValue = null;
                Type backingFilterType = null;
                bool backingHasValue = backing != null &&
                    backing.publicVariables.TryGetVariableValue(
                        "SceneMeshFilters", out backingFilterValue);
                bool backingHasType = backing != null &&
                    backing.publicVariables.TryGetVariableType(
                        "SceneMeshFilters", out backingFilterType);
                Array backingFilterArray = backingFilterValue as Array;
                Assert(backingHasValue && backingFilterArray != null && backingFilterArray.Length >= 16,
                    "prefab renderer Udon pool (has type=" + backingHasType +
                    ", type=" + (backingFilterType == null ? "null" : backingFilterType.FullName) +
                    ", value=" + (backingFilterValue == null ? "null" : backingFilterValue.GetType().FullName) + ")");
                loader.TargetObjectSync = null;
                loader.ProductController = null;

                MethodInfo parse = typeof(Rac2RuntimeLoader).GetMethod(
                    "ParseAndApply", BindingFlags.Instance | BindingFlags.NonPublic);
                if (parse == null)
                    throw new InvalidOperationException("RAC2 parse entry point is missing.");
                bool parsed = (bool)parse.Invoke(loader, new object[] { File.ReadAllBytes(output) });
                if (parsed) Rac2AlgorithmRegression.DrainBundle(loader);
                if (!parsed)
                {
                    FieldInfo parseError = typeof(Rac2RuntimeLoader).GetField(
                        "_parseError", BindingFlags.Instance | BindingFlags.NonPublic);
                    throw new InvalidOperationException(
                        "Composite RAC2 parse failed: " + (parseError == null ? "unknown" : parseError.GetValue(loader)));
                }

                Assert(loader.LoadedRenderNodeCount == 2, "runtime renderer count");
                Assert(loader.LoadedMaterialCount == 3, "runtime material count");
                Assert(loader.LoadedParticleEmitterCount == 1, "runtime emitter count");
                Assert(loader.LoadedHasVat, "runtime VAT flag");
                Assert(loader.LoadedHasParticles, "runtime particle flag");
                Assert(loader.LoadedIsPortable, "runtime portable metadata");
                Assert(loader.LoadedHasProduct, "runtime product metadata");
                Assert(loader.LoadedProductName == "Composite Test", "runtime product name");
                Assert(loader.SceneMeshFilters[0].sharedMesh.subMeshCount == 2,
                    "runtime submesh count");
                Assert(loader.SceneRenderers[0].sharedMaterials.Length == 2,
                    "runtime material array");
                Assert(loader.ParticlePlayers[0].IsConfigured,
                    "runtime particle configuration");
                Assert(loader.ParticlePlayers[0].ParticleMesh !=
                       loader.SceneMeshFilters[0].sharedMesh,
                    "particle mesh independence");

                Rac2ParticlePlayer clippingPlayer =
                    loader.ParticlePlayers[0];
                clippingPlayer.Origin = Vector3.zero;
                clippingPlayer.Direction = Vector3.right;
                clippingPlayer.SpeedMinimum = 10f;
                clippingPlayer.SpeedMaximum = 10f;
                clippingPlayer.Lifetime = 2f;
                clippingPlayer.EmissionRate = 0.1f;
                clippingPlayer.ClipToBooth = true;
                clippingPlayer.BoothWidth = 3f;
                clippingPlayer.BoothDepth = 3f;
                clippingPlayer.BoothHeight = 2.7f;
                clippingPlayer.BoothBoundaryTolerance = 0f;
                clippingPlayer.ApplyConfiguration();
                MeshRenderer clippingRenderer =
                    clippingPlayer.PoolObjects[0].GetComponent<MeshRenderer>();
                Assert(clippingRenderer.enabled,
                    "particle starts visible inside booth");
                clippingPlayer.SimulateStep(0.2f);
                Assert(clippingPlayer.ActiveParticleCount > 0 &&
                       !clippingRenderer.enabled,
                    "particle remains simulated but is hidden outside booth");

                loader.Clear();
                string sakuraPath = Path.GetFullPath(
                    "Assets/Textures/Sakura Emitter.rac2");
                if (!File.Exists(sakuraPath))
                    throw new InvalidOperationException("Sakura RAC2 test asset is missing.");
                bool sakuraParsed = (bool)parse.Invoke(
                    loader, new object[] { File.ReadAllBytes(sakuraPath) });
                if (!sakuraParsed)
                {
                    FieldInfo parseError = typeof(Rac2RuntimeLoader).GetField(
                        "_parseError", BindingFlags.Instance | BindingFlags.NonPublic);
                    throw new InvalidOperationException(
                        "Sakura RAC2 parse failed: " +
                        (parseError == null ? "unknown" : parseError.GetValue(loader)));
                }
                Rac2ParticlePlayer sakuraPlayer = loader.ParticlePlayer;
                Assert(loader.LoadedHasParticles, "Sakura runtime particle flag");
                Assert(sakuraPlayer != null && sakuraPlayer.IsConfigured,
                    "Sakura runtime particle configuration");
                sakuraPlayer.SimulateStep(0.2f);
                Assert(sakuraPlayer.ActiveParticleCount > 0,
                    "Sakura emits a visible particle");
                MeshRenderer sakuraRenderer =
                    sakuraPlayer.PoolObjects[0].GetComponent<MeshRenderer>();
                Assert(sakuraPlayer.PoolObjects[0].activeInHierarchy,
                    "Sakura particle pool object is active");
                Assert(sakuraRenderer != null && sakuraRenderer.enabled,
                    "Sakura particle renderer is enabled");
                Assert(sakuraRenderer.sharedMaterial != null &&
                       sakuraRenderer.sharedMaterial.mainTexture != null,
                    "Sakura particle texture is assigned");
                loader.Clear();
                Debug.Log(
                    "[RAC2 Composite Self-Test] PASS - 2 renderers, 3 materials, " +
                    "VAT + independent particle + portable/product metadata.");
                succeeded = true;
            }
            finally
            {
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
                if (creatorWindow != null) UnityEngine.Object.DestroyImmediate(creatorWindow);
                if (multiMesh != null) UnityEngine.Object.DestroyImmediate(multiMesh);
                if (staticMesh != null) UnityEngine.Object.DestroyImmediate(staticMesh);
                if (particleMesh != null) UnityEngine.Object.DestroyImmediate(particleMesh);
                if (red != null) UnityEngine.Object.DestroyImmediate(red);
                if (blue != null) UnityEngine.Object.DestroyImmediate(blue);
                if (white != null) UnityEngine.Object.DestroyImmediate(white);
                if (succeeded && File.Exists(output)) File.Delete(output);
            }
        }

        private static void ConsumeRequest()
        {
            string request = Path.GetFullPath(RequestPath);
            if (!File.Exists(request)) return;
            File.Delete(request);
            try
            {
                Run();
                File.WriteAllText(Path.GetFullPath(ResultPath), "PASS");
            }
            catch (Exception exception)
            {
                File.WriteAllText(Path.GetFullPath(ResultPath), "FAIL\n" + exception);
                Debug.LogException(exception);
            }
        }

        private static Mesh CreateMultiMaterialMesh()
        {
            Mesh mesh = new Mesh { name = "RAC2 Composite Multi Mesh" };
            mesh.vertices = new[]
            {
                new Vector3(-0.4f, 0.1f, 0f),
                new Vector3(0.4f, 0.1f, 0f),
                new Vector3(-0.4f, 0.9f, 0f),
                new Vector3(0.4f, 0.9f, 0f),
            };
            mesh.normals = new[]
            {
                Vector3.back, Vector3.back, Vector3.back, Vector3.back,
            };
            mesh.uv = new[]
            {
                Vector2.zero, Vector2.right, Vector2.up, Vector2.one,
            };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 2, 1 }, 0);
            mesh.SetTriangles(new[] { 2, 3, 1 }, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateStaticMesh()
        {
            Mesh mesh = new Mesh { name = "RAC2 Composite Static Mesh" };
            mesh.vertices = new[]
            {
                new Vector3(0.55f, 0.2f, 0f),
                new Vector3(0.85f, 0.2f, 0f),
                new Vector3(0.7f, 0.5f, 0f),
            };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            mesh.triangles = new[] { 0, 2, 1 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateParticleQuad()
        {
            Mesh mesh = new Mesh { name = "RAC2 Composite Particle Mesh" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.normals = new[]
            {
                Vector3.back, Vector3.back, Vector3.back, Vector3.back,
            };
            mesh.uv = new[]
            {
                Vector2.zero, Vector2.right, Vector2.up, Vector2.one,
            };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void Assert(bool condition, string label)
        {
            if (!condition)
                throw new InvalidOperationException(
                    "Composite RAC2 assertion failed: " + label);
        }
    }
}
