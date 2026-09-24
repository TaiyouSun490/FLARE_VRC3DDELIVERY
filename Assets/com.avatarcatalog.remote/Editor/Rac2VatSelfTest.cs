using System;
using System.IO;
using System.Reflection;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public static class Rac2VatSelfTest
    {
        private const string RequestPath = "Library/NightSlotRac2Vat.selftest";
        private const string ResultPath = "Library/NightSlotRac2Vat.selftest.result";

        [InitializeOnLoadMethod]
        private static void RunWhenRequested()
        {
            string request = Path.GetFullPath(RequestPath);
            if (!File.Exists(request)) return;
            EditorApplication.delayCall += () =>
            {
                File.Delete(request);
                try
                {
                    Run();
                    File.WriteAllText(ResultPath, "PASS");
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    File.WriteAllText(ResultPath, "FAIL: " + exception);
                }
            };
        }

        [MenuItem("Tools/FLARE/Developer/Tests/Run RAC2 VAT Self-Test")]
        public static void RunMenu()
        {
            Run();
            EditorUtility.DisplayDialog("RAC2 VAT Self-Test", "PASS", "OK");
        }

        public static void RunFromCommandLine()
        {
            try
            {
                Rac2ParticlePadInstaller.EnsureParticleProgram();
                Run();
                Rac2LilToonSelfTest.Run();
                Rac2ParticleSelfTest.Run();
                Rac2InteractionSelfTest.Run();
                Rac2ProductSelfTest.Run();
                Rac2ManagedCatalogSelfTest.Run();
                Debug.Log("[RAC2 Full Suite] PASS");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                Debug.LogError("[RAC2 VAT Self-Test] FAIL");
                EditorApplication.Exit(1);
            }
        }

        public static void Run()
        {
            Shader lilToon = Shader.Find("lilToon");
            Shader vatOpaque = Shader.Find("Avatar Catalog/RAC2 VAT Opaque");
            Shader vatCutout = Shader.Find("Avatar Catalog/RAC2 VAT Cutout");
            if (lilToon == null || vatOpaque == null || vatCutout == null)
                throw new InvalidOperationException("Required lilToon or VAT shader is unavailable.");

            Mesh mesh = new Mesh { name = "RAC2 VAT Self-Test Mesh" };
            Material source = new Material(lilToon);
            Material opaque = new Material(vatOpaque);
            Material cutout = new Material(vatCutout);
            GameObject runtimeObject = null;
            string path = Path.Combine(Path.GetTempPath(), "rac2-vat-selftest.rac2");
            try
            {
                mesh.vertices = new[] { new Vector3(-.5f, 0f, 0f), new Vector3(.5f, 0f, 0f), new Vector3(0f, 1f, 0f) };
                mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back };
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
                mesh.triangles = new[] { 0, 2, 1 };
                mesh.RecalculateBounds();

                Vector3[][] positions = new Vector3[3][];
                Vector3[][] normals = new Vector3[3][];
                for (int frame = 0; frame < 3; frame++)
                {
                    float offset = frame * .1f;
                    positions[frame] = new[]
                    {
                        new Vector3(-.5f, offset, 0f),
                        new Vector3(.5f, offset, 0f),
                        new Vector3(0f, 1f + offset, 0f),
                    };
                    normals[frame] = new[] { Vector3.back, Vector3.back, Vector3.back };
                }

                source.SetFloat("_TransparentMode", 0f);
                var vat = new Rac2BinaryExporter.VatClipData
                {
                    Name = "SelfTestLoop",
                    FramesPerSecond = 12f,
                    Loop = true,
                    Positions = positions,
                    Normals = normals,
                };
                Rac2BinaryExporter.ExportSummary summary = Rac2BinaryExporter.ExportMesh(mesh, source, path, null, 1024, null, vat);
                if (summary.VatFrameCount != 3 || summary.VatTextureWidth != 4 || !summary.VatHasNormals ||
                    (summary.Attributes & Rac2BinaryExporter.MeshAttributes.Uv1) == 0 ||
                    summary.CompressedSectionCount == 0 || summary.FileSize >= summary.UncompressedFileSize)
                    throw new InvalidDataException("VAT export summary is incorrect.");

                byte[] bytes = File.ReadAllBytes(path);
                if (ReadU32(bytes, 0) != 0x32434152u || ReadU32(bytes, 4) != 2u || ReadU32(bytes, 12) != 6u ||
                    ReadU32(bytes, 16) != 1u || ReadU32(bytes, 20) != 24u)
                    throw new InvalidDataException("VAT RAC2 compressed header is incorrect.");
                string[] expected = { "META", "MESH", "MATL", "VATI", "VATP", "VATN" };
                for (int index = 0; index < expected.Length; index++)
                {
                    int toc = 24 + index * 24;
                    string type = new string(new[] { (char)bytes[toc], (char)bytes[toc + 1], (char)bytes[toc + 2], (char)bytes[toc + 3] });
                    if (type != expected[index]) throw new InvalidDataException("VAT section order is incorrect: " + type);
                }

                runtimeObject = new GameObject("RAC2 VAT Runtime Self-Test", typeof(MeshFilter), typeof(MeshRenderer));
                Rac2RuntimeLoader loader = runtimeObject.AddUdonSharpComponent<Rac2RuntimeLoader>();
                loader.TargetMeshFilter = runtimeObject.GetComponent<MeshFilter>();
                loader.TargetRenderer = runtimeObject.GetComponent<MeshRenderer>();
                loader.VatOpaqueTemplate = opaque;
                loader.VatCutoutTemplate = cutout;
                loader.EnforceStandardBoothProfile = false;
                MethodInfo parse = typeof(Rac2RuntimeLoader).GetMethod("ParseAndApply", BindingFlags.Instance | BindingFlags.NonPublic);
                if (parse == null) throw new InvalidOperationException("Runtime parser reflection failed.");
                byte[] corrupted = (byte[])bytes.Clone();
                corrupted[corrupted.Length - 1] ^= 0x5a;
                if ((bool)parse.Invoke(loader, new object[] { corrupted }))
                    throw new InvalidDataException("Runtime loader accepted a corrupted compressed RAC2.");

                if (!(bool)parse.Invoke(loader, new object[] { bytes }) ||
                    !loader.LoadedWasCompressed || loader.LoadedStoredBytes != bytes.Length ||
                    loader.LoadedDecodedBytes != summary.UncompressedFileSize ||
                    !loader.LoadedHasVat || loader.LoadedVatFrameCount != 3 ||
                    Mathf.Abs(loader.LoadedVatFramesPerSecond - 12f) > .001f ||
                    loader.TargetMeshFilter.sharedMesh == null || !loader.TargetRenderer.enabled ||
                    loader.TargetRenderer.sharedMaterial == null ||
                    loader.TargetRenderer.sharedMaterial.GetTexture("_VatPositionTex") == null)
                    throw new InvalidDataException("Runtime loader did not reconstruct VAT.");

                Debug.Log("[RAC2 VAT Self-Test] LZ4+Adler32 / corruption rejection / 3 frames / RGBAHalf positions / RGBA32 normals / runtime PASS");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (runtimeObject != null) UnityEngine.Object.DestroyImmediate(runtimeObject);
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(opaque);
                UnityEngine.Object.DestroyImmediate(cutout);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        private static uint ReadU32(byte[] data, int offset)
        {
            return (uint)data[offset] | ((uint)data[offset + 1] << 8) |
                   ((uint)data[offset + 2] << 16) | ((uint)data[offset + 3] << 24);
        }
    }
}
