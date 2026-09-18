using System;
using System.IO;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.Udon;

namespace AvatarCatalog.Remote
{
    /// <summary>Finalizes the v0.1 Pad with the corrected tightly-packed index reader.</summary>
    public static class Rac3dImagePadGlb01FixInstaller
    {
        private const string RequestPath = "Library/NightSlotRacGlbPad01.fix";
        private const string PrefabPath = "Assets/NightSlotMall/Prefabs/RAC-3D-ImagePad.prefab";
        private const string ScriptPath = "Assets/com.avatarcatalog.remote/Runtime/SimpleGlbRuntimeLoader01.cs";
        private const string ProgramPath = "Assets/NightSlotMall/Udon/SimpleGlbRuntimeLoader01.asset";
        private const string MaterialPath = "Assets/NightSlotMall/Materials/RAC1-Runtime-Cutout.mat";

        [InitializeOnLoadMethod]
        private static void RunWhenRequested()
        {
            string path = Path.GetFullPath(RequestPath);
            if (!File.Exists(path)) return;
            EditorApplication.delayCall += () =>
            {
                try { File.Delete(path); Install(); }
                catch (Exception exception) { Debug.LogException(exception); }
            };
        }

        [MenuItem("Tools/Avatar Catalog/Developer/Installers/Finalize Legacy GLB + RAC2 Pad")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before updating the 3D Pad.");
            EnsureProgram();
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null) throw new InvalidOperationException("Runtime material is missing.");
            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                RuntimeRacImagePadControllerV2 controller = root.GetComponentInChildren<RuntimeRacImagePadControllerV2>(true);
                if (controller == null) throw new InvalidOperationException("ImagePad v2 controller is missing.");
                UdonBehaviour controllerBacking = UdonSharpEditorUtility.GetBackingUdonBehaviour(controller);
                if (controllerBacking == null) throw new InvalidOperationException("ImagePad v2 backing UdonBehaviour is missing.");
                Transform old = root.transform.Find("Direct GLB Runtime Display");
                if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);

                GameObject display = new GameObject("Direct GLB Runtime Display", typeof(MeshFilter), typeof(MeshRenderer));
                display.transform.SetParent(root.transform, false);
                MeshFilter filter = display.GetComponent<MeshFilter>();
                MeshRenderer renderer = display.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.enabled = false;
                SimpleGlbRuntimeLoader01 loader = display.AddUdonSharpComponent<SimpleGlbRuntimeLoader01>();
                UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(loader);
                if (backing == null) throw new InvalidOperationException("Direct GLB backing UdonBehaviour is missing.");
                loader.TargetMeshFilter = filter;
                loader.TargetRenderer = renderer;
                loader.MaterialTemplate = material;
                loader.DisplaySize = 1.5f;
                loader.MaxBytes = 10000000;
                loader.MaxVertices = 40000;
                loader.MaxIndices = 120000;
                loader.CallbackReceiver = controllerBacking;
                controller.GlbLoaderBehaviour = backing;
                UdonSharpEditorUtility.CopyProxyToUdon(loader, ProxySerializationPolicy.All);
                UdonSharpEditorUtility.CopyProxyToUdon(controller, ProxySerializationPolicy.All);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log("[3D ImagePad v0.1] Corrected direct GLB loader installed.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void EnsureProgram()
        {
            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
            if (script == null || script.GetClass() != typeof(SimpleGlbRuntimeLoader01))
                throw new InvalidOperationException("SimpleGlbRuntimeLoader01 has not finished compiling.");
            UdonSharpProgramAsset program = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramPath);
            if (program == null)
            {
                program = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
                program.sourceCsScript = script;
                AssetDatabase.CreateAsset(program, ProgramPath);
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
    }
}


