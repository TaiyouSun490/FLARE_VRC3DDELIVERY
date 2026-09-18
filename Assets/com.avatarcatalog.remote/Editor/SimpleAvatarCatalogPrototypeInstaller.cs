using System;
using System.IO;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.Udon;

namespace AvatarCatalog.Remote
{
    /// <summary>Builds the three-avatar catalog MVP without modifying the RAC booth prefab.</summary>
    public static class SimpleAvatarCatalogPrototypeInstaller
    {
        private const string MenuPath = "Tools/Avatar Catalog/Developer/Installers/Install 3 Avatar Catalog %#3";
        private const string ScenePath = "Assets/NightSlotMall/Scenes/NightSlot-Mall-Prototype.unity";
        private const string AnchorName = "BoothAnchor_002";
        private const string RootName = "Avatar Catalog - 3 Avatar MVP";
        private const string PrefabPath = "Assets/NightSlotMall/Prefabs/Avatar-Catalog-3.prefab";
        private const string MaterialPath = "Assets/NightSlotMall/Materials/Avatar-Catalog-Pedestal.mat";
        private const string ProgramAssetPath = "Assets/NightSlotMall/Udon/SimpleAvatarCatalogController.asset";
        private const string ControllerScriptPath = "Assets/com.avatarcatalog.remote/Runtime/SimpleAvatarCatalogController.cs";
        private const string RuntimeAsmdefPath = "Assets/com.avatarcatalog.remote/Runtime/AvatarCatalog.Remote.Runtime.asmdef";
        private const string UdonAssemblyAssetPath = "Assets/AvatarCatalogRoundTrip/AvatarCatalog.Remote.Runtime.UdonSharpAssembly.asset";
        private const string InstallRequestPath = "Library/NightSlotAvatarCatalog.install";

        private static readonly string[] AvatarIds =
        {
            "avtr_47e599d7-3360-4b97-a5d5-4466e07ad6b4",
            "avtr_3fedb290-7c9f-4aac-8ddb-9b7aed4bedfa",
            "avtr_c38a1615-5bf5-42b4-84eb-a8b6c37cbd11"
        };

        private static readonly string[] AvatarNames = { "Human", "Robot", "SDK Sample" };
        private static readonly string[] AvatarInitials = { "H", "R", "S" };
        private static readonly Color[] AvatarColors =
        {
            new Color(0.20f, 0.48f, 0.92f, 1f),
            new Color(0.88f, 0.32f, 0.52f, 1f),
            new Color(0.27f, 0.72f, 0.48f, 1f)
        };

        [InitializeOnLoadMethod]
        private static void InstallWhenRequested()
        {
            string requestPath = Path.GetFullPath(InstallRequestPath);
            if (!File.Exists(requestPath))
            {
                return;
            }

            EditorApplication.delayCall += () =>
            {
                try
                {
                    File.Delete(requestPath);
                    Install();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            };
        }

        [MenuItem(MenuPath)]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("Exit Play Mode before installing the avatar catalog.");
            }

            EnsureProgramAsset();
            Scene scene = OpenTargetScene();
            GameObject anchor = GameObject.Find(AnchorName);
            if (anchor == null)
            {
                throw new InvalidOperationException("The mall scene does not contain " + AnchorName + ".");
            }

            GameObject oldInstance = GameObject.Find(RootName);
            if (oldInstance != null)
            {
                UnityEngine.Object.DestroyImmediate(oldInstance);
            }

            GameObject prefab = BuildPrefabAsset();
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.name = RootName;
            instance.transform.SetPositionAndRotation(anchor.transform.position, anchor.transform.rotation);
            instance.transform.localScale = Vector3.one;

            EnsureEventSystem(scene);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                throw new IOException("Unity could not save " + ScenePath + ".");
            }

