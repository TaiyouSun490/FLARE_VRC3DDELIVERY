using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public static class Rac2ManagedCatalogReleaseBuilder
    {
        private const string Prefab = "Assets/RemoteAvatarCatalogDistribution/Gimmicks/RAC2-Managed-Avatar-Catalog-v0.1.prefab";
        private const string Readme = "Assets/RemoteAvatarCatalogDistribution/MANAGED-CATALOG-JA.md";
        private const string Dependencies = "Assets/RemoteAvatarCatalogDistribution/DEPENDENCIES-JA.md";
        private const string Configurator = "Assets/com.avatarcatalog.remote/Editor/Rac2ManagedCatalogUrlConfigurator.cs";
        private const string BuildFolder = "Builds/RAC2-Managed-Avatar-Catalog-0.1.0";
        private const string UnityPackage = BuildFolder + "/RAC2-Managed-Avatar-Catalog-0.1.0.unitypackage";

        [MenuItem("Tools/Avatar Catalog/Developer/Build/Build Managed Avatar Catalog Release")]
        public static void BuildRelease()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before building the managed catalog release.");

            Rac2ManagedCatalogPrefabBuilder.Build();
            if (AssetDatabase.LoadAssetAtPath<GameObject>(Prefab) == null)
                throw new InvalidOperationException("Managed catalog prefab was not generated.");
            if (AssetDatabase.LoadAssetAtPath<TextAsset>(Readme) == null)
                throw new InvalidOperationException("Managed catalog README is missing.");

            Directory.CreateDirectory(Path.GetFullPath(BuildFolder));
            AssetDatabase.ExportPackage(
                new[] { Prefab, Readme, Dependencies, Configurator },
                UnityPackage,
                ExportPackageOptions.IncludeDependencies);
            if (!File.Exists(Path.GetFullPath(UnityPackage)))
                throw new InvalidOperationException("Unity failed to create the managed catalog unitypackage.");
            Debug.Log("[RAC2 Managed Catalog Release] PASS - " + UnityPackage);
        }
    }
}
