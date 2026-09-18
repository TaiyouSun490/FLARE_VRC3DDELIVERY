using System;
using System.IO;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;

namespace AvatarCatalog.Remote
{
    /// <summary>Adds the self-owned direct GLB path beside the existing full RAC2 path.</summary>
    public static class Rac3dImagePadV2Installer
    {
        private const string MenuPath = "Tools/Avatar Catalog/Developer/Installers/Install Legacy GLB + RAC2 Pad";
        private const string RequestPath = "Library/NightSlotRacGlbPadV2.install";
        private const string PrefabPath = "Assets/NightSlotMall/Prefabs/RAC-3D-ImagePad.prefab";
        private const string MaterialPath = "Assets/NightSlotMall/Materials/RAC1-Runtime-Cutout.mat";
        private const string GlbScriptPath = "Assets/com.avatarcatalog.remote/Runtime/SimpleGlbRuntimeLoader.cs";
        private const string GlbProgramPath = "Assets/NightSlotMall/Udon/SimpleGlbRuntimeLoader.asset";
        private const string ControllerScriptPath = "Assets/com.avatarcatalog.remote/Runtime/RuntimeRacImagePadControllerV2.cs";
        private const string ControllerProgramPath = "Assets/NightSlotMall/Udon/RuntimeRacImagePadControllerV2.asset";
        private const string SampleGlb = "https://raw.githubusercontent.com/KhronosGroup/glTF-Sample-Assets/main/Models/Box/glTF-Binary/Box.glb";
        private const string SampleRac2 = "https://night-slot-avatar-mall.e36a5137-a9f2-4607-89d1-e70e23b94dc8.chatgpt.site/api/catalog/slots/booth-001/rac2";

        [InitializeOnLoadMethod]
        private static void InstallWhenRequested()
        {
            string path = Path.GetFullPath(RequestPath);
            if (!File.Exists(path)) return;
            EditorApplication.delayCall += () =>
            {
                try { File.Delete(path); Install(); }
                catch (Exception exception) { Debug.LogException(exception); }
            };
        }

        [MenuItem(MenuPath)]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before updating the 3D Pad.");
            EnsurePrograms();
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null) throw new InvalidOperationException("RAC runtime material is missing.");

            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Transform oldController = root.transform.Find("ImagePad Controller");
                if (oldController != null) UnityEngine.Object.DestroyImmediate(oldController.gameObject);
                Transform oldV2 = root.transform.Find("ImagePad Controller V2");
                if (oldV2 != null) UnityEngine.Object.DestroyImmediate(oldV2.gameObject);
                Transform oldGlb = root.transform.Find("Direct GLB Runtime Display");
                if (oldGlb != null) UnityEngine.Object.DestroyImmediate(oldGlb.gameObject);

                Rac2RuntimeLoader rac2Loader = root.GetComponentInChildren<Rac2RuntimeLoader>(true);
                if (rac2Loader == null) throw new InvalidOperationException("Existing RAC2 loader is missing.");
                UdonBehaviour rac2Backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(rac2Loader);
                if (rac2Backing == null) throw new InvalidOperationException("RAC2 backing UdonBehaviour is missing.");

                GameObject controllerObject = new GameObject("ImagePad Controller V2");
                controllerObject.transform.SetParent(root.transform, false);
                RuntimeRacImagePadControllerV2 controller = controllerObject.AddUdonSharpComponent<RuntimeRacImagePadControllerV2>();
                UdonBehaviour controllerBacking = UdonSharpEditorUtility.GetBackingUdonBehaviour(controller);
                if (controllerBacking == null) throw new InvalidOperationException("ImagePad v2 backing UdonBehaviour is missing.");

                GameObject glbDisplay = new GameObject("Direct GLB Runtime Display", typeof(MeshFilter), typeof(MeshRenderer));
                glbDisplay.transform.SetParent(root.transform, false);
                MeshFilter glbFilter = glbDisplay.GetComponent<MeshFilter>();
                MeshRenderer glbRenderer = glbDisplay.GetComponent<MeshRenderer>();
                glbRenderer.sharedMaterial = material;
                glbRenderer.enabled = false;
                SimpleGlbRuntimeLoader glbLoader = glbDisplay.AddUdonSharpComponent<SimpleGlbRuntimeLoader>();
                UdonBehaviour glbBacking = UdonSharpEditorUtility.GetBackingUdonBehaviour(glbLoader);
                if (glbBacking == null) throw new InvalidOperationException("GLB backing UdonBehaviour is missing.");
                glbLoader.TargetMeshFilter = glbFilter;
                glbLoader.TargetRenderer = glbRenderer;
                glbLoader.MaterialTemplate = material;
                glbLoader.DisplaySize = 1.5f;
                glbLoader.MaxBytes = 10000000;
                glbLoader.MaxVertices = 40000;
                glbLoader.MaxIndices = 120000;
                glbLoader.CallbackReceiver = controllerBacking;

