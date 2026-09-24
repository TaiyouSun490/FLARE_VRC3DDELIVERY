using System;
using System.IO;
using System.Reflection;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public static class Rac2ParticleSelfTest
    {
        [MenuItem("Tools/FLARE/Developer/Tests/Run RAC2 Particle Self-Test")]
        public static void RunMenu()
        {
            Run();
            EditorUtility.DisplayDialog("RAC2 Particle Self-Test", "PASS", "OK");
        }

        public static void Run()
        {
            Shader alphaShader = Shader.Find("Avatar Catalog/RAC2 Particle Alpha");
            Shader additiveShader = Shader.Find("Avatar Catalog/RAC2 Particle Additive");
            Shader standard = Shader.Find("Standard");
            if (alphaShader == null || additiveShader == null || standard == null)
                throw new InvalidOperationException("Required RAC2 particle shaders are unavailable.");

            Mesh mesh = CreateQuad();
            Texture2D texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            Material carrier = new Material(standard);
            Material alpha = new Material(alphaShader);
            Material additive = new Material(additiveShader);
            Material fallback = new Material(standard);
            GameObject runtime = null;
            string path = Path.Combine(Path.GetTempPath(), "rac2-particle-selftest.rac2");
            try
            {
                Color[] pixels = new Color[16];
                for (int index = 0; index < pixels.Length; index++)
                    pixels[index] = index < 8 ? Color.white : new Color(1f, 0.5f, 0.1f, 0.5f);
                texture.SetPixels(pixels);
                texture.Apply(false, false);

                var particle = new Rac2BinaryExporter.ParticleData
                {
                    Loop = true,
                    Billboard = true,
                    HideBaseMesh = true,
                    ShaderProfile = 1,
                    MaximumParticles = 4,
                    Duration = 3f,
                    Lifetime = 1.5f,
                    EmissionRate = 10f,
                    SpeedMinimum = 0.2f,
                    SpeedMaximum = 0.6f,
                    SizeMinimum = 0.1f,
                    SizeMaximum = 0.2f,
                    Gravity = 0.15f,
                    AngularSpeed = 90f,
                    Origin = new Vector3(0f, 0.5f, 0f),
                    Direction = Vector3.up,
                    Shape = 2,
                    ShapeRadius = 0.1f,
                    ShapeAngle = 20f,
                    ShapeScale = Vector3.one,
                    StartColor = Color.white,
                    EndColor = new Color(1f, 0.2f, 0.05f, 0f),
                    FlipbookColumns = 2,
                    FlipbookRows = 2,
                    Texture = texture,
                };

                Rac2BinaryExporter.ExportSummary summary = Rac2BinaryExporter.ExportMesh(
                    mesh, carrier, path, null, 1024, null, null, true, particle);
                if (!summary.HasParticles || summary.ParticleMaximum != 4 ||
                    summary.ParticleTextureWidth != 4 || summary.ParticleTextureHeight != 4 ||
                    summary.CompressedSectionCount == 0)
                    throw new InvalidDataException("Particle export summary is incorrect.");

                byte[] bytes = File.ReadAllBytes(path);
                if (ReadU32(bytes, 12) != 4u || ReadU32(bytes, 16) != 1u || ReadU32(bytes, 20) != 24u)
                    throw new InvalidDataException("Particle RAC2 header is incorrect.");
                string[] expected = { "META", "MESH", "PART", "PTEX" };
                for (int index = 0; index < expected.Length; index++)
                {
                    int toc = 24 + index * 24;
                    string type = new string(new[] { (char)bytes[toc], (char)bytes[toc + 1], (char)bytes[toc + 2], (char)bytes[toc + 3] });
                    if (type != expected[index]) throw new InvalidDataException("Particle section order is incorrect: " + type);
                }

                runtime = new GameObject("RAC2 Particle Runtime Self-Test", typeof(MeshFilter), typeof(MeshRenderer));
                GameObject playerObject = new GameObject("Particle Player");
                playerObject.transform.SetParent(runtime.transform, false);
                Rac2ParticlePlayer player = playerObject.AddUdonSharpComponent<Rac2ParticlePlayer>();
                var pool = new GameObject[4];
                for (int index = 0; index < pool.Length; index++)
                {
                    pool[index] = new GameObject("Particle " + index, typeof(MeshFilter), typeof(MeshRenderer));
                    pool[index].transform.SetParent(playerObject.transform, false);
                    pool[index].GetComponent<MeshRenderer>().enabled = false;
                }
                player.PoolObjects = pool;
                player.AlphaTemplate = alpha;
                player.AdditiveTemplate = additive;

                Rac2RuntimeLoader loader = runtime.AddUdonSharpComponent<Rac2RuntimeLoader>();
                loader.TargetMeshFilter = runtime.GetComponent<MeshFilter>();
                loader.TargetRenderer = runtime.GetComponent<MeshRenderer>();
                loader.MaterialTemplate = fallback;
                loader.ParticlePlayer = player;
                loader.EnforceStandardBoothProfile = true;

                MethodInfo parse = typeof(Rac2RuntimeLoader).GetMethod("ParseAndApply", BindingFlags.Instance | BindingFlags.NonPublic);
                if (parse == null || !(bool)parse.Invoke(loader, new object[] { bytes }))
                    throw new InvalidDataException("Runtime loader rejected the particle RAC2.");
                player.SimulateStep(0.2f);
                if (!loader.LoadedHasParticles || loader.LoadedParticleMaximum != 4 ||
                    !loader.LoadedWasCompressed || !player.IsConfigured || player.ActiveParticleCount < 1 ||
                    loader.TargetRenderer.enabled || !pool[0].GetComponent<MeshRenderer>().enabled ||
                    pool[0].GetComponent<MeshRenderer>().sharedMaterial.GetTexture("_MainTex") == null)
                    throw new InvalidDataException("Particle runtime reconstruction is incomplete.");

                loader.Clear();
                if (player.IsConfigured || player.ActiveParticleCount != 0)
                    throw new InvalidDataException("Particle pool did not clear.");

                Debug.Log("[RAC2 Particle Self-Test] PART+PTEX / LZ4 / Additive / Billboard / Flipbook / runtime pool PASS");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (runtime != null) UnityEngine.Object.DestroyImmediate(runtime);
                UnityEngine.Object.DestroyImmediate(fallback);
                UnityEngine.Object.DestroyImmediate(additive);
                UnityEngine.Object.DestroyImmediate(alpha);
                UnityEngine.Object.DestroyImmediate(carrier);
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        private static Mesh CreateQuad()
        {
            Mesh mesh = new Mesh { name = "RAC2 Particle Self-Test Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static uint ReadU32(byte[] data, int offset)
        {
            return (uint)data[offset] | ((uint)data[offset + 1] << 8) |
                   ((uint)data[offset + 2] << 16) | ((uint)data[offset + 3] << 24);
        }
    }
}
