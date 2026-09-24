using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;

// Local packaging harness only; deliberately excluded from the distributable.
[InitializeOnLoad]
public static class FlarePackageVerification
{
    private const string ImportKey = "FLARE.PortablePackageImport";
    private static double _readyAfter;
    static FlarePackageVerification() { EditorApplication.update += WaitForImport; }
    private const string Output = "Builds/FLARE-Core-0.2.5/FLARE-Core-0.2.5.unitypackage";
    private static readonly string[] Roots = { "Assets/com.avatarcatalog.remote", "Assets/RemoteAvatarCatalogDistribution", "Assets/NightSlotMall", "Assets/SerializedUdonPrograms" };
    private static readonly string[] Prefabs = { "RAC2-ImagePad", "RAC2-ImagePad-Pedestal", "RAC2-Product-Pedestal" };
    private static bool _compileError;

    public static void Pack()
    {
        try
        {
            CompilePrograms();
            MakePortable();
            Validate();
            string[] sourcePaths = AssetDatabase.GetAllAssetPaths().Where(p => Roots.Take(3).Any(r => p == r || p.StartsWith(r + "/", StringComparison.Ordinal))).ToArray();
            // SDK initialization also writes programs here: include only our actual dependencies.
            string[] programs = AssetDatabase.GetDependencies(sourcePaths, true).Where(p => p.StartsWith("Assets/SerializedUdonPrograms/", StringComparison.Ordinal)).ToArray();
            string[] paths = sourcePaths.Concat(programs).Concat(new[] { "Assets/SerializedUdonPrograms" }).Distinct().ToArray();
            if (paths.Any(p => p.Contains("FlareGimmick") || p.StartsWith("Assets/FLAREGimmicks"))) throw new Exception("Gimmick files in core package.");
            foreach (string dependency in AssetDatabase.GetDependencies(paths, true))
                if (dependency.StartsWith("Assets/") && !paths.Contains(dependency)) throw new Exception("Unpackaged dependency: " + dependency);
            Directory.CreateDirectory(Path.GetDirectoryName(Output));
            AssetDatabase.ExportPackage(paths, Output, ExportPackageOptions.Default);
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(Output), "asset-list.txt"), paths.OrderBy(p => p));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output), "export-validation.txt"), "PASS: isolated build project compiles, Udon compiles, three prefabs have no missing scripts/references, all Assets dependencies included.\nFLARE Core 0.2.5. Third-party Packages excluded. Not a VRChat play/build test. Known Sirius eye-rendering issue remains unresolved.\n");
            Debug.Log("[FLARE package] EXPORT PASS: " + Output);
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    private static void MakePortable()
    {
        foreach (string name in Prefabs)
        {
            string path = "Assets/RemoteAvatarCatalogDistribution/Prefabs/" + name + ".prefab";
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            bool changed = false;
            try
            {
                foreach (UdonSharpBehaviour proxy in contents.GetComponentsInChildren<UdonSharpBehaviour>(true))
                {
                    var serialized = new SerializedObject(proxy);
                    var sample = serialized.FindProperty("SampleRac2Url");
                    if (sample == null) continue;
                    var button = serialized.FindProperty("SampleRac2Button").objectReferenceValue as Component;
                    if (string.IsNullOrEmpty(sample.FindPropertyRelative("url").stringValue) && (button == null || !button.gameObject.activeSelf)) continue;
                    sample.FindPropertyRelative("url").stringValue = "";
                    if (button != null) button.gameObject.SetActive(false);
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    UdonSharpEditorUtility.CopyProxyToUdon(proxy);
                    changed = true;
                }
                if (changed && PrefabUtility.SaveAsPrefabAsset(contents, path) == null) throw new Exception("Portable prefab save failed: " + path);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
        AssetDatabase.SaveAssets();
    }

    public static void VerifyImport()
    {
        try
        {
            Validate();
            File.WriteAllText("import-validation.txt", "PASS: imported unitypackage into separate empty project with Worlds SDK 3.10.4 / lilToon 2.3.4; C# and Udon compile; three prefabs instantiate with no missing scripts/references. No source-world scenes or avatar fixtures installed. VRChat playback and upload not tested.\n");
            Debug.Log("[FLARE package] FRESH IMPORT PASS");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    public static void ImportPackage()
    {
        string[] args = Environment.GetCommandLineArgs();
        int option = Array.IndexOf(args, "-flarePackage");
        if (option < 0 || option + 1 >= args.Length || !File.Exists(args[option + 1])) throw new Exception("Provide -flarePackage with the archive path.");
        SessionState.SetBool(ImportKey, true);
        SessionState.SetFloat(ImportKey + ".deadline", (float)EditorApplication.timeSinceStartup + 240f);
        AssetDatabase.ImportPackage(args[option + 1], false);
    }

    private static void WaitForImport()
    {
        if (!SessionState.GetBool(ImportKey, false)) return;
        if (EditorApplication.timeSinceStartup > SessionState.GetFloat(ImportKey + ".deadline", 0))
        {
            SessionState.SetBool(ImportKey, false);
            Debug.LogError("Timed out waiting for package import/compilation."); EditorApplication.Exit(1); return;
        }
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) { _readyAfter = 0; return; }
        var script = AssetDatabase.LoadAssetAtPath<MonoScript>("Assets/com.avatarcatalog.remote/Runtime/Rac2RuntimeLoader.cs");
        if (script == null || script.GetClass() == null) return;
        if (_readyAfter == 0) { _readyAfter = EditorApplication.timeSinceStartup + 2; return; }
        if (EditorApplication.timeSinceStartup < _readyAfter) return;
        SessionState.SetBool(ImportKey, false);
        VerifyImport();
    }

    private static void CompilePrograms()
    {
        if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("Assets/com.avatarcatalog.remote/Runtime/AvatarCatalog.Remote.Runtime.UdonSharpAssembly.asset") == null)
            throw new Exception("Missing UdonSharp assembly registration.");
        _compileError = false;
        Application.logMessageReceived += OnCompileLog;
        try { UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true }); }
        finally { Application.logMessageReceived -= OnCompileLog; }
        if (_compileError || UdonSharpProgramAsset.AnyUdonSharpScriptHasError()) throw new Exception("Udon compile errors.");
    }

    private static void Validate()
    {
        CompilePrograms();
        foreach (string name in Prefabs)
        {
            string path = "Assets/RemoteAvatarCatalogDistribution/Prefabs/" + name + ".prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new Exception("Missing prefab: " + path);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                foreach (Transform t in instance.GetComponentsInChildren<Transform>(true))
                {
                    if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject) != 0) throw new Exception("Missing script: " + path + "/" + t.name);
                    foreach (Component component in t.GetComponents<Component>())
                    {
                        var serialized = new SerializedObject(component);
                        var sample = serialized.FindProperty("SampleRac2Url");
                        if (sample != null)
                        {
                            if (!string.IsNullOrEmpty(sample.FindPropertyRelative("url").stringValue)) throw new Exception("Operator sample URL remains in " + path);
                            var button = serialized.FindProperty("SampleRac2Button").objectReferenceValue as Component;
                            if (button != null && button.gameObject.activeSelf) throw new Exception("Unconfigured sample button remains visible in " + path);
                        }
                        var field = serialized.GetIterator();
                        while (field.Next(true))
                            if (field.propertyType == SerializedPropertyType.ObjectReference && field.objectReferenceValue == null && field.objectReferenceInstanceIDValue != 0)
                                throw new Exception("Missing reference: " + path + "/" + t.name + ":" + field.propertyPath);
                    }
                }
                if (!instance.GetComponentsInChildren<Component>(true).Any(c => c != null && (c.GetType().Name == "Rac2RuntimeLoader" || c.GetType().Name == "Rac2ProductLoader")))
                    throw new Exception("No RAC2 loader on " + path);
            }
            finally { UnityEngine.Object.DestroyImmediate(instance); }
        }
    }
    private static void OnCompileLog(string message, string stack, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) _compileError = true;
    }
}
