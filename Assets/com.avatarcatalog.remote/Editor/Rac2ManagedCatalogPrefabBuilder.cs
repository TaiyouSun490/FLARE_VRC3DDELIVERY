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
using VRC.Udon;

namespace AvatarCatalog.Remote
{
    public static class Rac2ManagedCatalogPrefabBuilder
    {
        private const string OutputFolder = "Assets/RemoteAvatarCatalogDistribution/Gimmicks";
        private const string OutputPrefab = OutputFolder + "/RAC2-Managed-Avatar-Catalog-v0.1.prefab";
        private const string UdonFolder = "Assets/RemoteAvatarCatalogDistribution/Udon";
        private const string ScriptPath = "Assets/com.avatarcatalog.remote/Runtime/Rac2ManagedCatalogController.cs";
        private const string ProgramPath = UdonFolder + "/Rac2ManagedCatalogController.asset";

        [MenuItem("Tools/FLARE/Developer/Build/Build Managed Avatar Catalog Gimmick")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode first.");
            EnsureFolder(OutputFolder);
            EnsureFolder(UdonFolder);
            EnsureProgram();
            AssetDatabase.SaveAssets();
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });

            GameObject root = new GameObject("RAC2 Managed Avatar Catalog v0.1");
            try
            {
                GameObject pedestalHost = new GameObject("Avatar Pedestal");
                pedestalHost.transform.SetParent(root.transform, false);
                pedestalHost.transform.localPosition = new Vector3(1.9f, 0f, 0f);
                VRCAvatarPedestal pedestal = pedestalHost.AddComponent<VRCAvatarPedestal>();
                GameObject pedestalVisual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pedestalVisual.name = "Pedestal Visual";
                pedestalVisual.transform.SetParent(pedestalHost.transform, false);
                pedestalVisual.transform.localPosition = new Vector3(0f, .12f, 0f);
                pedestalVisual.transform.localScale = new Vector3(.75f, .12f, .75f);
                Renderer pedestalRenderer = pedestalVisual.GetComponent<Renderer>();
                if (pedestalRenderer != null) pedestalRenderer.sharedMaterial = EnsureMaterial();

                Canvas canvas = CreateCanvas(root.transform);
                GameObject panel = Ui("Catalog Panel", canvas.transform);
                Stretch(panel.GetComponent<RectTransform>());
                panel.AddComponent<Image>().color = new Color(.018f, .026f, .043f, .985f);
                TextAt(panel.transform, "Title", "MANAGED AVATAR CATALOG", new Vector2(0f, 495f), new Vector2(1350f, 60f), 36, new Color(.65f, .94f, 1f, 1f));

                Rac2ManagedCatalogController controller = root.AddUdonSharpComponent<Rac2ManagedCatalogController>();
                UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(controller);
                if (backing == null) throw new InvalidOperationException("Managed catalog backing UdonBehaviour is missing.");

                GameObject[] roots = new GameObject[12];
                RawImage[] images = new RawImage[12];
                Text[] names = new Text[12];
                Text[] creators = new Text[12];
                Image[] frames = new Image[12];
                for (int cell = 0; cell < 12; cell++)
                {
                    int row = cell / 4;
                    int column = cell - row * 4;
                    Vector2 position = new Vector2(-495f + column * 330f, 260f - row * 245f);
                    GameObject card = Ui("Avatar Card " + cell.ToString("00"), panel.transform);
                    RectTransform rect = card.GetComponent<RectTransform>();
                    rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
                    rect.anchoredPosition = position;
                    rect.sizeDelta = new Vector2(310f, 225f);
                    Image frame = card.AddComponent<Image>();
                    frame.color = new Color(.12f, .16f, .22f, 1f);
                    Button button = card.AddComponent<Button>();
                    button.targetGraphic = frame;
                    Navigation navigation = button.navigation;
                    navigation.mode = Navigation.Mode.None;
                    button.navigation = navigation;
                    Add(button, backing, "SelectCard" + cell);
                    RawImage thumbnail = Ui("Thumbnail", card.transform).AddComponent<RawImage>();
                    RectTransform thumbnailRect = thumbnail.GetComponent<RectTransform>();
                    thumbnailRect.anchorMin = thumbnailRect.anchorMax = new Vector2(.5f, .5f);
                    thumbnailRect.anchoredPosition = new Vector2(0f, 23f);
                    thumbnailRect.sizeDelta = new Vector2(286f, 160f);
                    thumbnail.color = Color.white;
                    thumbnail.raycastTarget = false;
                    Text name = TextAt(card.transform, "Name", "Avatar", new Vector2(0f, -73f), new Vector2(286f, 34f), 20, Color.white);
                    Text creator = TextAt(card.transform, "Creator", "Creator", new Vector2(0f, -101f), new Vector2(286f, 24f), 14, new Color(.6f, .78f, .88f, 1f));
                    roots[cell] = card;
                    images[cell] = thumbnail;
                    names[cell] = name;
                    creators[cell] = creator;
                    frames[cell] = frame;
                }

                Button previous = ButtonAt(panel.transform, "Previous Page", "< PREV", new Vector2(-500f, -430f), new Vector2(190f, 54f));
                Button reload = ButtonAt(panel.transform, "Reload", "RELOAD", new Vector2(-270f, -430f), new Vector2(180f, 54f));
                Text pageText = TextAt(panel.transform, "Page", "0 / 0", new Vector2(0f, -430f), new Vector2(180f, 54f), 22, Color.white);
                Button next = ButtonAt(panel.transform, "Next Page", "NEXT >", new Vector2(270f, -430f), new Vector2(190f, 54f));
                Button tryAvatar = ButtonAt(panel.transform, "Try Avatar", "TRY AVATAR", new Vector2(505f, -430f), new Vector2(220f, 54f));
                Add(previous, backing, "PreviousPage");
                Add(reload, backing, "ReloadManifest");
                Add(next, backing, "NextPage");
                Add(tryAvatar, backing, "TrySelectedAvatar");

                Text selectedName = TextAt(panel.transform, "Selected Name", "", new Vector2(-370f, -500f), new Vector2(480f, 38f), 22, new Color(.65f, .94f, 1f, 1f));
                Text selectedCreator = TextAt(panel.transform, "Selected Creator", "", new Vector2(80f, -500f), new Vector2(360f, 32f), 17, Color.white);
                Text selectedUrl = TextAt(panel.transform, "Selected Product URL", "", new Vector2(350f, -500f), new Vector2(430f, 32f), 14, new Color(.6f, .78f, .88f, 1f));
                Text status = TextAt(panel.transform, "Status", "Configure fixed URLs before upload.", new Vector2(0f, -545f), new Vector2(1300f, 34f), 16, new Color(.42f, .95f, .76f, 1f));

                controller.TargetPedestal = pedestal;
                controller.LoadRac2PreviewOnCardPress = false;
                controller.WearAvatarOnCardPress = false;
                controller.CardRoots = roots;
                controller.CardImages = images;
                controller.CardNameTexts = names;
                controller.CardCreatorTexts = creators;
                controller.CardSelectionFrames = frames;
                controller.PageText = pageText;
                controller.StatusText = status;
                controller.SelectedNameText = selectedName;
                controller.SelectedCreatorText = selectedCreator;
                controller.SelectedProductUrlText = selectedUrl;
                controller.TryAvatarButtonRoot = tryAvatar.gameObject;
                controller.AtlasUrls = new VRC.SDKBase.VRCUrl[12];
                controller.Rac2SlotUrls = new VRC.SDKBase.VRCUrl[128];
                tryAvatar.gameObject.SetActive(false);
                foreach (GameObject card in roots) card.SetActive(false);

                UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
                UdonSharpEditorUtility.CopyProxyToUdon(controller, ProxySerializationPolicy.All);
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, OutputPrefab);
                if (saved == null) throw new InvalidOperationException("Failed to save managed catalog prefab.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
            AssetDatabase.SaveAssets();
            Validate();
            Debug.Log("[RAC2 Managed Catalog Prefab] PASS - " + OutputPrefab);
        }

