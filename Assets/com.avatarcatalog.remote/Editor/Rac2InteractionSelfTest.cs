using System;
using System.IO;
using System.Reflection;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Components;

namespace AvatarCatalog.Remote
{
    public static class Rac2InteractionSelfTest
    {
        [MenuItem("Tools/FLARE/Developer/Tests/Run RAC2 Interaction Self-Test")]
        public static void RunMenu()
        {
            Run();
            EditorUtility.DisplayDialog("RAC2 Interaction Self-Test", "PASS", "OK");
        }

        public static void Run()
        {
            Shader standard = Shader.Find("Standard");
            if (standard == null) throw new InvalidOperationException("Standard shader is unavailable.");
            Mesh mesh = CreateCube();
            Material source = new Material(standard);
            Material fallback = new Material(standard);
            GameObject runtime = null;
            string portablePath = Path.Combine(Path.GetTempPath(), "rac2-interaction-portable.rac2");
            string legacyPath = Path.Combine(Path.GetTempPath(), "rac2-interaction-legacy.rac2");
            try
            {
                var interaction = new Rac2BinaryExporter.InteractionData
                {
                    HasCollider = true,
                    IsPortable = true,
                };
                Rac2BinaryExporter.ExportSummary summary = Rac2BinaryExporter.ExportMesh(
                    mesh, source, portablePath, interaction: interaction);
                if (!summary.HasCollider || !summary.IsPortable)
                    throw new InvalidDataException("Interaction export summary is incorrect.");

                byte[] portable = File.ReadAllBytes(portablePath);
                if (ReadU32(portable, 12) != 3u)
                    throw new InvalidDataException("Interaction RAC2 section count is incorrect.");
                int intrToc = 24 + 2 * 24;
                if (portable[intrToc] != (byte)'I' || portable[intrToc + 1] != (byte)'N' ||
                    portable[intrToc + 2] != (byte)'T' || portable[intrToc + 3] != (byte)'R')
                    throw new InvalidDataException("INTR is missing or out of order.");

                runtime = new GameObject("RAC2 Interaction Runtime Self-Test",
                    typeof(MeshFilter), typeof(MeshRenderer), typeof(BoxCollider), typeof(Rigidbody));
                VRCPickup pickup = runtime.AddComponent<VRCPickup>();
                Rac2RuntimeLoader loader = runtime.AddUdonSharpComponent<Rac2RuntimeLoader>();
                loader.TargetMeshFilter = runtime.GetComponent<MeshFilter>();
                loader.TargetRenderer = runtime.GetComponent<MeshRenderer>();
                loader.TargetCollider = runtime.GetComponent<BoxCollider>();
                loader.TargetRigidbody = runtime.GetComponent<Rigidbody>();
                loader.TargetPickup = pickup;
                loader.MaterialTemplate = fallback;
                loader.EnforceStandardBoothProfile = false;
                loader.InitialLocalPosition = new Vector3(1f, 2f, 3f);
                loader.InitialLocalEulerAngles = new Vector3(0f, 25f, 0f);

                MethodInfo parse = typeof(Rac2RuntimeLoader).GetMethod(
                    "ParseAndApply", BindingFlags.Instance | BindingFlags.NonPublic);
                if (parse == null) throw new MissingMethodException("Rac2RuntimeLoader.ParseAndApply");

                runtime.transform.localPosition = new Vector3(9f, 8f, 7f);
                runtime.transform.localRotation = Quaternion.Euler(40f, 30f, 20f);
                loader.TargetRigidbody.velocity = new Vector3(4f, 5f, 6f);
                if (!(bool)parse.Invoke(loader, new object[] { portable }))
                    throw new InvalidDataException("Runtime loader rejected portable INTR.");
                AssertPortable(loader, pickup, runtime);

                runtime.transform.localPosition = new Vector3(-4f, 6f, 8f);
                loader.TargetRigidbody.velocity = Vector3.one * 7f;
                if (!(bool)parse.Invoke(loader, new object[] { portable }))
                    throw new InvalidDataException("Runtime loader rejected INTR on reload.");
                AssertPortable(loader, pickup, runtime);

                Rac2BinaryExporter.ExportMesh(mesh, source, legacyPath);
                byte[] legacy = File.ReadAllBytes(legacyPath);
                if (!(bool)parse.Invoke(loader, new object[] { legacy }))
                    throw new InvalidDataException("Runtime loader rejected legacy RAC2 after INTR.");
                if (loader.LoadedHasCollider || loader.LoadedIsPortable || loader.TargetCollider.enabled ||
                    pickup.pickupable || !loader.TargetRigidbody.isKinematic)
                    throw new InvalidDataException("Legacy RAC2 did not disable interaction.");

                Debug.Log("[RAC2 Interaction Self-Test] INTR / portable pickup / BoxCollider / reload pose reset / legacy fixed PASS");
            }
            finally
            {
                if (File.Exists(portablePath)) File.Delete(portablePath);
                if (File.Exists(legacyPath)) File.Delete(legacyPath);
                if (runtime != null) UnityEngine.Object.DestroyImmediate(runtime);
                UnityEngine.Object.DestroyImmediate(fallback);
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        private static void AssertPortable(Rac2RuntimeLoader loader, VRCPickup pickup, GameObject runtime)
        {
            if (!loader.LoadedHasCollider || !loader.LoadedIsPortable ||
                !loader.TargetCollider.enabled || !pickup.pickupable || loader.TargetRigidbody.isKinematic ||
                loader.TargetRigidbody.velocity.sqrMagnitude > 0.000001f ||
                Vector3.Distance(runtime.transform.localPosition, loader.InitialLocalPosition) > 0.0001f ||
                Quaternion.Angle(runtime.transform.localRotation, Quaternion.Euler(loader.InitialLocalEulerAngles)) > 0.01f)
                throw new InvalidDataException("Portable interaction or reload pose reset is incomplete.");
        }

        private static Mesh CreateCube()
        {
            GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh copy = UnityEngine.Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
            copy.name = "RAC2 Interaction Self-Test Cube";
            UnityEngine.Object.DestroyImmediate(primitive);
            return copy;
        }

        private static uint ReadU32(byte[] data, int offset)
        {
            return (uint)data[offset] | ((uint)data[offset + 1] << 8) |
                   ((uint)data[offset + 2] << 16) | ((uint)data[offset + 3] << 24);
        }
    }
}
