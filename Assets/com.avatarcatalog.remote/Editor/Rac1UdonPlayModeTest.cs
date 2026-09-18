using System;
using System.IO;
using UdonSharp;
using UdonSharpEditor;
using UdonSharp.Compiler;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

namespace AvatarCatalog.Remote
{
    /// <summary>
    /// Creates a Play Mode scene that invokes the real compiled Udon program.
    /// The RAC1 payload is served from a fixed localhost URL so the test covers
    /// VRCStringDownloader, ResultBytes, parsing, and runtime Mesh creation.
    /// </summary>
    public static class Rac1UdonPlayModeTest
    {
        private const string MenuRoot = "Tools/Avatar Catalog/Developer/Tests/RAC1 Udon Play Mode/";
        private const string SourceScenePath = "Assets/AvatarCatalogRoundTrip/XBot-RoundTrip.unity";
        private const string TargetScenePath = "Assets/AvatarCatalogRoundTrip/XBot-Udon-PlayMode.unity";
        private const string Rac1AssetPath = "Assets/AvatarCatalogRoundTrip/XBot.rac1";
        private const string RestoredObjectName = "RAC1 RESTORED - X Bot";
        private const string CanvasObjectName = "RAC1 Udon Function Controls";
        private const string LocalRac1Url = "http://127.0.0.1:8765/XBot.rac1";
        private const string LoaderScriptPath = "Packages/com.avatarcatalog.remote/Runtime/RemoteAvatarCatalogLoader.cs";
        private const string RuntimeAssemblyDefinitionPath = "Packages/com.avatarcatalog.remote/Runtime/AvatarCatalog.Remote.Runtime.asmdef";
        private const string UdonSharpAssemblyAssetPath = "Assets/AvatarCatalogRoundTrip/AvatarCatalog.Remote.Runtime.UdonSharpAssembly.asset";
        private const string LoaderProgramAssetPath = "Assets/AvatarCatalogRoundTrip/RemoteAvatarCatalogLoader.asset";

        /// <summary>Batch entry point that compiles and validates only the loader program asset.</summary>
        public static void CompileLoaderProgramFromCommandLine()
        {
            EnsureLoaderProgramAsset();
        }

        [MenuItem(MenuRoot + "Create Test Scene")]
        public static void CreateTestScene()
        {
            RequireAsset(SourceScenePath);
            RequireAsset(Rac1AssetPath);

            EnsureLoaderProgramAsset();
            Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);
            GameObject restoredRoot = GameObject.Find(RestoredObjectName);
            if (restoredRoot == null)
            {
                throw new InvalidOperationException(
                    $"'{RestoredObjectName}' was not found in {SourceScenePath}. Run the X Bot round-trip test first.");
            }

            MeshFilter targetMeshFilter = restoredRoot.GetComponent<MeshFilter>();
            MeshRenderer targetRenderer = restoredRoot.GetComponent<MeshRenderer>();
            if (targetMeshFilter == null || targetRenderer == null || targetRenderer.sharedMaterial == null)
            {
                throw new InvalidOperationException("The restored X Bot display is missing its MeshFilter, MeshRenderer, or material.");
            }

            // The left-hand display must start empty. Only the Udon function is
            // allowed to recreate its Mesh while Play Mode is running.
            targetMeshFilter.sharedMesh = null;
            targetRenderer.enabled = false;

            RemoteAvatarCatalogLoader loader = restoredRoot.GetComponent<RemoteAvatarCatalogLoader>();
            if (loader == null)
            {
                loader = restoredRoot.AddUdonSharpComponent<RemoteAvatarCatalogLoader>();
            }

            loader.CatalogUrls = new[] { new VRCUrl(LocalRac1Url) };
            loader.SelectedSlot = 0;
            loader.LoadOnInteract = false;
            loader.TargetMeshFilter = targetMeshFilter;
            loader.TargetRenderer = targetRenderer;
            loader.MaterialTemplate = targetRenderer.sharedMaterial;
            loader.VerboseLogging = true;

