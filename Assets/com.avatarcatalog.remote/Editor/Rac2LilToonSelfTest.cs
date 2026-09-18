using System;
using System.IO;
using System.Reflection;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public static class Rac2LilToonSelfTest
    {
        [MenuItem("Tools/Avatar Catalog/Developer/Tests/Run RAC2 lilToon Self-Test")]
        public static void RunMenu()
        {
            try
            {
                Run();
                EditorUtility.DisplayDialog("RAC2 lilToon Self-Test", "PASS", "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("RAC2 lilToon Self-Test", "FAIL: " + exception.Message, "OK");
            }
        }

        public static void RunFromCommandLine()
        {
            try
            {
                Run();
                Debug.Log("[RAC2 lilToon Self-Test] PASS");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Debug.LogError("[RAC2 lilToon Self-Test] FAIL");
                EditorApplication.Exit(1);
            }
        }

        public static void Run()
        {
            Shader shader = Shader.Find("lilToon");
            if (shader == null) throw new InvalidOperationException("lilToon shader is unavailable.");

            Mesh mesh = new Mesh { name = "RAC2 lilToon Self-Test Mesh" };
            Texture2D main = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Texture2D normal = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            Material material = new Material(shader);
            Material legacyMaterial = null;
            Material opaqueTemplate = null;
            Material cutoutTemplate = null;
            GameObject runtimeObject = null;
            string path = Path.Combine(Path.GetTempPath(), "rac2-liltoon-v01-selftest.rac2");
            string legacyPath = Path.Combine(Path.GetTempPath(), "rac2-legacy-selftest.rac2");
            try
            {
                mesh.vertices = new[]
                {
                    new Vector3(-0.5f, 0f, 0f),
                    new Vector3(0.5f, 0f, 0f),
                    new Vector3(0f, 1f, 0f),
                };
                mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
                mesh.tangents = new[]
                {
                    new Vector4(1f, 0f, 0f, -1f),
                    new Vector4(1f, 0f, 0f, -1f),
                    new Vector4(1f, 0f, 0f, -1f),
                };
                mesh.triangles = new[] { 0, 2, 1 };
                mesh.RecalculateBounds();

                main.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white });
                main.Apply(false, false);
                Color flat = new Color(0.5f, 0.5f, 1f, 1f);
                normal.SetPixels(new[] { flat, flat, flat, flat });
                normal.Apply(false, false);

                material.SetColor("_Color", new Color(0.8f, 0.7f, 0.6f, 1f));
                material.SetTexture("_MainTex", main);
                material.SetFloat("_TransparentMode", 1f);
                material.SetFloat("_Cutoff", 0.42f);
                material.SetFloat("_UseBumpMap", 1f);
                material.SetFloat("_BumpScale", 0.75f);
                material.SetTexture("_BumpMap", normal);

                Rac2BinaryExporter.ExportSummary summary = Rac2BinaryExporter.ExportMesh(mesh, material, path);
                if (summary.Profile != Rac2BinaryExporter.MaterialProfile.LilToon ||
                    summary.NormalTextureWidth != 2 || summary.NormalTextureHeight != 2 ||
                    (summary.Attributes & Rac2BinaryExporter.MeshAttributes.Tangents) == 0)
                {
                    throw new InvalidDataException("Exporter summary does not describe the lilToon profile.");
                }

                byte[] bytes = File.ReadAllBytes(path);
                if (ReadU32(bytes, 0) != 0x32434152u || ReadU32(bytes, 4) != 2u || ReadU32(bytes, 12) != 5u ||
                    ReadU32(bytes, 16) != 1u || ReadU32(bytes, 20) != 24u)
                {
                    throw new InvalidDataException("RAC2 compressed header is incorrect.");
                }

                string[] expected = { "META", "MESH", "MATL", "TEX0", "TEXN" };
                for (int index = 0; index < expected.Length; index++)
                {
                    int toc = 24 + index * 24;
                    string type = new string(new[]
                    {
                        (char)bytes[toc],
                        (char)bytes[toc + 1],
                        (char)bytes[toc + 2],
                        (char)bytes[toc + 3],
                    });
                    if (type != expected[index]) throw new InvalidDataException("Unexpected RAC2 section order.");
                }

                int materialToc = 24 + 2 * 24;
                if (ReadU32(bytes, materialToc + 12) != 48u ||
                    ReadU32(bytes, materialToc + 16) > 1u ||
                    ReadU32(bytes, materialToc + 20) == 0u)
                {
                    throw new InvalidDataException("RAC2 compressed MATL directory is incorrect.");
                }

                Shader cutoutShader = Shader.Find("Hidden/lilToonCutout");
                if (cutoutShader == null) throw new InvalidOperationException("lilToon Cutout shader is unavailable.");
                opaqueTemplate = new Material(shader);
                cutoutTemplate = new Material(cutoutShader);
                legacyMaterial = new Material(Shader.Find("Standard"));
                runtimeObject = new GameObject("RAC2 Runtime Parser Self-Test", typeof(MeshFilter), typeof(MeshRenderer));
                Rac2RuntimeLoader loader = runtimeObject.AddUdonSharpComponent<Rac2RuntimeLoader>();
                loader.TargetMeshFilter = runtimeObject.GetComponent<MeshFilter>();
                loader.TargetRenderer = runtimeObject.GetComponent<MeshRenderer>();
                loader.MaterialTemplate = legacyMaterial;
                loader.LilToonOpaqueTemplate = opaqueTemplate;
                loader.LilToonCutoutTemplate = cutoutTemplate;
                MethodInfo parse = typeof(Rac2RuntimeLoader).GetMethod("ParseAndApply", BindingFlags.Instance | BindingFlags.NonPublic);
                if (parse == null || !(bool)parse.Invoke(loader, new object[] { bytes }) ||
                    loader.LoadedMaterialProfile != 1 || !loader.LoadedHasTexture || !loader.LoadedHasNormalMap ||
                    loader.TargetMeshFilter.sharedMesh == null || !loader.TargetRenderer.enabled)
                {
                    throw new InvalidDataException("Runtime loader did not reconstruct the lilToon RAC2.");
                }

                loader.Clear();
                mesh.tangents = new Vector4[0];
                Rac2BinaryExporter.ExportMesh(mesh, legacyMaterial, legacyPath, main, 1024, null, null, false);
                byte[] legacyBytes = File.ReadAllBytes(legacyPath);
                if (ReadU32(legacyBytes, 12) != 3u || ReadU32(legacyBytes, 16) != 0u ||
                    !(bool)parse.Invoke(loader, new object[] { legacyBytes }) || loader.LoadedWasCompressed ||
                    loader.LoadedMaterialProfile != 0 || loader.LoadedHasNormalMap)
                {
                    throw new InvalidDataException("Runtime loader lost legacy RAC2 compatibility.");
                }

                Debug.Log("[RAC2 lilToon Self-Test] " + bytes.Length + " bytes / compressed MATL+Main+Normal / tangent / runtime / raw legacy PASS");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(legacyPath)) File.Delete(legacyPath);
                if (runtimeObject != null) UnityEngine.Object.DestroyImmediate(runtimeObject);
                if (legacyMaterial != null) UnityEngine.Object.DestroyImmediate(legacyMaterial);
                if (opaqueTemplate != null) UnityEngine.Object.DestroyImmediate(opaqueTemplate);
                if (cutoutTemplate != null) UnityEngine.Object.DestroyImmediate(cutoutTemplate);
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(main);
                UnityEngine.Object.DestroyImmediate(normal);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        private static uint ReadU32(byte[] data, int offset)
        {
            return (uint)data[offset] |
                   ((uint)data[offset + 1] << 8) |
                   ((uint)data[offset + 2] << 16) |
                   ((uint)data[offset + 3] << 24);
        }
    }
}
