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
    /// <summary>Upgrades the existing ImagePad prefab to RAC2-only in-place.</summary>
    public static class Rac2GlbImagePadInstaller
    {
        private const string MenuPath = "Tools/Avatar Catalog/Developer/Installers/Upgrade 3D Pad to RAC2";
        private const string RequestPath = "Library/NightSlotRac2Pad.upgrade";
        private const string PrefabPath = "Assets/NightSlotMall/Prefabs/RAC-3D-ImagePad.prefab";
        private const string MaterialPath = "Assets/NightSlotMall/Materials/RAC1-Runtime-Cutout.mat";
        private const string LilToonOpaquePath = "Assets/NightSlotMall/Materials/RAC2-lilToon-Opaque.mat";
        private const string LilToonCutoutPath = "Assets/NightSlotMall/Materials/RAC2-lilToon-Cutout.mat";
        private const string Rac2ScriptPath = "Assets/com.avatarcatalog.remote/Runtime/Rac2RuntimeLoader.cs";
        private const string Rac2ProgramPath = "Assets/NightSlotMall/Udon/Rac2RuntimeLoader.asset";
        private const string ControllerScriptPath = "Assets/com.avatarcatalog.remote/Runtime/RuntimeRacImagePadController.cs";
        private const string ControllerProgramPath = "Assets/NightSlotMall/Udon/RuntimeRacImagePadController.asset";
        private const string SampleRac2 = "https://night-slot-avatar-mall.e36a5137-a9f2-4607-89d1-e70e23b94dc8.chatgpt.site/api/catalog/slots/booth-001/rac2";

        [InitializeOnLoadMethod]
        private static void UpgradeWhenRequested()
        {
            string path = Path.GetFullPath(RequestPath);
            if (!File.Exists(path)) return;
            EditorApplication.delayCall += () =>
            {
                try { File.Delete(path); UpgradePrefab(); }
                catch (Exception exception) { Debug.LogException(exception); }
            };
        }

        [MenuItem(MenuPath)]
        public static void UpgradePrefab()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before upgrading the 3D Pad.");
            EnsurePrograms();
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null) throw new InvalidOperationException("RAC runtime material is missing.");
            Material lilToonOpaque = EnsureLilToonMaterial(LilToonOpaquePath, "lilToon", 0);
            Material lilToonCutout = EnsureLilToonMaterial(LilToonCutoutPath, "Hidden/lilToonCutout", 1);

            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                RuntimeRacImagePadController controller = root.GetComponentInChildren<RuntimeRacImagePadController>(true);
                if (controller == null) throw new InvalidOperationException("Existing ImagePad controller is missing.");
                UdonBehaviour controllerBacking = UdonSharpEditorUtility.GetBackingUdonBehaviour(controller);
                if (controllerBacking == null) throw new InvalidOperationException("ImagePad backing UdonBehaviour is missing.");

                Transform legacy = root.transform.Find("Runtime Display");
                if (legacy != null) { legacy.gameObject.SetActive(false); legacy.name = "Legacy RAC1 Display (disabled)"; }
                Transform old = root.transform.Find("RAC2 Runtime Display");
                if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
                Transform obsoleteGlb = root.transform.Find("GLB Runtime Parent");
                if (obsoleteGlb != null) UnityEngine.Object.DestroyImmediate(obsoleteGlb.gameObject);

                GameObject display = new GameObject("RAC2 Runtime Display", typeof(MeshFilter), typeof(MeshRenderer));
                display.transform.SetParent(root.transform, false);
                MeshFilter filter = display.GetComponent<MeshFilter>();
                MeshRenderer renderer = display.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.enabled = false;

                Rac2RuntimeLoader loader = display.AddUdonSharpComponent<Rac2RuntimeLoader>();
                UdonBehaviour loaderBacking = UdonSharpEditorUtility.GetBackingUdonBehaviour(loader);
                if (loaderBacking == null) throw new InvalidOperationException("RAC2 backing UdonBehaviour is missing.");
                loader.TargetMeshFilter = filter;
                loader.TargetRenderer = renderer;
                loader.MaterialTemplate = material;
                loader.LilToonOpaqueTemplate = lilToonOpaque;
                loader.LilToonCutoutTemplate = lilToonCutout;
                loader.EnforceStandardBoothProfile = true;
                Rac2InteractionPadInstaller.Configure(loader);
                loader.CallbackReceiver = controllerBacking;
                loader.LoadStartedEvent = "OnRac2LoadStarted";
                loader.LoadSucceededEvent = "OnRac2LoadSucceeded";
                loader.LoadFailedEvent = "OnRac2LoadFailed";
                loader.ClearedEvent = "OnRac2Cleared";

                Canvas canvas = root.GetComponentInChildren<Canvas>(true);
                if (canvas == null) throw new InvalidOperationException("Existing ImagePad Canvas is missing.");
                while (canvas.transform.childCount > 0) UnityEngine.Object.DestroyImmediate(canvas.transform.GetChild(0).gameObject);

                BuildPanel(canvas.transform, controllerBacking, out VRCUrlInputField input, out Button rac2Button, out Button sampleButton, out Button retryButton, out Button clearButton, out Text status);
                controller.UrlInput = input;
                controller.Rac2LoaderBehaviour = loaderBacking;
                controller.StatusText = status;
                controller.Rac2Button = rac2Button;
                controller.SampleRac2Button = sampleButton;
                controller.RetryButton = retryButton;
                controller.ClearButton = clearButton;
                controller.SampleRac2Url = new VRCUrl(SampleRac2);

                UdonSharpEditorUtility.CopyProxyToUdon(loader, ProxySerializationPolicy.All);
                UdonSharpEditorUtility.CopyProxyToUdon(controller, ProxySerializationPolicy.All);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log("[RAC2 Pad] Prefab upgraded in-place. Existing scene instance and booth-003 placement were preserved.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateUpgrade()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode && AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) != null;
        }

        private static void BuildPanel(Transform canvas, UdonBehaviour backing, out VRCUrlInputField input, out Button rac2, out Button sample, out Button retry, out Button clear, out Text status)
        {
            GameObject panel = Ui("Panel", canvas);
            Stretch(panel.GetComponent<RectTransform>());
            panel.AddComponent<Image>().color = new Color(.025f, .035f, .055f, .97f);
            TextAt(panel.transform, "Title", "RAC2 v0.1 / lilToon 3D PAD", new Vector2(0, 154), new Vector2(880, 42), 28, new Color(.68f, .94f, 1, 1));
            TextAt(panel.transform, "Instructions", "Paste a public HTTPS .rac2 URL or RAC2 API endpoint.", new Vector2(0, 119), new Vector2(880, 30), 16, new Color(.8f, .86f, .94f, 1));
            input = CreateInput(panel.transform);
            rac2 = ButtonAt(panel.transform, "RAC2 Load Button", "LOAD RAC2", new Vector2(-285, -10), new Vector2(200, 52));
            sample = ButtonAt(panel.transform, "Sample RAC2 Button", "SAMPLE", new Vector2(-95, -10), new Vector2(160, 52));
            retry = ButtonAt(panel.transform, "Retry Button", "RETRY", new Vector2(95, -10), new Vector2(160, 52));
            clear = ButtonAt(panel.transform, "Clear Button", "CLEAR", new Vector2(285, -10), new Vector2(160, 52));
            Add(rac2, backing, "LoadRac2FromInput");
            Add(sample, backing, "LoadSampleRac2");
            Add(retry, backing, "RetryLast");
            Add(clear, backing, "ClearDisplay");
            status = TextAt(panel.transform, "Status", "Paste a RAC2 URL or use SAMPLE.", new Vector2(0, -105), new Vector2(860, 100), 20, new Color(.42f, .95f, .76f, 1));
        }

        private static VRCUrlInputField CreateInput(Transform parent)
        {
            GameObject value = Ui("RAC2 URL Input", parent);
            RectTransform rect = value.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.anchoredPosition = new Vector2(0, 65);
            rect.sizeDelta = new Vector2(850, 58);
            Image image = value.AddComponent<Image>();
            image.color = new Color(.11f, .14f, .19f, 1);
            Text placeholder = TextAt(value.transform, "Placeholder", "https://example.com/model.rac2  or  .../api/.../rac2", Vector2.zero, new Vector2(810, 48), 18, new Color(.56f, .62f, .70f, 1));
            placeholder.fontStyle = FontStyle.Italic;
            Text text = TextAt(value.transform, "Text", "", Vector2.zero, new Vector2(810, 48), 18, Color.white);
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
            image.color = new Color(.12f, .42f, .58f, 1);
            Button button = value.AddComponent<Button>();
            button.targetGraphic = image;
            Navigation nav = button.navigation;
            nav.mode = Navigation.Mode.None;
            button.navigation = nav;
            TextAt(value.transform, "Label", label, Vector2.zero, size, 18, Color.white);
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
            EnsureProgram(Rac2ScriptPath, Rac2ProgramPath, typeof(Rac2RuntimeLoader));
            EnsureProgram(ControllerScriptPath, ControllerProgramPath, typeof(RuntimeRacImagePadController));
            AssetDatabase.SaveAssets();
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
            AssetDatabase.SaveAssets();
        }

        private static Material EnsureLilToonMaterial(string path, string shaderName, int transparentMode)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null) throw new InvalidOperationException("Required lilToon shader is missing: " + shaderName);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(material, path);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
                EditorUtility.SetDirty(material);
            }

            if (material.HasProperty("_TransparentMode")) material.SetFloat("_TransparentMode", transparentMode);
            if (material.HasProperty("_UseBumpMap")) material.SetFloat("_UseBumpMap", 1f);
            if (material.HasProperty("_BumpScale")) material.SetFloat("_BumpScale", 1f);
            EditorUtility.SetDirty(material);
            return material;
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