            UdonBehaviour loaderUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(loader);
            if (loaderUdon == null)
            {
                throw new InvalidOperationException("UdonSharp did not create a backing UdonBehaviour for the RAC1 loader.");
            }

            UdonSharpEditorUtility.CopyProxyToUdon(loader, ProxySerializationPolicy.All);

            GameObject oldCanvas = GameObject.Find(CanvasObjectName);
            if (oldCanvas != null)
            {
                UnityEngine.Object.DestroyImmediate(oldCanvas);
            }

            CreateControls(loaderUdon);
            EnsureEventSystem();

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, TargetScenePath))
            {
                throw new IOException("Unity could not save the Udon Play Mode scene at " + TargetScenePath);
            }

            Selection.activeGameObject = restoredRoot;
            Debug.Log(
                "[RAC1 Udon PlayMode] Scene ready. Enter Play Mode, then click " +
                "'FIRE UDON: LoadSelectedSlot()'. Fixed URL: " + LocalRac1Url);
        }

        /// <summary>Command-line entry point that leaves a visible Editor in Play Mode.</summary>
        [MenuItem(MenuRoot + "Create Scene and Enter Play Mode %#u")]
        public static void CreateAndEnterPlayMode()
        {
            CreateTestScene();
            EditorApplication.delayCall += () => EditorApplication.isPlaying = true;
        }

        [MenuItem(MenuRoot + "Fire Load Function %#l")]
        public static void FireLoadFunction()
        {
            if (!EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("Enter Play Mode before firing the Udon load function.");
            }

            UdonBehaviour loaderUdon = FindLoaderUdon();
            Debug.Log("[RAC1 Udon PlayMode] Sending custom event 'LoadSelectedSlot' to the live UdonBehaviour.");
            loaderUdon.SendCustomEvent("LoadSelectedSlot");
        }

        [MenuItem(MenuRoot + "Fire Load Function %#l", true)]
        private static bool ValidateFireLoadFunction()
        {
            return EditorApplication.isPlaying;
        }

        [MenuItem(MenuRoot + "Clear Restored Mesh")]
        public static void ClearRestoredMesh()
        {
            if (!EditorApplication.isPlaying)
            {
                throw new InvalidOperationException("Enter Play Mode before firing the Udon clear function.");
            }

            FindLoaderUdon().SendCustomEvent("Clear");
        }

        private static UdonBehaviour FindLoaderUdon()
        {
            GameObject restoredRoot = GameObject.Find(RestoredObjectName);
            if (restoredRoot == null)
            {
                throw new InvalidOperationException("The Udon RAC1 display object is not present in the active scene.");
            }

            UdonBehaviour loaderUdon = restoredRoot.GetComponent<UdonBehaviour>();
            if (loaderUdon == null)
            {
                throw new InvalidOperationException("The live backing UdonBehaviour was not found on the RAC1 display.");
            }

            return loaderUdon;
        }

        private static void CreateControls(UdonBehaviour loaderUdon)
        {
            GameObject canvasObject = new GameObject(
                CanvasObjectName,
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.matchWidthOrHeight = 0.5f;

            GameObject panelObject = CreateUiObject("Panel", canvasObject.transform);
            RectTransform panelRect = panelObject.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 1f);
            panelRect.anchorMax = new Vector2(0.5f, 1f);
            panelRect.pivot = new Vector2(0.5f, 1f);
            panelRect.anchoredPosition = new Vector2(0f, -20f);
            panelRect.sizeDelta = new Vector2(760f, 205f);
            Image panelImage = panelObject.AddComponent<Image>();
            panelImage.color = new Color(0.02f, 0.025f, 0.035f, 0.94f);

            CreateText(
                panelObject.transform,
                "Title",
                "VRC UDON PLAY MODE - X BOT RAC1",
                new Vector2(0f, -16f),
                new Vector2(710f, 34f),
                25,
                TextAnchor.MiddleCenter,
                new Color(0.62f, 1f, 0.18f, 1f));

            CreateText(
                panelObject.transform,
                "Instructions",
                "Left display starts empty. This button sends a custom event to the live UdonBehaviour.",
                new Vector2(0f, -52f),
                new Vector2(710f, 28f),
                16,
                TextAnchor.MiddleCenter,
                Color.white);

            Button fireButton = CreateButton(
                panelObject.transform,
                "Fire Udon Load",
                "FIRE UDON: LoadSelectedSlot()",
                new Vector2(0f, -108f),
                new Vector2(520f, 54f),
                new Color(0.28f, 0.65f, 0.06f, 1f));

            UnityAction<string> fireUdonEvent = loaderUdon.SendCustomEvent;
            UnityEventTools.AddStringPersistentListener(
                fireButton.onClick,
                fireUdonEvent,
                "LoadSelectedSlot");

            CreateText(
                panelObject.transform,
                "Shortcut",
                "Keyboard alternative: Ctrl+Shift+L   |   Expected Console: Loading then Loaded 15,901 vertices",
                new Vector2(0f, -164f),
                new Vector2(710f, 25f),
                14,
                TextAnchor.MiddleCenter,
                new Color(0.72f, 0.78f, 0.86f, 1f));
        }

        private static Button CreateButton(
            Transform parent,
            string name,
            string label,
            Vector2 anchoredPosition,
            Vector2 size,
            Color color)
        {
            GameObject buttonObject = CreateUiObject(name, parent);
            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            Image image = buttonObject.AddComponent<Image>();
            image.color = color;
            Button button = buttonObject.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(
                Mathf.Min(color.r + 0.15f, 1f),
                Mathf.Min(color.g + 0.15f, 1f),
                Mathf.Min(color.b + 0.15f, 1f),
                1f);
            colors.pressedColor = color * 0.75f;
            button.colors = colors;

            Text text = CreateText(
                buttonObject.transform,
                "Label",
                label,
                Vector2.zero,
                size,
                22,
                TextAnchor.MiddleCenter,
                Color.white);
            RectTransform textRect = text.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.pivot = new Vector2(0.5f, 0.5f);
            textRect.anchoredPosition = Vector2.zero;
            textRect.sizeDelta = Vector2.zero;
            return button;
        }

        private static Text CreateText(
            Transform parent,
            string name,
            string value,
            Vector2 anchoredPosition,
            Vector2 size,
            int fontSize,
            TextAnchor alignment,
            Color color)
        {
            GameObject textObject = CreateUiObject(name, parent);
            RectTransform rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            Text text = textObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = value;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.raycastTarget = false;
            return text;
        }

        private static GameObject CreateUiObject(string name, Transform parent)
        {
            GameObject value = new GameObject(name, typeof(RectTransform));
            value.transform.SetParent(parent, false);
            return value;
        }

        private static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() != null)
            {
                return;
            }

            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        private static void EnsureLoaderProgramAsset()
        {
            AssemblyDefinitionAsset runtimeAssembly =
                AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(RuntimeAssemblyDefinitionPath);
            if (runtimeAssembly == null)
            {
                throw new InvalidOperationException(
                    "Unity could not load the runtime asmdef at " + RuntimeAssemblyDefinitionPath);
            }

            UdonSharpAssemblyDefinition udonSharpAssembly =
                AssetDatabase.LoadAssetAtPath<UdonSharpAssemblyDefinition>(UdonSharpAssemblyAssetPath);
            bool udonSharpAssemblyChanged = false;
            if (udonSharpAssembly == null)
            {
                udonSharpAssembly = ScriptableObject.CreateInstance<UdonSharpAssemblyDefinition>();
                udonSharpAssembly.sourceAssembly = runtimeAssembly;
                AssetDatabase.CreateAsset(udonSharpAssembly, UdonSharpAssemblyAssetPath);
                udonSharpAssemblyChanged = true;
            }
            else if (udonSharpAssembly.sourceAssembly != runtimeAssembly)
            {
                udonSharpAssembly.sourceAssembly = runtimeAssembly;
                EditorUtility.SetDirty(udonSharpAssembly);
                udonSharpAssemblyChanged = true;
            }

            if (udonSharpAssemblyChanged)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(UdonSharpAssemblyAssetPath, ImportAssetOptions.ForceSynchronousImport);
            }

            string loaderScriptGuid = AssetDatabase.AssetPathToGUID(LoaderScriptPath);
            if (string.IsNullOrEmpty(loaderScriptGuid))
            {
                throw new InvalidOperationException(
                    "Loader script path did not resolve to an asset GUID: " + LoaderScriptPath);
            }

            string resolvedLoaderPath = AssetDatabase.GUIDToAssetPath(loaderScriptGuid);
            if (!string.Equals(resolvedLoaderPath, LoaderScriptPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Loader script GUID resolved to unexpected path: " + resolvedLoaderPath);
            }

            Debug.Log("[RAC1 Udon PlayMode] Loader script GUID verified: " + loaderScriptGuid + " -> " + resolvedLoaderPath);

            MonoScript loaderScript = AssetDatabase.LoadAssetAtPath<MonoScript>(resolvedLoaderPath);
            if (loaderScript == null)
            {
                throw new InvalidOperationException(
                    "Unity could not load the RemoteAvatarCatalogLoader MonoScript at " + resolvedLoaderPath);
            }

            Type resolvedLoaderClass = loaderScript.GetClass();
            if (resolvedLoaderClass != typeof(RemoteAvatarCatalogLoader))
            {
                throw new InvalidOperationException(
                    "Loader MonoScript did not resolve to " + typeof(RemoteAvatarCatalogLoader).FullName + ".");
            }

            UdonSharpProgramAsset programAsset =
                AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(LoaderProgramAssetPath);
            bool programAssetChanged = false;
            if (programAsset == null)
            {
                programAsset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
                programAsset.sourceCsScript = loaderScript;
                AssetDatabase.CreateAsset(programAsset, LoaderProgramAssetPath);
                programAssetChanged = true;
            }
            else if (programAsset.sourceCsScript != loaderScript)
            {
                programAsset.sourceCsScript = loaderScript;
                EditorUtility.SetDirty(programAsset);
                programAssetChanged = true;
            }

            if (programAssetChanged)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(LoaderProgramAssetPath, ImportAssetOptions.ForceSynchronousImport);
            }

            UdonSharpCompilerV1.CompileSync(
                new UdonSharpCompileOptions { IsEditorBuild = true });
            AssetDatabase.SaveAssets();

            if (programAsset.sourceCsScript != loaderScript)
            {
                throw new InvalidOperationException("Compiled Udon program lost its loader source-script association.");
            }

            if (!string.IsNullOrEmpty(programAsset.AssemblyError))
            {
                throw new InvalidOperationException(
                    "Udon assembly failed: " + programAsset.AssemblyError);
            }

            if (programAsset.SerializedProgramAsset == null)
            {
                throw new InvalidOperationException("UdonSharp did not produce a serialized loader program asset.");
            }

            string serializedProgramPath = AssetDatabase.GetAssetPath(programAsset.SerializedProgramAsset);
            FileInfo serializedProgramFile = new FileInfo(Path.GetFullPath(serializedProgramPath));
            if (string.IsNullOrEmpty(serializedProgramPath) ||
                !serializedProgramFile.Exists ||
                serializedProgramFile.Length <= 0L)
            {
                throw new InvalidOperationException("Compiled Udon serialized program file is missing or empty.");
            }

            Debug.Log(
                "[RAC1 Udon PlayMode] CompileSync completed. Program asset: " +
                LoaderProgramAssetPath + ", serialized bytes: " + serializedProgramFile.Length + ".");
        }

        private static void RequireAsset(string assetPath)
        {
            string absolutePath = Path.GetFullPath(assetPath);
            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException("Required X Bot test asset is missing: " + assetPath, absolutePath);
            }
        }
    }
}