                rac2Loader.CallbackReceiver = controllerBacking;
                rac2Loader.LoadStartedEvent = "OnRac2LoadStarted";
                rac2Loader.LoadSucceededEvent = "OnRac2LoadSucceeded";
                rac2Loader.LoadFailedEvent = "OnRac2LoadFailed";
                rac2Loader.ClearedEvent = "OnRac2Cleared";

                Canvas canvas = root.GetComponentInChildren<Canvas>(true);
                if (canvas == null) throw new InvalidOperationException("Existing ImagePad Canvas is missing.");
                RectTransform canvasRect = canvas.GetComponent<RectTransform>();
                canvasRect.sizeDelta = new Vector2(920, 540);
                while (canvas.transform.childCount > 0) UnityEngine.Object.DestroyImmediate(canvas.transform.GetChild(0).gameObject);

                BuildPanel(canvas.transform, controllerBacking, out VRCUrlInputField input,
                    out Button glbButton, out Button rac2Button, out Button sampleGlbButton, out Button sampleRac2Button,
                    out Button retryButton, out Button clearButton, out Button adaptiveLoadingButton,
                    out Text adaptiveLoadingText, out Text status);
                controller.UrlInput = input;
                controller.GlbLoaderBehaviour = glbBacking;
                controller.Rac2LoaderBehaviour = rac2Backing;
                controller.StatusText = status;
                controller.GlbButton = glbButton;
                controller.Rac2Button = rac2Button;
                controller.SampleGlbButton = sampleGlbButton;
                controller.SampleRac2Button = sampleRac2Button;
                controller.RetryButton = retryButton;
                controller.ClearButton = clearButton;
                controller.AdaptiveLoadingButton = adaptiveLoadingButton;
                controller.AdaptiveLoadingText = adaptiveLoadingText;
                controller.Rac2AdaptiveLoadingEnabled = true;
                controller.SampleGlbUrl = new VRCUrl(SampleGlb);
                controller.SampleRac2Url = new VRCUrl(SampleRac2);

                UdonSharpEditorUtility.CopyProxyToUdon(glbLoader, ProxySerializationPolicy.All);
                UdonSharpEditorUtility.CopyProxyToUdon(rac2Loader, ProxySerializationPolicy.All);
                UdonSharpEditorUtility.CopyProxyToUdon(controller, ProxySerializationPolicy.All);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log("[3D ImagePad v0.1] Installed direct GLB + RAC2 modes. Scene was not opened or saved.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateInstall()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode && AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null;
        }

        private static void BuildPanel(Transform canvas, UdonBehaviour backing, out VRCUrlInputField input,
            out Button glb, out Button rac2, out Button sampleGlb, out Button sampleRac2,
            out Button retry, out Button clear, out Button adaptiveLoading,
            out Text adaptiveLoadingText, out Text status)
        {
            GameObject panel = Ui("Panel", canvas);
            Stretch(panel.GetComponent<RectTransform>());
            panel.AddComponent<Image>().color = new Color(.025f, .035f, .055f, .97f);
            TextAt(panel.transform, "Title", "RAC 3D IMAGEPAD v0.1", new Vector2(0, 208), new Vector2(890, 38), 27, new Color(.68f, .94f, 1, 1));
            TextAt(panel.transform, "Instructions", "DIRECT GLB = basic one-mesh preview / RAC2 = full validated preview", new Vector2(0, 175), new Vector2(890, 28), 16, new Color(.8f, .86f, .94f, 1));
            input = CreateInput(panel.transform);

            glb = ButtonAt(panel.transform, "Load Direct GLB Button", "LOAD DIRECT GLB", new Vector2(-220, 62), new Vector2(400, 48));
            rac2 = ButtonAt(panel.transform, "Load RAC2 Button", "LOAD RAC2 / API", new Vector2(220, 62), new Vector2(400, 48));
            sampleGlb = ButtonAt(panel.transform, "Sample GLB Button", "SAMPLE GLB", new Vector2(-220, 4), new Vector2(400, 44));
            sampleRac2 = ButtonAt(panel.transform, "Sample RAC2 Button", "SAMPLE RAC2", new Vector2(220, 4), new Vector2(400, 44));
            retry = ButtonAt(panel.transform, "Retry Button", "RETRY", new Vector2(-220, -52), new Vector2(400, 42));
            clear = ButtonAt(panel.transform, "Clear Button", "CLEAR", new Vector2(220, -52), new Vector2(400, 42));
            adaptiveLoading = ButtonAt(panel.transform, "RAC2 Adaptive Loading Button", "ADAPTIVE LOAD: ON", new Vector2(0, -102), new Vector2(840, 38));
            adaptiveLoadingText = adaptiveLoading.GetComponentInChildren<Text>();
            Add(glb, backing, "LoadGlbFromInput");
            Add(rac2, backing, "LoadRac2FromInput");
            Add(sampleGlb, backing, "LoadSampleGlb");
            Add(sampleRac2, backing, "LoadSampleRac2");
            Add(retry, backing, "RetryLast");
            Add(clear, backing, "ClearDisplay");
            Add(adaptiveLoading, backing, "ToggleRac2AdaptiveLoading");
            status = TextAt(panel.transform, "Status", "Paste a URL or select a sample.", new Vector2(0, -175), new Vector2(860, 76), 18, new Color(.42f, .95f, .76f, 1));
        }