        private static Canvas CreateCanvas(Transform parent)
        {
            GameObject value = new GameObject("Managed Catalog Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            value.transform.SetParent(parent, false);
            value.transform.localPosition = new Vector3(-.8f, 1.45f, 0f);
            value.transform.localScale = Vector3.one * .001f;
            RectTransform rect = value.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(1480f, 1160f);
            Canvas canvas = value.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 35;
            return canvas;
        }

        private static Button ButtonAt(Transform parent, string name, string label, Vector2 position, Vector2 size)
        {
            GameObject value = Ui(name, parent);
            RectTransform rect = value.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            Image image = value.AddComponent<Image>();
            image.color = new Color(.1f, .43f, .6f, 1f);
            Button button = value.AddComponent<Button>();
            button.targetGraphic = image;
            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.None;
            button.navigation = navigation;
            TextAt(value.transform, "Label", label, Vector2.zero, size, 18, Color.white);
            return button;
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
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
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

        private static void Add(Button button, UdonBehaviour backing, string eventName)
        {
            UnityAction<string> action = backing.SendCustomEvent;
            UnityEventTools.AddStringPersistentListener(button.onClick, action, eventName);
        }

        private static Material EnsureMaterial()
        {
            const string folder = "Assets/RemoteAvatarCatalogDistribution/Materials";
            const string path = folder + "/RAC2-Managed-Catalog-Pedestal.mat";
            EnsureFolder(folder);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader shader = Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("Standard shader is missing.");
            if (material == null)
            {
                material = new Material(shader) { name = "RAC2 Managed Catalog Pedestal" };
                AssetDatabase.CreateAsset(material, path);
            }
            material.color = new Color(.08f, .46f, .64f, 1f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void EnsureProgram()
        {
            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(ScriptPath);
            if (script == null || script.GetClass() != typeof(Rac2ManagedCatalogController))
                throw new InvalidOperationException("Rac2ManagedCatalogController has not finished compiling.");
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
        }

        private static void Validate()
        {
            GameObject value = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPrefab);
            if (value == null) throw new InvalidOperationException("Managed catalog prefab is missing.");
            Rac2ManagedCatalogController controller = value.GetComponent<Rac2ManagedCatalogController>();
            if (controller == null || controller.TargetPedestal == null ||
                controller.CardRoots == null || controller.CardRoots.Length != 12 ||
                controller.CardImages == null || controller.CardImages.Length != 12 ||
                controller.AtlasUrls == null || controller.AtlasUrls.Length != 12 ||
                controller.Rac2SlotUrls == null || controller.Rac2SlotUrls.Length != 128)
                throw new InvalidOperationException("Managed catalog prefab references are incomplete.");
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
