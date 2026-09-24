using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    /// <summary>
    /// Dependency-free Editor smoke test. It exercises both static and skinned
    /// inputs, atlas generation, bounds validation and the existing RAC1 writer.
    /// It can also be called with -executeMethod in a dedicated Unity process.
    /// </summary>
    public static class Rac1BoothAuthoringSelfTest
    {
        private const string MenuPath = "Tools/FLARE/Developer/Tests/RAC1/Run Booth Authoring Self-Test";

        [MenuItem(MenuPath)]
        public static void RunFromMenu()
        {
            Run();
            if (!Application.isBatchMode)
            {
                EditorUtility.DisplayDialog("Booth Authoring self-test", "PASS", "OK");
            }
        }

        public static void RunFromCommandLine()
        {
            try
            {
                Run();
                Debug.Log("[RAC1 Booth Self-Test] PASS");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Debug.LogError("[RAC1 Booth Self-Test] FAIL: " + exception.Message);
                EditorApplication.Exit(1);
            }
        }

        private static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("Exit Play Mode before running the Booth Authoring self-test.");
            }

            string outputPath = Path.Combine(
                Path.GetTempPath(),
                "rac1-booth-self-test-" + Guid.NewGuid().ToString("N") + ".rac1");
            GameObject booth = null;
            Material material = null;
            Material solidMaterial = null;
            Texture2D texture = null;
            Mesh skinnedMesh = null;
            Mesh shopMesh = null;
            try
            {
                Shader shader = Shader.Find("Avatar Catalog/RAC1 Opaque Cutout") ?? Shader.Find("Standard");
                Require(shader != null, "Catalog or Standard shader is missing.");

                texture = CreateCheckerTexture();
                texture.wrapMode = TextureWrapMode.Repeat;
                material = new Material(shader) { name = "Booth Self-Test Material" };
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
                if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);

                solidMaterial = new Material(shader) { name = "Booth Self-Test Solid Material" };
                if (solidMaterial.HasProperty("_MainTex")) solidMaterial.SetTexture("_MainTex", null);
                if (solidMaterial.HasProperty("_BaseMap")) solidMaterial.SetTexture("_BaseMap", null);
                Color solidColor = new Color(0.15f, 0.7f, 0.35f, 1f);
                if (solidMaterial.HasProperty("_Color")) solidMaterial.SetColor("_Color", solidColor);
                if (solidMaterial.HasProperty("_BaseColor")) solidMaterial.SetColor("_BaseColor", solidColor);

                booth = new GameObject("RAC1 Booth Self-Test Root");
                booth.tag = "EditorOnly";
                Rac1BoothAuthoring authoring = booth.AddComponent<Rac1BoothAuthoring>();
                authoring.RefreshFloorGuide();
                VerifyFloorGuide(authoring);

                GameObject avatarRoot = new GameObject("Avatar Root");
                avatarRoot.transform.SetParent(booth.transform, false);
                authoring.AvatarRoot = avatarRoot.transform;
                skinnedMesh = CreateSkinnedTriangle(avatarRoot, material);

                GameObject shopRoot = new GameObject("Shop Visual Root");
                shopRoot.transform.SetParent(booth.transform, false);
                authoring.ShopVisualRoot = shopRoot.transform;
                shopMesh = CreateShopQuad(shopRoot, solidMaterial);

                Rac1BoothValidationReport captureReport = Rac1BoothBaker.Capture(authoring);
                Require(!captureReport.HasErrors, FormatErrors(captureReport));
                Require(authoring.HasCapture, "Capture did not retain a preview mesh and atlas.");
                Require(authoring.CapturedAtlas.width == Rac1BoothBaker.AtlasSize &&
                        authoring.CapturedAtlas.height == Rac1BoothBaker.AtlasSize,
                    "Capture atlas is not exactly 1024 x 1024.");
                Require(captureReport.VertexCount >= 7, "Static and skinned vertices were not both collected.");
                Require(captureReport.IndexCount == 9, "Unexpected combined index count.");
                VerifySolidColorUvRemap(authoring.CapturedMesh);

                Rac1BinaryExporter.ExportSummary summary = Rac1BoothBaker.Export(
                    authoring,
                    outputPath,
                    out Rac1BoothValidationReport exportReport);
                Require(!exportReport.HasErrors, FormatErrors(exportReport));
                Require(summary.VertexCount == exportReport.VertexCount, "Exporter vertex count differs from capture.");
                Require(summary.IndexCount == exportReport.IndexCount, "Exporter index count differs from capture.");
                Require(summary.TextureWidth == Rac1BoothBaker.AtlasSize &&
                        summary.TextureHeight == Rac1BoothBaker.AtlasSize,
                    "RAC1 texture payload is not 1024 x 1024.");
                Require(summary.FileSize <= Rac1BoothBaker.MaximumFileBytes, "RAC1 exceeds the 10 MB limit.");

                VerifyHeader(outputPath, authoring.CapturedMesh.bounds, summary);

                // A creator may deliberately collect everything under BoothRoot.
                // Both the captured preview and floor guide must remain authoring-only.
                authoring.AvatarRoot = booth.transform;
                authoring.ShopVisualRoot = null;
                Rac1BoothValidationReport boothRootReport = Rac1BoothBaker.Validate(authoring);
                Require(!boothRootReport.HasErrors, FormatErrors(boothRootReport));
                Require(boothRootReport.SourceRendererCount == captureReport.SourceRendererCount,
                    "Authoring helpers were collected when AvatarRoot was BoothRoot.");
                Require(boothRootReport.VertexCount == captureReport.VertexCount &&
                        boothRootReport.IndexCount == captureReport.IndexCount,
                    "Floor guide or captured preview changed flattened export counts.");
                authoring.AvatarRoot = avatarRoot.transform;
                authoring.ShopVisualRoot = shopRoot.transform;

                MeshRenderer shopRenderer = shopRoot.GetComponentInChildren<MeshRenderer>(true);
                Require(shopRenderer != null, "Shop self-test renderer is missing.");
                shopRenderer.sharedMaterial = material;
                authoring.BakeRepeatUvDomains = false;
                Vector2[] sourceUvBeforeFix = shopMesh.uv;
                Rac1BoothValidationReport texturedUvReport = Rac1BoothBaker.Validate(authoring);
                Require(texturedUvReport.HasErrors,
                    "A textured sub-mesh with out-of-range UV0 was not rejected.");
                Require(HasFix(texturedUvReport, Rac1BoothValidationFixKind.EnableRepeatUvBake),
                    "A safe textured Repeat UV error did not expose the structured Fix action.");

                Rac1BoothValidationReport fixedReport =
                    Rac1BoothAuthoringEditor.ApplyValidationFix(
                        authoring,
                        Rac1BoothValidationFixKind.EnableRepeatUvBake);
                Require(authoring.BakeRepeatUvDomains,
                    "Fix did not persist the Repeat UV bake policy on Booth Authoring.");
                Require(!fixedReport.HasErrors, FormatErrors(fixedReport));
                Require(!HasAnyFix(fixedReport),
                    "Repeat UV Fix remained actionable after the policy was enabled.");
                RequireUvEqual(sourceUvBeforeFix, shopMesh.uv,
                    "Fix changed the source Mesh UV0 array.");

                Rac1BoothValidationReport repeatCapture = Rac1BoothBaker.Capture(authoring);
                Require(!repeatCapture.HasErrors, FormatErrors(repeatCapture));
                VerifyRepeatedTexturedUvRemap(authoring.CapturedMesh);
                Rac1BinaryExporter.ExportSummary repeatSummary = Rac1BoothBaker.Export(
                    authoring,
                    outputPath,
                    out Rac1BoothValidationReport repeatExportReport);
                Require(!repeatExportReport.HasErrors, FormatErrors(repeatExportReport));
                Require(repeatSummary.VertexCount == repeatExportReport.VertexCount &&
                        repeatSummary.IndexCount == repeatExportReport.IndexCount,
                    "Repeat UV fixed booth did not export with validated geometry counts.");
                RequireUvEqual(sourceUvBeforeFix, shopMesh.uv,
                    "Capture/export changed the source Mesh UV0 array.");

                texture.wrapMode = TextureWrapMode.Clamp;
                Rac1BoothValidationReport clampReport = Rac1BoothBaker.Validate(authoring);
                Require(clampReport.HasErrors,
                    "Out-of-range UV0 with a Clamp texture was not rejected.");
                Require(!HasAnyFix(clampReport),
                    "Clamp UV validation incorrectly exposed a destructive/unsafe Fix.");

                texture.wrapMode = TextureWrapMode.Repeat;
                shopMesh.uv = new[]
                {
                    new Vector2(-5f, 0.004f), new Vector2(0.997f, 0.004f),
                    new Vector2(0.997f, 0.997f), new Vector2(-5f, 0.997f)
                };
                Rac1BoothValidationReport excessiveReport = Rac1BoothBaker.Validate(authoring);
                Require(excessiveReport.HasErrors,
                    "An excessive Repeat UV domain was not rejected.");
                Require(!HasAnyFix(excessiveReport),
                    "An excessive Repeat UV domain incorrectly exposed Fix.");

                Vector2[] nonFiniteUv = (Vector2[])sourceUvBeforeFix.Clone();
                nonFiniteUv[0] = new Vector2(float.NaN, nonFiniteUv[0].y);
                shopMesh.uv = nonFiniteUv;
                Rac1BoothValidationReport nonFiniteReport = Rac1BoothBaker.Validate(authoring);
                Require(nonFiniteReport.HasErrors,
                    "A non-finite UV0 was not rejected.");
                Require(!HasAnyFix(nonFiniteReport),
                    "A non-finite UV0 incorrectly exposed Fix.");

                shopMesh.uv = sourceUvBeforeFix;
                shopRenderer.sharedMaterial = solidMaterial;

                shopRoot.transform.localPosition = new Vector3(2f, 0f, 0f);
                Rac1BoothValidationReport outsideReport = Rac1BoothBaker.Validate(authoring);
                Require(outsideReport.HasErrors, "Out-of-bounds shop geometry was not rejected.");

                Debug.Log(
                    "[RAC1 Booth Self-Test] PASS. " +
                    $"{summary.VertexCount} vertices, {summary.IndexCount} indices, " +
                    $"{summary.TextureWidth}x{summary.TextureHeight}, {summary.FileSize} bytes.");
            }
            finally
            {
                if (booth != null)
                {
                    Rac1BoothAuthoring authoring = booth.GetComponent<Rac1BoothAuthoring>();
                    if (authoring != null) authoring.ReleaseCapture();
                    UnityEngine.Object.DestroyImmediate(booth);
                }
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(solidMaterial);
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(skinnedMesh);
                UnityEngine.Object.DestroyImmediate(shopMesh);
                if (File.Exists(outputPath)) File.Delete(outputPath);
            }
        }

        private static void VerifyFloorGuide(Rac1BoothAuthoring authoring)
        {
            GameObject guide = authoring.FloorGuideObject;
            Require(guide != null, "Floor guide was not created in Edit Mode.");
            Require(guide.CompareTag("EditorOnly"), "Floor guide is not tagged EditorOnly.");
            Require((guide.hideFlags & HideFlags.DontSaveInEditor) != 0 &&
                    (guide.hideFlags & HideFlags.DontSaveInBuild) != 0,
                "Floor guide is not protected from scene and player serialization.");
            Require(guide.transform.parent == authoring.transform,
                "Floor guide is not parented to BoothRoot.");
            Require(guide.transform.localPosition.sqrMagnitude < 0.0000001f &&
                    Quaternion.Angle(guide.transform.localRotation, Quaternion.identity) < 0.0001f &&
                    (guide.transform.localScale - Vector3.one).sqrMagnitude < 0.0000001f,
                "Floor guide transform is not aligned to BoothRoot.");

            Mesh mesh = authoring.FloorGuideMesh;
            Require(mesh != null, "Floor guide mesh is missing.");
            Require((mesh.hideFlags & HideFlags.DontSaveInBuild) != 0,
                "Floor guide mesh is not protected from player serialization.");
            Require(mesh.vertexCount == 4 && mesh.GetIndexCount(0) == 6,
                "Floor guide mesh is not the expected quad.");
            Bounds bounds = mesh.bounds;
            Vector3 expectedCenter = new Vector3(0f, Rac1BoothAuthoring.FloorGuideYOffset, 0f);
            Vector3 expectedSize = new Vector3(
                Rac1BoothAuthoring.ProfileWidth,
                0f,
                Rac1BoothAuthoring.ProfileDepth);
            Require((bounds.center - expectedCenter).sqrMagnitude < 0.0000001f &&
                    (bounds.size - expectedSize).sqrMagnitude < 0.0000001f,
                "Floor guide dimensions do not match the Standard-3x3-v1 profile.");

            MeshRenderer renderer = guide.GetComponent<MeshRenderer>();
            Require(renderer != null && renderer.enabled,
                "Floor guide renderer is missing or not visible by default.");
            Require(renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null &&
                    renderer.sharedMaterial.shader.name == "Hidden/Avatar Catalog/RAC1 Booth Floor Guide",
                "Floor guide is not using the dedicated grid shader.");

            authoring.ShowFloorGuide = false;
            Require(!renderer.enabled, "Show Floor Guide did not hide the guide.");
            Require(authoring.FloorGuideObject == guide,
                "Hiding the floor guide should retain its non-serialized helper object.");
            authoring.ShowFloorGuide = true;
            Require(renderer.enabled, "Show Floor Guide did not restore the guide.");
        }

        private static void VerifySolidColorUvRemap(Mesh capturedMesh)
        {
            Require(capturedMesh != null, "Captured mesh is missing for UV verification.");
            Vector3[] positions = capturedMesh.vertices;
            Vector2[] uv = capturedMesh.uv;
            Require(uv != null && uv.Length == positions.Length,
                "Captured mesh does not contain one UV0 per vertex.");

            bool found = false;
            Vector2 expected = default;
            int solidVertexCount = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                if (positions[i].z < 0.25f)
                {
                    continue;
                }

                Require(uv[i].x >= 0f && uv[i].x <= 1f && uv[i].y >= 0f && uv[i].y <= 1f,
                    "Solid-color output UV0 is outside the atlas.");
                if (!found)
                {
                    expected = uv[i];
                    found = true;
                }
                Require((uv[i] - expected).sqrMagnitude < 0.0000001f,
                    "Solid-color vertices did not sample one safe atlas point.");
                solidVertexCount++;
            }
            Require(found && solidVertexCount == 4,
                "Did not identify all four solid-color shop vertices in the captured mesh.");
        }

        private static void VerifyRepeatedTexturedUvRemap(Mesh capturedMesh)
        {
            Require(capturedMesh != null, "Captured Repeat UV mesh is missing.");
            Vector3[] positions = capturedMesh.vertices;
            Vector2[] uv = capturedMesh.uv;
            Require(uv != null && uv.Length == positions.Length,
                "Captured Repeat UV mesh does not contain one UV0 per vertex.");

            int shopVertexCount = 0;
            float minU = float.PositiveInfinity;
            float maxU = float.NegativeInfinity;
            for (int i = 0; i < positions.Length; i++)
            {
                if (positions[i].z < 0.25f)
                {
                    continue;
                }

                Require(uv[i].x >= 0f && uv[i].x <= 1f &&
                        uv[i].y >= 0f && uv[i].y <= 1f,
                    "Repeat UV output coordinate is outside the packed atlas.");
                minU = Mathf.Min(minU, uv[i].x);
                maxU = Mathf.Max(maxU, uv[i].x);
                shopVertexCount++;
            }

            Require(shopVertexCount == 4,
                "Did not identify all four Repeat UV shop vertices.");
            Require(maxU - minU > 0.2f,
                "Repeat UV output was collapsed instead of being affinely remapped.");
        }

        private static bool HasFix(
            Rac1BoothValidationReport report,
            Rac1BoothValidationFixKind fixKind)
        {
            for (int i = 0; i < report.Messages.Count; i++)
            {
                if (report.Messages[i].FixKind == fixKind)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasAnyFix(Rac1BoothValidationReport report)
        {
            for (int i = 0; i < report.Messages.Count; i++)
            {
                if (report.Messages[i].FixKind != Rac1BoothValidationFixKind.None)
                {
                    return true;
                }
            }
            return false;
        }

        private static void RequireUvEqual(Vector2[] expected, Vector2[] actual, string message)
        {
            Require(expected != null && actual != null && expected.Length == actual.Length, message);
            for (int i = 0; i < expected.Length; i++)
            {
                Require(expected[i] == actual[i], message + $" (vertex {i})");
            }
        }

        private static Mesh CreateSkinnedTriangle(GameObject avatarRoot, Material material)
        {
            GameObject boneObject = new GameObject("Pose Bone");
            boneObject.transform.SetParent(avatarRoot.transform, false);
            boneObject.transform.localPosition = new Vector3(0f, 0.4f, 0f);

            GameObject rendererObject = new GameObject("Baked Avatar Triangle");
            rendererObject.transform.SetParent(avatarRoot.transform, false);
            SkinnedMeshRenderer renderer = rendererObject.AddComponent<SkinnedMeshRenderer>();

            Mesh mesh = new Mesh { name = "Booth Self-Test Skinned Mesh" };
            mesh.vertices = new[]
            {
                new Vector3(-0.2f, 0f, 0f),
                new Vector3(0.2f, 0f, 0f),
                new Vector3(0f, 0.5f, 0f)
            };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back };
            mesh.uv = new[] { Vector2.zero, Vector2.right, new Vector2(0.5f, 1f) };
            mesh.triangles = new[] { 0, 2, 1 };
            mesh.bindposes = new[] { boneObject.transform.worldToLocalMatrix * rendererObject.transform.localToWorldMatrix };
            mesh.boneWeights = new[]
            {
                OneBoneWeight(), OneBoneWeight(), OneBoneWeight()
            };
            mesh.RecalculateBounds();

            renderer.sharedMesh = mesh;
            renderer.bones = new[] { boneObject.transform };
            renderer.rootBone = boneObject.transform;
            renderer.sharedMaterial = material;
            renderer.updateWhenOffscreen = true;
            return mesh;
        }

        private static Mesh CreateShopQuad(GameObject shopRoot, Material material)
        {
            GameObject rendererObject = new GameObject("Shop Counter Quad");
            rendererObject.transform.SetParent(shopRoot.transform, false);
            rendererObject.transform.localPosition = new Vector3(0f, 0.1f, 0.3f);
            MeshFilter filter = rendererObject.AddComponent<MeshFilter>();
            MeshRenderer renderer = rendererObject.AddComponent<MeshRenderer>();

            Mesh mesh = new Mesh { name = "Booth Self-Test Shop Mesh" };
            mesh.vertices = new[]
            {
                new Vector3(-0.35f, 0f, 0f),
                new Vector3(0.35f, 0f, 0f),
                new Vector3(0.35f, 0.4f, 0f),
                new Vector3(-0.35f, 0.4f, 0f)
            };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            // Maria-like finite U Repeat domain. A solid-color material must
            // ignore it. A textured Repeat material initially fails with Fix,
            // then bakes U -1..1 non-destructively after opt-in.
            mesh.uv = new[]
            {
                new Vector2(-0.952f, 0.004f),
                new Vector2(0.997f, 0.004f),
                new Vector2(0.997f, 0.997f),
                new Vector2(-0.952f, 0.997f)
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();

            filter.sharedMesh = mesh;
            renderer.sharedMaterial = material;
            return mesh;
        }

        private static BoneWeight OneBoneWeight()
        {
            return new BoneWeight { boneIndex0 = 0, weight0 = 1f };
        }

        private static Texture2D CreateCheckerTexture()
        {
            Texture2D texture = new Texture2D(8, 8, TextureFormat.RGBA32, false)
            {
                name = "Booth Self-Test Checker"
            };
            Color32[] pixels = new Color32[64];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    bool light = ((x + y) & 1) == 0;
                    pixels[y * 8 + x] = light
                        ? new Color32(255, 175, 30, 255)
                        : new Color32(30, 80, 255, 0);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static void VerifyHeader(
            string path,
            Bounds expectedBounds,
            Rac1BinaryExporter.ExportSummary summary)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Require(bytes.Length == summary.FileSize, "RAC1 summary size differs from file length.");
            Require(bytes.Length >= 60, "RAC1 header is truncated.");
            Require(bytes[0] == (byte)'R' && bytes[1] == (byte)'A' &&
                    bytes[2] == (byte)'C' && bytes[3] == (byte)'1',
                "RAC1 magic is missing.");

            uint vertexCount = ReadUInt32(bytes, 12);
            uint indexCount = ReadUInt32(bytes, 16);
            Require(vertexCount == summary.VertexCount, "Header vertex count is incorrect.");
            Require(indexCount == summary.IndexCount, "Header index count is incorrect.");

            Bounds headerBounds = new Bounds(
                new Vector3(ReadSingle(bytes, 20), ReadSingle(bytes, 24), ReadSingle(bytes, 28)),
                new Vector3(ReadSingle(bytes, 32), ReadSingle(bytes, 36), ReadSingle(bytes, 40)));
            Require((headerBounds.center - expectedBounds.center).sqrMagnitude < 0.0000001f,
                "RAC1 header bounds centre is not the tight combined mesh AABB.");
            Require((headerBounds.size - expectedBounds.size).sqrMagnitude < 0.0000001f,
                "RAC1 header bounds size is not the tight combined mesh AABB.");
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return BitConverter.ToUInt32(bytes, offset);
        }

        private static float ReadSingle(byte[] bytes, int offset)
        {
            return BitConverter.ToSingle(bytes, offset);
        }

        private static string FormatErrors(Rac1BoothValidationReport report)
        {
            string result = "Booth validation failed:";
            for (int i = 0; i < report.Messages.Count; i++)
            {
                if (report.Messages[i].Severity == Rac1BoothMessageSeverity.Error)
                {
                    result += "\n- " + report.Messages[i].Message;
                }
            }
            return result;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidDataException(message);
            }
        }
    }
}