        private static VRCUrlInputField CreateInput(Transform parent)
        {
            GameObject value = Ui("GLB or RAC2 URL Input", parent);
            RectTransform rect = value.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.anchoredPosition = new Vector2(0, 122);
            rect.sizeDelta = new Vector2(850, 54);
            Image image = value.AddComponent<Image>();
            image.color = new Color(.11f, .14f, .19f, 1);
            Text placeholder = TextAt(value.transform, "Placeholder", "https://example.com/model.glb   or   https://example.com/model.rac2", Vector2.zero, new Vector2(810, 44), 17, new Color(.56f, .62f, .70f, 1));
            placeholder.fontStyle = FontStyle.Italic;
            Text text = TextAt(value.transform, "Text", "", Vector2.zero, new Vector2(810, 44), 17, Color.white);
            VRCUrlInputField input = value.AddComponent<VRCUrlInputField>();
            input.targetGraphic = image;
            input.textComponent = text;
            input.placeholder = placeholder;
            input.lineType = VRCUrlInputField.LineType.SingleLine;
            input.characterLimit = 2048;
            Navigation navigation = input.navigation;
            navigation.mode = Navigation.Mode.None;
            input.navigation = navigation;
            return input;
        }

        private static Button ButtonAt(Transform parent, string name, string label, Vector2 position, Vector2 size)
        {
            GameObject value = Ui(name, parent);
            RectTransform rect = value.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            Image image = value.AddComponent<Image>();
            image.color = label.Contains("GLB") ? new Color(.18f, .39f, .66f, 1) : new Color(.12f, .48f, .48f, 1);
            Button button = value.AddComponent<Button>();
            button.targetGraphic = image;
            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;
            TextAt(value.transform, "Label", label, Vector2.zero, size, 17, Color.white);
            return button;
        }

        private static void Add(Button button, UdonBehaviour backing, string eventName)
        {
            UnityAction<string> action = backing.SendCustomEvent;
            UnityEventTools.AddStringPersistentListener(button.onClick, action, eventName);
        }

        private static Text TextAt(Transform parent, string name, string text, Vector2 position, Vector2 size, int fontSize, Color color)
        {
            GameObject value = Ui(name, parent);
            RectTransform rect = value.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            Text label = value.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = color;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Truncate;
            return label;
        }

        private static GameObject Ui(string name, Transform parent)
        {
            GameObject value = new GameObject(name, typeof(RectTransform));
            value.transform.SetParent(parent, false);
            return value;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void EnsurePrograms()
        {
            EnsureFolder("Assets/NightSlotMall/Udon");
            EnsureProgram(GlbScriptPath, GlbProgramPath, typeof(SimpleGlbRuntimeLoader));
            EnsureProgram(ControllerScriptPath, ControllerProgramPath, typeof(RuntimeRacImagePadControllerV2));
            AssetDatabase.SaveAssets();
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
            AssetDatabase.SaveAssets();
        }

        private static void EnsureProgram(string scriptPath, string assetPath, Type type)
        {
            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
            if (script == null || script.GetClass() != type) throw new InvalidOperationException(type.Name + " has not finished compiling.");
            UdonSharpProgramAsset program = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath);
            if (program == null)
            {
                program = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
                program.sourceCsScript = script;
                AssetDatabase.CreateAsset(program, assetPath);
            }
            else if (program.sourceCsScript != script)
            {
                program.sourceCsScript = script;
                EditorUtility.SetDirty(program);
            }
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



