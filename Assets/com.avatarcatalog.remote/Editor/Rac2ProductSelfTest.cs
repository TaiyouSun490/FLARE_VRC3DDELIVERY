using System;
using System.IO;
using System.Reflection;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public static class Rac2ProductSelfTest
    {
        [MenuItem("Tools/FLARE/Developer/Tests/Run RAC2 Product Self-Test")]
        public static void RunMenu()
        {
            Run();
            EditorUtility.DisplayDialog("RAC2 Product Self-Test", "PASS", "OK");
        }

        public static void Run()
        {
            Shader standard = Shader.Find("Standard");
            if (standard == null) throw new InvalidOperationException("Standard shader is unavailable.");
            GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh mesh = UnityEngine.Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
            UnityEngine.Object.DestroyImmediate(primitive);
            Material source = new Material(standard);
            Material fallback = new Material(standard);
            GameObject imageRuntime = null;
            GameObject pedestalRuntime = null;
            string path = Path.Combine(Path.GetTempPath(), "rac2-product-selftest.rac2");
            const string avatarId = "avtr_00000000-0000-0000-0000-000000000000";
            try
            {
                var product = new Rac2BinaryExporter.ProductData
                {
                    ProductName = "試着商品",
                    CreatorName = "Remote Creator",
                    ProductUrl = "https://example.com/products/rac2",
                    AvatarBlueprintId = avatarId,
                    TrialEnabled = true,
                };
                Rac2BinaryExporter.ExportSummary summary = Rac2BinaryExporter.ExportMesh(mesh, source, path, product: product);
                if (!summary.HasProduct || !summary.TrialEnabled)
                    throw new InvalidDataException("Product export summary is incorrect.");
                byte[] bytes = File.ReadAllBytes(path);
                int count = (int)ReadU32(bytes, 12);
                int tocSize = ReadU32(bytes, 16) == 1u ? 24 : 16;
                int prodToc = 24 + (count - 1) * tocSize;
                if (bytes[prodToc] != (byte)'P' || bytes[prodToc + 1] != (byte)'R' ||
                    bytes[prodToc + 2] != (byte)'O' || bytes[prodToc + 3] != (byte)'D')
                    throw new InvalidDataException("PROD is missing or is not the final canonical section.");

                imageRuntime = new GameObject("RAC2 Product ImagePad Self-Test", typeof(MeshFilter), typeof(MeshRenderer));
                Rac2RuntimeLoader imageLoader = imageRuntime.AddUdonSharpComponent<Rac2RuntimeLoader>();
                imageLoader.TargetMeshFilter = imageRuntime.GetComponent<MeshFilter>();
                imageLoader.TargetRenderer = imageRuntime.GetComponent<MeshRenderer>();
                imageLoader.MaterialTemplate = fallback;
                imageLoader.EnforceStandardBoothProfile = false;
                MethodInfo imageParse = typeof(Rac2RuntimeLoader).GetMethod("ParseAndApply", BindingFlags.Instance | BindingFlags.NonPublic);
                if (imageParse == null || !(bool)imageParse.Invoke(imageLoader, new object[] { bytes }))
                    throw new InvalidDataException("ImagePad runtime rejected PROD metadata.");
                AssertProduct(imageLoader.LoadedHasProduct, imageLoader.LoadedProductName, imageLoader.LoadedCreatorName,
                    imageLoader.LoadedProductUrl, imageLoader.LoadedAvatarBlueprintId, imageLoader.LoadedTrialEnabled, avatarId);

                pedestalRuntime = new GameObject("RAC2 Product Pedestal Self-Test");
                Rac2ProductController controller = pedestalRuntime.AddUdonSharpComponent<Rac2ProductController>();
                Rac2ProductLoader productLoader = pedestalRuntime.AddUdonSharpComponent<Rac2ProductLoader>();
                productLoader.ProductController = controller;
                MethodInfo productParse = typeof(Rac2ProductLoader).GetMethod("ParseAndApply", BindingFlags.Instance | BindingFlags.NonPublic);
                if (productParse == null || !(bool)productParse.Invoke(productLoader, new object[] { bytes }))
                    throw new InvalidDataException("Standalone pedestal loader rejected PROD metadata.");
                AssertProduct(true, productLoader.LoadedProductName, productLoader.LoadedCreatorName,
                    productLoader.LoadedProductUrl, productLoader.LoadedAvatarBlueprintId, productLoader.LoadedTrialEnabled, avatarId);

                bool rejected = false;
                try
                {
                    Rac2BinaryExporter.ExportMesh(mesh, source, path, product: new Rac2BinaryExporter.ProductData
                    {
                        ProductName = "Invalid trial",
                        AvatarBlueprintId = "avtr_invalid",
                        TrialEnabled = true,
                    });
                }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new InvalidDataException("Exporter accepted an invalid trial Avatar Blueprint ID.");
                Debug.Log("[RAC2 Product Self-Test] PROD UTF-8 / HTTPS / Avatar ID / compressed ImagePad + standalone pedestal PASS");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (imageRuntime != null) UnityEngine.Object.DestroyImmediate(imageRuntime);
                if (pedestalRuntime != null) UnityEngine.Object.DestroyImmediate(pedestalRuntime);
                UnityEngine.Object.DestroyImmediate(fallback);
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        private static void AssertProduct(bool hasProduct, string name, string creator, string url, string avatar, bool trial, string expectedAvatar)
        {
            if (!hasProduct || name != "試着商品" || creator != "Remote Creator" ||
                url != "https://example.com/products/rac2" || avatar != expectedAvatar || !trial)
                throw new InvalidDataException("PROD metadata did not round-trip exactly.");
        }

        private static uint ReadU32(byte[] data, int offset)
        {
            return (uint)data[offset] | (uint)data[offset + 1] << 8 |
                   (uint)data[offset + 2] << 16 | (uint)data[offset + 3] << 24;
        }
    }
}
