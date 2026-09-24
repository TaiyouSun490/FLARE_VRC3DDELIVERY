using System;
using System.IO;
using System.Reflection;
using System.Text;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace AvatarCatalog.Remote
{
    /// <summary>Build-time preallocation for RAC2 v3 composite scenes.</summary>
    [InitializeOnLoad]
    public static class Rac2CompositePadInstaller
    {
        private const string RequestPath = "Library/Rac2CompositePad.install";
        private const string ResultPath = "Library/Rac2CompositePad.install.result";
        private const string ParticleScriptPath =
            "Assets/com.avatarcatalog.remote/Runtime/Rac2ParticlePlayer.cs";
        private const string ParticleProgramPath =
            "Assets/NightSlotMall/Udon/Rac2ParticlePlayer.asset";
        private const string LoaderProgramPath =
            "Assets/NightSlotMall/Udon/Rac2RuntimeLoader.asset";
        private const int SceneSlots = 16;
        private const int EmitterSlots = 4;
        private const int ParticleSlots = 32;

        private static readonly string[] PrefabPaths =
        {
            "Assets/NightSlotMall/Prefabs/RAC-3D-ImagePad.prefab",
            "Assets/RemoteAvatarCatalogDistribution/RAC2-3D-ImagePad.prefab",
            "Assets/RemoteAvatarCatalogDistribution/Prefabs/RAC2-ImagePad-v0.1.prefab",
            "Assets/RemoteAvatarCatalogDistribution/Prefabs/RAC2-ImagePad-Pedestal-v0.1.prefab",
        };

        static Rac2CompositePadInstaller()
        {
            EditorApplication.update += ConsumeRequest;
        }

        [MenuItem("Tools/FLARE/Developer/Installers/Rebuild Complete ImagePad Prefabs")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException(
                    "Exit Play Mode before rebuilding the ImagePad prefabs.");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            EnsureParticleProgram();
            UdonSharpCompilerV1.CompileSync(
                new UdonSharpCompileOptions { IsEditorBuild = true });
            EnsureLoaderProgramIsCurrent();

            int updated = 0;
            for (int index = 0; index < PrefabPaths.Length; index++)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPaths[index]) == null)
                    continue;
                UpdatePrefab(PrefabPaths[index]);
                updated++;
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            if (updated == 0)
                throw new InvalidOperationException("No RAC2 ImagePad prefab was found.");
            Debug.Log("[RAC2 Complete ImagePad] Updated " + updated +
                      " prefabs: 16 renderers, 4 emitters, 32 particles per emitter.");
        }

        private static void ConsumeRequest()
        {
            string request = Path.GetFullPath(RequestPath);
            if (!File.Exists(request)) return;
            File.Delete(request);
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                File.WriteAllText(Path.GetFullPath(ResultPath),
                    "WAITING: exit Play Mode and create the request again.");
                return;
            }
            try
            {
                Install();
                File.WriteAllText(Path.GetFullPath(ResultPath), "PASS");
            }
            catch (Exception exception)
            {
                File.WriteAllText(Path.GetFullPath(ResultPath), "FAIL\n" + exception);
                Debug.LogException(exception);
            }
        }

        private static void UpdatePrefab(string prefabPath)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Rac2RuntimeLoader[] loaders =
                    root.GetComponentsInChildren<Rac2RuntimeLoader>(true);
                if (loaders.Length == 0)
                    throw new InvalidOperationException(
                        "RAC2 loader is missing from " + prefabPath);
                for (int index = 0; index < loaders.Length; index++)
                    BuildPools(loaders[index]);
                UpdateTitle(root);
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (saved == null)
                    throw new InvalidOperationException(
                        "Unity could not save " + prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void BuildPools(Rac2RuntimeLoader loader)
        {
            Transform oldScene = loader.transform.Find("RAC2 v3 Scene Pool");
            if (oldScene != null) UnityEngine.Object.DestroyImmediate(oldScene.gameObject);
            Transform oldCompositeParticles =
                loader.transform.Find("RAC2 v3 Particle Pools");
            if (oldCompositeParticles != null)
                UnityEngine.Object.DestroyImmediate(oldCompositeParticles.gameObject);
            Transform legacyParticles = loader.transform.Find("RAC2 Particle Pool");
            if (legacyParticles != null)
                UnityEngine.Object.DestroyImmediate(legacyParticles.gameObject);

            Material displayTemplate = loader.LilToonOpaqueTemplate != null
                ? loader.LilToonOpaqueTemplate
                : loader.MaterialTemplate;
            if (displayTemplate == null)
                throw new InvalidOperationException(
                    "RAC2 display material template is missing.");

            GameObject sceneRoot = new GameObject("RAC2 v3 Scene Pool");
            sceneRoot.transform.SetParent(loader.transform, false);
            var objects = new GameObject[SceneSlots];
            var filters = new MeshFilter[SceneSlots];
            var renderers = new MeshRenderer[SceneSlots];
            for (int index = 0; index < SceneSlots; index++)
            {
                GameObject item = new GameObject(
                    "Renderer " + index.ToString("00"),
                    typeof(MeshFilter), typeof(MeshRenderer));
                item.transform.SetParent(sceneRoot.transform, false);
                MeshFilter filter = item.GetComponent<MeshFilter>();
                MeshRenderer renderer = item.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = displayTemplate;
                renderer.enabled = false;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
                objects[index] = item;
                filters[index] = filter;
                renderers[index] = renderer;
                item.SetActive(false);
            }

            Material alpha = FindParticleTemplate(loader, false);
            Material additive = FindParticleTemplate(loader, true);
            if (alpha == null || additive == null)
                throw new InvalidOperationException(
                    "RAC2 particle Alpha/Additive templates are missing.");

            GameObject particleRoot = new GameObject("RAC2 v3 Particle Pools");
            particleRoot.transform.SetParent(loader.transform, false);
            var players = new Rac2ParticlePlayer[EmitterSlots];
            for (int emitter = 0; emitter < EmitterSlots; emitter++)
            {
                GameObject emitterRoot = new GameObject(
                    "Emitter " + emitter.ToString("00"));
                emitterRoot.transform.SetParent(particleRoot.transform, false);
                Rac2ParticlePlayer player =
                    emitterRoot.AddUdonSharpComponent<Rac2ParticlePlayer>();
                player.AlphaTemplate = alpha;
                player.AdditiveTemplate = additive;
                player.PoolObjects = new GameObject[ParticleSlots];

                for (int particle = 0; particle < ParticleSlots; particle++)
                {
                    GameObject item = new GameObject(
                        "Particle " + particle.ToString("00"),
                        typeof(MeshFilter), typeof(MeshRenderer));
                    item.transform.SetParent(emitterRoot.transform, false);
                    MeshRenderer renderer = item.GetComponent<MeshRenderer>();
                    renderer.sharedMaterial = alpha;
                    renderer.enabled = false;
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    renderer.lightProbeUsage = LightProbeUsage.Off;
                    renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    player.PoolObjects[particle] = item;
                }
                UdonSharpEditorUtility.CopyProxyToUdon(
                    player, ProxySerializationPolicy.All);
                players[emitter] = player;
            }

            loader.SceneObjects = objects;
            loader.SceneMeshFilters = filters;
            loader.SceneRenderers = renderers;
            loader.ParticlePlayers = players;
            loader.ParticlePlayer = players[0];
            UdonSharpEditorUtility.CopyProxyToUdon(
                loader, ProxySerializationPolicy.All);
            EditorUtility.SetDirty(loader);
        }

        private static Material FindParticleTemplate(
            Rac2RuntimeLoader loader, bool additive)
        {
            Rac2ParticlePlayer current = loader.ParticlePlayer;
            Material result = current == null
                ? null
                : additive ? current.AdditiveTemplate : current.AlphaTemplate;
            if (result != null) return result;
            string path = additive
                ? "Assets/NightSlotMall/Materials/RAC2-Particle-Additive.mat"
                : "Assets/NightSlotMall/Materials/RAC2-Particle-Alpha.mat";
            return AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        private static void EnsureLoaderProgramIsCurrent()
        {
            UdonSharpProgramAsset program =
                AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(
                    LoaderProgramPath);
            if (program != null && program.fieldDefinitions != null &&
                program.fieldDefinitions.ContainsKey("SceneMeshFilters") &&
                program.fieldDefinitions.ContainsKey("ParticlePlayers"))
                return;

            throw new InvalidOperationException(
                "Rac2RuntimeLoader Udon compilation did not produce the v3 fields.\n" +
                FormatLastUdonDiagnostics());
        }

        private static string FormatLastUdonDiagnostics()
        {
            try
            {
                Type cacheType = typeof(UdonSharpCompilerV1).Assembly.GetType(
                    "UdonSharp.UdonSharpEditorCache");
                PropertyInfo instanceProperty = cacheType == null ? null :
                    cacheType.GetProperty("Instance",
                        BindingFlags.Static | BindingFlags.Public |
                        BindingFlags.NonPublic);
                object cache = instanceProperty == null ? null :
                    instanceProperty.GetValue(null, null);
                PropertyInfo diagnosticsProperty = cacheType == null ? null :
                    cacheType.GetProperty("LastCompileDiagnostics",
                        BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic);
                Array diagnostics = diagnosticsProperty == null || cache == null
                    ? null
                    : diagnosticsProperty.GetValue(cache, null) as Array;
                if (diagnostics == null || diagnostics.Length == 0)
                    return "UdonSharp returned no diagnostics.";

                var text = new StringBuilder();
                for (int index = 0; index < diagnostics.Length; index++)
                {
                    object diagnostic = diagnostics.GetValue(index);
                    Type diagnosticType = diagnostic.GetType();
                    string severity = Convert.ToString(
                        diagnosticType.GetField("severity").GetValue(diagnostic));
                    if (!string.Equals(severity, "Error",
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    string file = Convert.ToString(
                        diagnosticType.GetField("file").GetValue(diagnostic));
                    int line = Convert.ToInt32(
                        diagnosticType.GetField("line").GetValue(diagnostic)) + 1;
                    int character = Convert.ToInt32(
                        diagnosticType.GetField("character").GetValue(diagnostic)) + 1;
                    string message = Convert.ToString(
                        diagnosticType.GetField("message").GetValue(diagnostic));
                    text.Append(file).Append('(').Append(line).Append(',')
                        .Append(character).Append("): ").AppendLine(message);
                }
                return text.Length == 0
                    ? "UdonSharp returned no error diagnostics."
                    : text.ToString();
            }
            catch (Exception exception)
            {
                return "Could not read UdonSharp diagnostics: " + exception.Message;
            }
        }
        private static void EnsureParticleProgram()
        {
            MonoScript script =
                AssetDatabase.LoadAssetAtPath<MonoScript>(ParticleScriptPath);
            if (script == null || script.GetClass() != typeof(Rac2ParticlePlayer))
                throw new InvalidOperationException(
                    "Rac2ParticlePlayer has not finished compiling.");
            UdonSharpProgramAsset program =
                AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(
                    ParticleProgramPath);
            if (program == null)
            {
                program = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
                program.sourceCsScript = script;
                AssetDatabase.CreateAsset(program, ParticleProgramPath);
            }
            else if (program.sourceCsScript != script)
            {
                program.sourceCsScript = script;
                EditorUtility.SetDirty(program);
            }
            AssetDatabase.SaveAssets();
        }

        private static void UpdateTitle(GameObject root)
        {
            Text[] labels = root.GetComponentsInChildren<Text>(true);
            for (int index = 0; index < labels.Length; index++)
            {
                if (labels[index].text != null &&
                    labels[index].text.StartsWith(
                        "RAC 3D IMAGEPAD", StringComparison.Ordinal))
                {
                    labels[index].text =
                        "RAC 3D IMAGEPAD / STATIC + VAT + PARTICLE";
                    EditorUtility.SetDirty(labels[index]);
                    return;
                }
            }
        }
    }
}
