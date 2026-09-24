using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    /// <summary>Builds the clean RAC2 v0.1 sample prefab without modifying a scene.</summary>
    public static class Rac2DistributionPrefabBuilder
    {
        private const string MenuPath = "Tools/FLARE/Developer/Build/Build Legacy RAC2 v0.1 Sample Prefab";
        private const string RequestPath = "Library/BuildRac2DistributionPrefab.request";
        private const string SourcePrefabPath = "Assets/NightSlotMall/Prefabs/RAC-3D-ImagePad.prefab";
        private const string OutputFolder = "Assets/RemoteAvatarCatalogDistribution";
        private const string OutputPrefabPath = OutputFolder + "/RAC2-3D-ImagePad.prefab";

        [InitializeOnLoadMethod]
        private static void BuildWhenRequested()
        {
            string request = Path.GetFullPath(RequestPath);
            if (!File.Exists(request)) return;
            EditorApplication.delayCall += () =>
            {
                try
                {
                    File.Delete(request);
                    Build();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            };
        }

        [MenuItem(MenuPath)]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before building the RAC2 sample prefab.");
            if (AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefabPath) == null)
                throw new InvalidOperationException("The verified RAC2 ImagePad source prefab is missing: " + SourcePrefabPath);

            EnsureFolder(OutputFolder);
            GameObject root = PrefabUtility.LoadPrefabContents(SourcePrefabPath);
            try
            {
                Transform legacy = root.transform.Find("Legacy RAC1 Display (disabled)");
                if (legacy != null) UnityEngine.Object.DestroyImmediate(legacy.gameObject);
                Transform older = root.transform.Find("Runtime Display");
                if (older != null) UnityEngine.Object.DestroyImmediate(older.gameObject);

                root.name = "RAC2 3D ImagePad v0.1";
                root.transform.localPosition = Vector3.zero;
                root.transform.localRotation = Quaternion.identity;
                root.transform.localScale = Vector3.one;

                Rac2RuntimeLoader[] loaders = root.GetComponentsInChildren<Rac2RuntimeLoader>(true);
                RuntimeRacImagePadController[] controllers = root.GetComponentsInChildren<RuntimeRacImagePadController>(true);
                if (loaders.Length != 1 || controllers.Length != 1)
                    throw new InvalidOperationException("The sample must contain exactly one RAC2 loader and one ImagePad controller.");
                if (root.GetComponentsInChildren<RemoteAvatarCatalogLoader>(true).Length != 0)
                    throw new InvalidOperationException("The distribution sample still contains the legacy RAC1 loader.");

                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, OutputPrefabPath);
                if (saved == null) throw new InvalidOperationException("Unity failed to save the RAC2 sample prefab.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
            string[] dependencies = AssetDatabase.GetDependencies(OutputPrefabPath, true);
            if (dependencies.Any(path => path.EndsWith("RemoteAvatarCatalogLoader.asset", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The RAC2 sample unexpectedly depends on the legacy RAC1 program asset.");

            Debug.Log("[RAC2 Distribution] PASS - clean prefab created at " + OutputPrefabPath +
                      " with " + dependencies.Length + " dependencies.");
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
