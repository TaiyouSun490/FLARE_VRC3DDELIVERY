using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public static class Rac2ManagedCatalogReleaseBuilderV2
    {
        private const string Prefab = "Assets/RemoteAvatarCatalogDistribution/Gimmicks/RAC2-Managed-Avatar-Catalog-v0.2.prefab";
        private const string Readme = "Assets/RemoteAvatarCatalogDistribution/MANAGED-CATALOG-V2-JA.md";
        private const string Dependencies = "Assets/RemoteAvatarCatalogDistribution/DEPENDENCIES-JA.md";
        private const string Logo = "Assets/RemoteAvatarCatalogDistribution/Brand/FLARE-Logo.png";
        private const string Configurator = "Assets/com.avatarcatalog.remote/Editor/Rac2ManagedCatalogUrlConfiguratorV2.cs";
        private const string BuildFolder = "Builds/RAC2-Managed-Avatar-Catalog-0.2.0";
        private const string UnityPackage = BuildFolder + "/RAC2-Managed-Avatar-Catalog-0.2.0.unitypackage";

        [MenuItem("Tools/Avatar Catalog/Developer/Build/Build Managed Avatar Catalog Discovery Release")]
        public static void BuildRelease()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before building the managed catalog release.");

            Rac2ManagedCatalogPrefabBuilderV2.Build();
            if (AssetDatabase.LoadAssetAtPath<GameObject>(Prefab) == null)
                throw new InvalidOperationException("Managed catalog v0.2 prefab was not generated.");
            if (AssetDatabase.LoadAssetAtPath<TextAsset>(Readme) == null)
                throw new InvalidOperationException("Managed catalog v0.2 README is missing.");
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(Logo) == null)
                throw new InvalidOperationException("FLARE product logo is missing.");

            Directory.CreateDirectory(Path.GetFullPath(BuildFolder));
            AssetDatabase.ExportPackage(
                new[] { Prefab, Readme, Dependencies, Logo, Configurator },
                UnityPackage,
                ExportPackageOptions.IncludeDependencies);
            if (!File.Exists(Path.GetFullPath(UnityPackage)))
                throw new InvalidOperationException("Unity failed to create the managed catalog v0.2 unitypackage.");
            Debug.Log("[RAC2 Managed Catalog Discovery Release] PASS - " + UnityPackage);
        }
    }
}
