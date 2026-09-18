using System;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace AvatarCatalog.Remote
{
    public static class Rac2ParticlePadInstaller
    {
        private const string PrefabPath = "Assets/NightSlotMall/Prefabs/RAC-3D-ImagePad.prefab";
        private const string AlphaPath = "Assets/NightSlotMall/Materials/RAC2-Particle-Alpha.mat";
        private const string AdditivePath = "Assets/NightSlotMall/Materials/RAC2-Particle-Additive.mat";
        private const string PlayerScriptPath = "Assets/com.avatarcatalog.remote/Runtime/Rac2ParticlePlayer.cs";
        private const string PlayerProgramPath = "Assets/NightSlotMall/Udon/Rac2ParticlePlayer.asset";
        private const int PoolSize = 32;

        [MenuItem("Tools/Avatar Catalog/Developer/Installers/Install RAC2 Particle Support")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before installing RAC2 Particle support.");

            EnsureParticleProgram();
            Material alpha = EnsureMaterial(AlphaPath, "Avatar Catalog/RAC2 Particle Alpha");
            Material additive = EnsureMaterial(AdditivePath, "Avatar Catalog/RAC2 Particle Additive");

            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Rac2RuntimeLoader loader = root.GetComponentInChildren<Rac2RuntimeLoader>(true);
                if (loader == null) throw new InvalidOperationException("RAC2 Runtime Loader is missing from the Pad.");

                Transform oldPool = loader.transform.Find("RAC2 Particle Pool");
                if (oldPool != null) UnityEngine.Object.DestroyImmediate(oldPool.gameObject);

                GameObject poolRoot = new GameObject("RAC2 Particle Pool");
                poolRoot.transform.SetParent(loader.transform, false);
                Rac2ParticlePlayer player = poolRoot.AddUdonSharpComponent<Rac2ParticlePlayer>();
                player.AlphaTemplate = alpha;
                player.AdditiveTemplate = additive;
                player.PoolObjects = new GameObject[PoolSize];

                for (int index = 0; index < PoolSize; index++)
                {
                    GameObject item = new GameObject("Particle " + index.ToString("00"), typeof(MeshFilter), typeof(MeshRenderer));
                    item.transform.SetParent(poolRoot.transform, false);
                    MeshRenderer renderer = item.GetComponent<MeshRenderer>();
                    renderer.sharedMaterial = alpha;
                    renderer.enabled = false;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.lightProbeUsage = LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    player.PoolObjects[index] = item;
                }

                loader.ParticlePlayer = player;
                UdonSharpEditorUtility.CopyProxyToUdon(player, ProxySerializationPolicy.All);
                UdonSharpEditorUtility.CopyProxyToUdon(loader, ProxySerializationPolicy.All);

                Text title = root.GetComponentInChildren<Text>(true);
                if (title != null && title.text.StartsWith("RAC 3D IMAGEPAD", StringComparison.Ordinal))
                    title.text = "RAC 3D IMAGEPAD / VAT + PARTICLE";

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log("[RAC2 Particle] 32-slot pool and Alpha/Additive templates installed.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void EnsureParticleProgram()
        {
            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(PlayerScriptPath);
            if (script == null || script.GetClass() != typeof(Rac2ParticlePlayer))
                throw new InvalidOperationException("Rac2ParticlePlayer has not finished compiling.");

            UdonSharpProgramAsset program = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(PlayerProgramPath);
            if (program == null)
            {
                program = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
                program.sourceCsScript = script;
                AssetDatabase.CreateAsset(program, PlayerProgramPath);
            }
            else if (program.sourceCsScript != script)
            {
                program.sourceCsScript = script;
                EditorUtility.SetDirty(program);
            }

            AssetDatabase.SaveAssets();
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
            AssetDatabase.SaveAssets();
        }

        private static Material EnsureMaterial(string path, string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null) throw new InvalidOperationException("Particle shader is missing: " + shaderName);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(material, path);
            }
            else material.shader = shader;
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}