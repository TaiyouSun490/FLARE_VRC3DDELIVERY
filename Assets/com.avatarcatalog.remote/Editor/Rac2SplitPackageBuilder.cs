using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Components;

namespace AvatarCatalog.Remote
{
    /// <summary>Publishes ImagePad and Pedestal Gimmick as separate release artifacts.</summary>
    public static class Rac2SplitPackageBuilder
    {
        private const string Root = "Assets/RemoteAvatarCatalogDistribution";
        private const string ImagePad = Root + "/Prefabs/RAC2-ImagePad-v0.1.prefab";
        private const string Integrated = Root + "/Prefabs/RAC2-ImagePad-Pedestal-v0.1.prefab";
        private const string StandaloneSource = Root + "/Prefabs/RAC2-Product-Pedestal-v0.1.prefab";
        private const string GimmickFolder = Root + "/Gimmicks";
        private const string Gimmick = GimmickFolder + "/RAC2-Pedestal-Gimmick-v0.1.prefab";
        private const string Dependencies = Root + "/DEPENDENCIES-JA.md";
        private const string ImagePadReadme = Root + "/README-JA.md";
        private const string GimmickReadme = Root + "/PEDESTAL-GIMMICK-JA.md";
        private const string ImagePadBuild = "Builds/RemoteAvatarCatalog-ImagePad-0.1.0/RemoteAvatarCatalog-ImagePad-0.1.0.unitypackage";
        private const string GimmickBuild = "Builds/RAC2-Pedestal-Gimmick-0.1.0/RAC2-Pedestal-Gimmick-0.1.0.unitypackage";

        [MenuItem("Tools/Avatar Catalog/Developer/Build/Pack Split Legacy RAC2 Releases")]
        public static void Build()
        {
            Require<GameObject>(ImagePad);
            Require<GameObject>(Integrated);
            Require<GameObject>(StandaloneSource);
            Require<TextAsset>(Dependencies);
            Require<TextAsset>(ImagePadReadme);
            Require<TextAsset>(GimmickReadme);
            EnsureFolder(GimmickFolder);

            GameObject root = PrefabUtility.LoadPrefabContents(StandaloneSource);
            try
            {
                root.name = "RAC2 Pedestal Gimmick v0.1";
                root.transform.localPosition = Vector3.zero;
                root.transform.localRotation = Quaternion.identity;
                root.transform.localScale = Vector3.one;
                if (PrefabUtility.SaveAsPrefabAsset(root, Gimmick) == null)
                    throw new InvalidOperationException("Failed to save the standalone Pedestal Gimmick prefab.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            AssetDatabase.SaveAssets();
            ValidateGimmick();

            Directory.CreateDirectory(Path.GetFullPath(Path.GetDirectoryName(ImagePadBuild)));
            Directory.CreateDirectory(Path.GetFullPath(Path.GetDirectoryName(GimmickBuild)));
            AssetDatabase.ExportPackage(
                new[] { ImagePad, ImagePadReadme, Dependencies },
                ImagePadBuild,
                ExportPackageOptions.IncludeDependencies);
            AssetDatabase.ExportPackage(
                new[] { Gimmick, Integrated, GimmickReadme, Dependencies },
                GimmickBuild,
                ExportPackageOptions.IncludeDependencies);
            Debug.Log("[RAC2 Split Release] PASS - ImagePad and Pedestal Gimmick were packed separately.");
        }

        private static void ValidateGimmick()
        {
            GameObject value = Require<GameObject>(Gimmick);
            Rac2ProductLoader loader = value.GetComponentInChildren<Rac2ProductLoader>(true);
            Rac2ProductController controller = value.GetComponentInChildren<Rac2ProductController>(true);
            if (loader == null || controller == null || loader.ProductController != controller ||
                value.GetComponentsInChildren<VRCAvatarPedestal>(true).Length != 1 ||
                value.GetComponentsInChildren<Rac2RuntimeLoader>(true).Length != 0)
                throw new InvalidOperationException("Pedestal Gimmick is not a self-contained product-only prefab.");
        }

        private static T Require<T>(string path) where T : UnityEngine.Object
        {
            T value = AssetDatabase.LoadAssetAtPath<T>(path);
            if (value == null) throw new InvalidOperationException("Required release asset is missing: " + path);
            return value;
        }

        private static void EnsureFolder(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}