            Selection.activeGameObject = instance;
            Debug.Log(
                "[Simple Avatar Catalog] Installed three touch icons at " + AnchorName +
                ". Human, Robot, and SDK Sample are ready for VRChat Build & Test.");
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateInstall()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private static Scene OpenTargetScene()
        {
            Scene current = SceneManager.GetActiveScene();
            if (current.IsValid() && current.path == ScenePath)
            {
                return current;
            }

            if (current.IsValid() && current.isDirty)
            {
                throw new InvalidOperationException(
                    "The active scene has unsaved changes. Save it before installing the avatar catalog.");
            }

            return EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        private static GameObject BuildPrefabAsset()
        {
            EnsureFolder("Assets/NightSlotMall/Prefabs");
            Material pedestalMaterial = EnsurePedestalMaterial();

            GameObject root = new GameObject(RootName);
            try
            {
                SimpleAvatarCatalogController controller =
                    root.AddUdonSharpComponent<SimpleAvatarCatalogController>();
                UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(controller);
                if (backing == null)
                {
                    throw new InvalidOperationException("UdonSharp did not create the catalog backing behaviour.");
                }

                VRCAvatarPedestal pedestal = CreatePedestal(root.transform, pedestalMaterial);
                Canvas canvas = CreateCanvas(root.transform);
                Image[] frames = new Image[3];
                Image[] icons = new Image[3];
                Text[] placeholders = new Text[3];
                Text[] labels = new Text[3];

                CreateCatalogPanel(
                    canvas.transform,
                    backing,
                    frames,
                    icons,
                    placeholders,
                    labels,
                    out Text status);

                controller.TargetPedestal = pedestal;
                controller.AvatarBlueprintIds = (string[])AvatarIds.Clone();
                controller.AvatarNames = (string[])AvatarNames.Clone();
                controller.AvatarIcons = new Sprite[3];
                controller.WearAvatarWhenIconIsPressed = true;
                controller.SelectionFrames = frames;
                controller.AvatarIconImages = icons;
                controller.AvatarPlaceholderTexts = placeholders;
                controller.AvatarNameLabels = labels;
                controller.StatusText = status;
                controller.LastSelectedIndex = -1;
                controller.LastSelectedBlueprintId = string.Empty;
                controller.UseRequestCount = 0;

                pedestal.blueprintId = AvatarIds[0];
                pedestal.ChangeAvatarsOnUse = true;
                UdonSharpEditorUtility.CopyProxyToUdon(controller, ProxySerializationPolicy.All);
                SetLayerRecursively(root, 0);

                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                if (prefab == null)
                {
                    throw new IOException("Unity could not save " + PrefabPath + ".");
                }

                AssetDatabase.SaveAssets();
                return prefab;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static VRCAvatarPedestal CreatePedestal(Transform parent, Material material)
        {
            GameObject host = new GameObject("Avatar Pedestal");
            host.transform.SetParent(parent, false);
            host.transform.localPosition = new Vector3(-0.62f, 0f, 0f);

            VRCAvatarPedestal pedestal = host.AddComponent<VRCAvatarPedestal>();
            pedestal.blueprintId = AvatarIds[0];
            pedestal.ChangeAvatarsOnUse = true;
            pedestal.scale = 1f;

            GameObject placement = new GameObject("Avatar Placement");
            placement.transform.SetParent(host.transform, false);
            pedestal.Placement = placement.transform;

            GameObject baseVisual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            baseVisual.name = "Pedestal Base";
            baseVisual.transform.SetParent(host.transform, false);
            baseVisual.transform.localPosition = new Vector3(0f, 0.06f, 0f);
            baseVisual.transform.localScale = new Vector3(0.55f, 0.06f, 0.55f);
            baseVisual.GetComponent<MeshRenderer>().sharedMaterial = material;
            return pedestal;
        }

        private static Canvas CreateCanvas(Transform parent)
        {
            GameObject canvasObject = new GameObject(
                "Avatar Catalog Touch UI",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster),
                typeof(VRCUiShape));
            canvasObject.transform.SetParent(parent, false);

            RectTransform rect = canvasObject.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(900f, 440f);
            rect.localPosition = new Vector3(0.50f, 1.28f, 0.04f);
            rect.localRotation = Quaternion.Euler(0f, 180f, 0f);
            rect.localScale = Vector3.one * 0.0018f;

            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 20;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.dynamicPixelsPerUnit = 12f;
            return canvas;
        }

        private static void CreateCatalogPanel(
            Transform canvas,
            UdonBehaviour backing,
            Image[] frames,
            Image[] icons,
            Text[] placeholders,
            Text[] labels,
            out Text status)
        {
            GameObject panel = CreateUiObject("Panel", canvas);
            Stretch(panel.GetComponent<RectTransform>());
            Image panelImage = panel.AddComponent<Image>();
            panelImage.color = new Color(0.025f, 0.035f, 0.055f, 0.96f);

            CreateText(
                panel.transform,
                "Title",
                "AVATAR CATALOG - 3 AVATAR MVP",
                new Vector2(0f, 178f),
                new Vector2(840f, 46f),
                30,
                new Color(0.68f, 0.94f, 1f, 1f));

            CreateText(
                panel.transform,
                "Instructions",
                "Touch an icon to wear that avatar",
                new Vector2(0f, 139f),
                new Vector2(840f, 34f),
                21,
                new Color(0.82f, 0.87f, 0.94f, 1f));

            float[] positions = { -280f, 0f, 280f };
            for (int i = 0; i < 3; i++)
            {
                Button button = CreateAvatarButton(
                    panel.transform,
                    i,
                    positions[i],
                    AvatarColors[i],
                    out frames[i],
                    out icons[i],
                    out placeholders[i],
                    out labels[i]);

                UnityAction<string> sendEvent = backing.SendCustomEvent;
                UnityEventTools.AddStringPersistentListener(
                    button.onClick,
                    sendEvent,
                    "ChooseAvatar" + i);
            }

            status = CreateText(
                panel.transform,
                "Status",
                "Touch an avatar icon to switch.",
                new Vector2(0f, -184f),
                new Vector2(840f, 36f),
                20,
                new Color(0.48f, 0.86f, 1f, 1f));
        }

        private static Button CreateAvatarButton(
            Transform parent,
            int index,
            float x,
            Color accent,
            out Image frame,
            out Image icon,
            out Text placeholder,
            out Text label)
        {
            GameObject buttonObject = CreateUiObject("Avatar " + (index + 1) + " Button", parent);
            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(x, -12f);
            rect.sizeDelta = new Vector2(236f, 252f);

            frame = buttonObject.AddComponent<Image>();
            frame.color = new Color(0.13f, 0.16f, 0.22f, 1f);
            Button button = buttonObject.AddComponent<Button>();
            button.targetGraphic = frame;
            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;

            GameObject iconObject = CreateUiObject("Avatar Icon", buttonObject.transform);
            RectTransform iconRect = iconObject.GetComponent<RectTransform>();
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = new Vector2(0f, 28f);
            iconRect.sizeDelta = new Vector2(168f, 168f);
            icon = iconObject.AddComponent<Image>();
            icon.color = Color.white;
            icon.raycastTarget = false;
            icon.enabled = false;

            placeholder = CreateText(
                buttonObject.transform,
                "Placeholder Initial",
                AvatarInitials[index],
                new Vector2(0f, 31f),
                new Vector2(170f, 170f),
                88,
                accent);
            placeholder.raycastTarget = false;

            label = CreateText(
                buttonObject.transform,
                "Avatar Name",
                AvatarNames[index],
                new Vector2(0f, -91f),
                new Vector2(210f, 46f),
                25,
                Color.white);
            label.raycastTarget = false;
            return button;
        }

        private static Text CreateText(
            Transform parent,
            string name,
            string value,
            Vector2 position,
            Vector2 size,
            int fontSize,
            Color color)
        {
            GameObject textObject = CreateUiObject(name, parent);
            RectTransform rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;

            Text text = textObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = value;
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = color;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static GameObject CreateUiObject(string name, Transform parent)
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

        private static Material EnsurePedestalMaterial()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material != null)
            {
                return material;
            }

            Shader shader = Shader.Find("Standard");
            if (shader == null)
            {
                throw new InvalidOperationException("The built-in Standard shader is unavailable.");
            }

            material = new Material(shader)
            {
                name = "Avatar Catalog Pedestal",
                color = new Color(0.08f, 0.16f, 0.22f, 1f),
                enableInstancing = true
            };
            AssetDatabase.CreateAsset(material, MaterialPath);
            return material;
        }

        private static void EnsureEventSystem(Scene scene)
        {
            if (UnityEngine.Object.FindObjectOfType<EventSystem>(true) != null)
            {
                return;
            }

            GameObject eventSystem = new GameObject(
                "Avatar Catalog EventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule));
            SceneManager.MoveGameObjectToScene(eventSystem, scene);
        }

        private static void EnsureProgramAsset()
        {
            EnsureFolder("Assets/NightSlotMall/Udon");
            AssemblyDefinitionAsset runtimeAsmdef =
                AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(RuntimeAsmdefPath);
            UdonSharpAssemblyDefinition udonAssembly =
                AssetDatabase.LoadAssetAtPath<UdonSharpAssemblyDefinition>(UdonAssemblyAssetPath);
            if (runtimeAsmdef == null || udonAssembly == null || udonAssembly.sourceAssembly != runtimeAsmdef)
            {
                throw new InvalidOperationException(
                    "The AvatarCatalog.Remote.Runtime UdonSharp assembly registration is missing or invalid.");
            }

            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(ControllerScriptPath);
            if (script == null || script.GetClass() != typeof(SimpleAvatarCatalogController))
            {
                throw new InvalidOperationException(
                    "SimpleAvatarCatalogController has not finished compiling. Wait for Unity, then run Install again.");
            }

            UdonSharpProgramAsset program =
                AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(ProgramAssetPath);
            if (program == null)
            {
                program = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
                program.sourceCsScript = script;
                AssetDatabase.CreateAsset(program, ProgramAssetPath);
            }
            else if (program.sourceCsScript != script)
            {
                program.sourceCsScript = script;
                EditorUtility.SetDirty(program);
            }

            AssetDatabase.SaveAssets();
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
            AssetDatabase.SaveAssets();

            if (!string.IsNullOrEmpty(program.AssemblyError))
            {
                throw new InvalidOperationException("Avatar catalog Udon compile failed: " + program.AssemblyError);
            }

            if (program.SerializedProgramAsset == null)
            {
                throw new InvalidOperationException("UdonSharp did not create the avatar catalog serialized program.");
            }
        }

        private static void EnsureFolder(string assetPath)
        {
            string[] segments = assetPath.Split('/');
            string current = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                string next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, segments[i]);
                }
                current = next;
            }
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            root.layer = layer;
            for (int i = 0; i < root.transform.childCount; i++)
            {
                SetLayerRecursively(root.transform.GetChild(i).gameObject, layer);
            }
        }
    }
}
