using System;
using System.IO;
using System.Linq;
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
    /// <summary>Builds and validates the public Complete RAC2 0.2.4 distribution.</summary>
    [InitializeOnLoad]
    public static class Rac2ReleaseBuilder
    {
        private const string RequestPath = "Library/Rac2Release.build";
        private const string ResultPath = "Library/Rac2Release.build.result";
        private const string SourcePrefab = "Assets/NightSlotMall/Prefabs/RAC-3D-ImagePad.prefab";
        private const string DistributionRoot = "Assets/RemoteAvatarCatalogDistribution";
        private const string PrefabFolder = DistributionRoot + "/Prefabs";
        private const string UdonFolder = DistributionRoot + "/Udon";
        private const string ImagePadPrefab = PrefabFolder + "/RAC2-ImagePad.prefab";
        private const string IntegratedPrefab = PrefabFolder + "/RAC2-ImagePad-Pedestal.prefab";
        private const string PedestalPrefab = PrefabFolder + "/RAC2-Product-Pedestal.prefab";
        private const string ProductControllerScript = "Assets/com.avatarcatalog.remote/Runtime/Rac2ProductController.cs";
        private const string ProductControllerProgram = UdonFolder + "/Rac2ProductController.asset";
        private const string ProductLoaderScript = "Assets/com.avatarcatalog.remote/Runtime/Rac2ProductLoader.cs";
        private const string ProductLoaderProgram = UdonFolder + "/Rac2ProductLoader.asset";
        private const string BuildFolder = "Builds/RemoteAvatarCatalog-Complete-RAC2-0.2.4";
        private const string UnityPackage = BuildFolder + "/RemoteAvatarCatalog-Complete-RAC2-0.2.4.unitypackage";

        static Rac2ReleaseBuilder()
        {
            EditorApplication.update += ConsumeRequest;
        }

        [MenuItem("Tools/Avatar Catalog/Developer/Build/Build Complete RAC2 Release Package")]
        public static void BuildRelease()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before building the RAC2 release.");
            if (AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefab) == null)
                throw new InvalidOperationException("Verified ImagePad source prefab is missing: " + SourcePrefab);

            Rac2CompositePadInstaller.Install();

            EnsureFolder(PrefabFolder);
            EnsureFolder(UdonFolder);
            EnsureProgram(ProductControllerScript, ProductControllerProgram, typeof(Rac2ProductController));
            EnsureProgram(ProductLoaderScript, ProductLoaderProgram, typeof(Rac2ProductLoader));
            AssetDatabase.SaveAssets();
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
            if (UdonSharpProgramAsset.AnyUdonSharpScriptHasError())
                throw new InvalidOperationException("Cannot package a runtime with UdonSharp compile errors.");
            AssetDatabase.SaveAssets();

            BuildImagePad(false, ImagePadPrefab);
            BuildImagePad(true, IntegratedPrefab);
            BuildStandalonePedestal();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            ValidatePrefabs();

            Directory.CreateDirectory(Path.GetFullPath(BuildFolder));
            AssetDatabase.ExportPackage(
                new[]
                {
                    ImagePadPrefab,
                    IntegratedPrefab,
                    PedestalPrefab,
                    DistributionRoot + "/README-JA.md",
                    DistributionRoot + "/DEPENDENCIES-JA.md",
                    "Assets/com.avatarcatalog.remote/package.json",
                    "Assets/com.avatarcatalog.remote/README-RAC2-JA.md",
                    "Assets/com.avatarcatalog.remote/Authoring/AvatarCatalog.Remote.Authoring.asmdef",
                    "Assets/com.avatarcatalog.remote/Authoring/Rac2ProductMetadata.cs",
                    "Assets/com.avatarcatalog.remote/Runtime/AvatarCatalog.Remote.Runtime.asmdef",
                    "Assets/com.avatarcatalog.remote/Runtime/Rac2RuntimeLoader.cs",
                    "Assets/com.avatarcatalog.remote/Runtime/Rac2ParticlePlayer.cs",
                    "Assets/com.avatarcatalog.remote/Runtime/Rac2ProductController.cs",
                    "Assets/com.avatarcatalog.remote/Runtime/Rac2ProductLoader.cs",
                    "Assets/com.avatarcatalog.remote/Editor/AvatarCatalog.Remote.Editor.asmdef",
                    "Assets/com.avatarcatalog.remote/Editor/Rac2BinaryExporter.cs",
                    "Assets/com.avatarcatalog.remote/Editor/Rac2BundleBinaryExporter.cs",
                    "Assets/com.avatarcatalog.remote/Editor/Rac2CreatorWindow.cs",
                    "Assets/com.avatarcatalog.remote/Editor/Rac2CreatorWindow.BoothGuide.cs",
                    "Assets/com.avatarcatalog.remote/Editor/Rac2ProductMetadataUtility.cs",
                    "Assets/com.avatarcatalog.remote/Shaders/Rac2NormalEncode.shader",
                },
                UnityPackage,
                ExportPackageOptions.IncludeDependencies);
            Debug.Log("[RAC2 Release] PASS - public Creator, runtime and three prefabs packed at " + UnityPackage);
        }

        private static void ConsumeRequest()
        {
            string request = Path.GetFullPath(RequestPath);
            if (!File.Exists(request)) return;
            File.Delete(request);
            try
            {
                BuildRelease();
                File.WriteAllText(Path.GetFullPath(ResultPath), "PASS\n" +
                    Path.GetFullPath(UnityPackage));
            }
            catch (Exception exception)
            {
                File.WriteAllText(Path.GetFullPath(ResultPath),
                    "FAIL\n" + exception);
                Debug.LogException(exception);
            }
        }
        private static void BuildImagePad(bool includePedestal, string outputPath)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(SourcePrefab);
            try
            {
                RemoveLegacy(root);
                Transform previous = root.transform.Find("RAC2 Product Pedestal Module");
                if (previous != null) UnityEngine.Object.DestroyImmediate(previous.gameObject);
                root.name = includePedestal ? "RAC2 ImagePad + Product Pedestal" : "RAC2 ImagePad";
                root.transform.localPosition = Vector3.zero;
                root.transform.localRotation = Quaternion.identity;
                root.transform.localScale = Vector3.one;

                Rac2RuntimeLoader loader = root.GetComponentInChildren<Rac2RuntimeLoader>(true);
                if (loader == null) throw new InvalidOperationException("ImagePad RAC2 loader is missing.");
                loader.ProductController = null;
                if (includePedestal)
                {
                    Rac2ProductController controller = BuildProductModule(root.transform, true);
                    loader.ProductController = controller;
                    UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
                    UdonSharpEditorUtility.CopyProxyToUdon(controller, ProxySerializationPolicy.All);
                }
                UdonSharpEditorUtility.CopyProxyToUdon(loader, ProxySerializationPolicy.All);
                if (PrefabUtility.SaveAsPrefabAsset(root, outputPath) == null)
                    throw new InvalidOperationException("Unity failed to save " + outputPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void BuildStandalonePedestal()
        {
            GameObject root = new GameObject("RAC2 Product Pedestal");
            try
            {
                Rac2ProductController controller = BuildProductModule(root.transform, false);
                Rac2ProductLoader loader = root.AddUdonSharpComponent<Rac2ProductLoader>();
                UdonBehaviour loaderBacking = UdonSharpEditorUtility.GetBackingUdonBehaviour(loader);
                if (loaderBacking == null) throw new InvalidOperationException("Product loader backing UdonBehaviour is missing.");

                Canvas inputCanvas = CreateCanvas("RAC2 Product Loader Canvas", root.transform, new Vector3(0f, 1.8f, 0f), new Vector2(900f, 330f));
                GameObject panel = Ui("Loader Panel", inputCanvas.transform);
                Stretch(panel.GetComponent<RectTransform>());
                panel.AddComponent<Image>().color = new Color(.025f, .035f, .055f, .98f);
                TextAt(panel.transform, "Title", "RAC2 PRODUCT PEDESTAL", new Vector2(0f, 120f), new Vector2(840f, 44f), 28, new Color(.68f, .94f, 1f, 1f));
                VRCUrlInputField input = CreateUrlInput(panel.transform, new Vector2(0f, 58f));
                Button loadButton = ButtonAt(panel.transform, "Load", "LOAD PRODUCT", new Vector2(-205f, -20f), new Vector2(250f, 54f));
                Button retryButton = ButtonAt(panel.transform, "Retry", "RETRY", new Vector2(0f, -20f), new Vector2(130f, 54f));
                Button clearButton = ButtonAt(panel.transform, "Clear", "CLEAR", new Vector2(160f, -20f), new Vector2(130f, 54f));
                Text status = TextAt(panel.transform, "Status", "Paste a public HTTPS .rac2 URL.", new Vector2(0f, -105f), new Vector2(840f, 60f), 18, new Color(.42f, .95f, .76f, 1f));
                Add(loadButton, loaderBacking, "LoadFromInput");
                Add(retryButton, loaderBacking, "Retry");
                Add(clearButton, loaderBacking, "Clear");
                loader.UrlInput = input;
                loader.ProductController = controller;
                loader.StatusText = status;

                UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
                UdonSharpEditorUtility.CopyProxyToUdon(controller, ProxySerializationPolicy.All);
                UdonSharpEditorUtility.CopyProxyToUdon(loader, ProxySerializationPolicy.All);
                if (PrefabUtility.SaveAsPrefabAsset(root, PedestalPrefab) == null)
                    throw new InvalidOperationException("Unity failed to save " + PedestalPrefab);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static Rac2ProductController BuildProductModule(Transform parent, bool integrated)
        {
            GameObject module = new GameObject("RAC2 Product Pedestal Module");
            module.transform.SetParent(parent, false);
            module.transform.localPosition = integrated ? new Vector3(2.1f, 0f, 0f) : Vector3.zero;

            GameObject pedestalHost = new GameObject("Avatar Pedestal Host");
            pedestalHost.transform.SetParent(module.transform, false);
            VRCAvatarPedestal pedestal = pedestalHost.AddComponent<VRCAvatarPedestal>();

            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            visual.name = "Pedestal Visual";
            visual.transform.SetParent(pedestalHost.transform, false);
            visual.transform.localPosition = new Vector3(0f, .12f, 0f);
            visual.transform.localScale = new Vector3(.75f, .12f, .75f);
            Renderer visualRenderer = visual.GetComponent<Renderer>();
            if (visualRenderer != null) visualRenderer.sharedMaterial = EnsurePedestalMaterial();

            Canvas canvas = CreateCanvas("Product Canvas", module.transform, new Vector3(0f, 1.25f, 0f), new Vector2(720f, 520f));
            GameObject panel = Ui("Product Display", canvas.transform);
            Stretch(panel.GetComponent<RectTransform>());
            panel.AddComponent<Image>().color = new Color(.035f, .045f, .07f, .97f);
            Text name = TextAt(panel.transform, "Product Name", "Product", new Vector2(0f, 170f), new Vector2(660f, 70f), 34, new Color(.68f, .94f, 1f, 1f));
            Text creator = TextAt(panel.transform, "Creator", "", new Vector2(0f, 110f), new Vector2(660f, 42f), 22, Color.white);
            Text url = TextAt(panel.transform, "Product URL", "", new Vector2(0f, 35f), new Vector2(650f, 90f), 16, new Color(.7f, .78f, .9f, 1f));
            Button trial = ButtonAt(panel.transform, "Try Avatar", "TRY AVATAR", new Vector2(0f, -75f), new Vector2(300f, 64f));
            Text status = TextAt(panel.transform, "Product Status", "No product metadata", new Vector2(0f, -155f), new Vector2(650f, 52f), 18, new Color(.42f, .95f, .76f, 1f));

            Rac2ProductController controller = module.AddUdonSharpComponent<Rac2ProductController>();
            UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(controller);
            if (backing == null) throw new InvalidOperationException("Product controller backing UdonBehaviour is missing.");
            Add(trial, backing, "TryAvatar");
            controller.ProductDisplayRoot = panel;
            controller.ProductNameText = name;
            controller.CreatorText = creator;
            controller.ProductUrlText = url;
            controller.StatusText = status;
            controller.TargetPedestal = pedestal;
            controller.PedestalVisualRoot = visual;
            controller.TrialButtonRoot = trial.gameObject;
            controller.HasProduct = false;
            controller.TrialEnabled = false;
            panel.SetActive(false);
            visual.SetActive(false);
            return controller;
        }

        private static Canvas CreateCanvas(string name, Transform parent, Vector3 position, Vector2 size)
        {
            GameObject value = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            value.transform.SetParent(parent, false);
            value.transform.localPosition = position;
            value.transform.localRotation = Quaternion.identity;
            value.transform.localScale = Vector3.one * .001f;
            RectTransform rect = value.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            Canvas canvas = value.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 30;
            CanvasScaler scaler = value.GetComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 10f;
            return canvas;
        }

        private static VRCUrlInputField CreateUrlInput(Transform parent, Vector2 position)
        {
            GameObject value = Ui("RAC2 URL Input", parent);
            RectTransform rect = value.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(820f, 58f);
            Image image = value.AddComponent<Image>();
            image.color = new Color(.11f, .14f, .19f, 1f);
            Text placeholder = TextAt(value.transform, "Placeholder", "https://example.com/product.rac2", Vector2.zero, new Vector2(780f, 48f), 18, new Color(.56f, .62f, .70f, 1f));
            placeholder.fontStyle = FontStyle.Italic;
            Text text = TextAt(value.transform, "Text", "", Vector2.zero, new Vector2(780f, 48f), 18, Color.white);
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
            image.color = new Color(.12f, .42f, .58f, 1f);
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

        private static Material EnsurePedestalMaterial()
        {
            const string folder = DistributionRoot + "/Materials";
            const string path = folder + "/RAC2-Product-Pedestal.mat";
            EnsureFolder(folder);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader shader = Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("Standard shader is missing.");
            if (material == null)
            {
                material = new Material(shader) { name = "RAC2 Product Pedestal" };
                AssetDatabase.CreateAsset(material, path);
            }
            material.color = new Color(.08f, .42f, .58f, 1f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void RemoveLegacy(GameObject root)
        {
            string[] names = { "Legacy RAC1 Display (disabled)", "Runtime Display", "GLB Runtime Parent" };
            foreach (string name in names)
            {
                Transform value = root.transform.Find(name);
                if (value != null) UnityEngine.Object.DestroyImmediate(value.gameObject);
            }
            if (root.GetComponentsInChildren<RemoteAvatarCatalogLoader>(true).Length != 0)
                throw new InvalidOperationException("ImagePad still contains a legacy RAC1 loader.");
        }

        private static void ValidatePrefabs()
        {
            GameObject imagePad = AssetDatabase.LoadAssetAtPath<GameObject>(ImagePadPrefab);
            GameObject integrated = AssetDatabase.LoadAssetAtPath<GameObject>(IntegratedPrefab);
            GameObject pedestal = AssetDatabase.LoadAssetAtPath<GameObject>(PedestalPrefab);
            if (imagePad == null || integrated == null || pedestal == null)
                throw new InvalidOperationException("One or more release prefabs are missing.");
            if (imagePad.GetComponentsInChildren<Rac2RuntimeLoader>(true).Length != 1 ||
                imagePad.GetComponentsInChildren<Rac2ProductController>(true).Length != 0)
                throw new InvalidOperationException("ImagePad-only prefab topology is invalid.");
            ValidateCompleteImagePadPool(imagePad, "ImagePad-only");
            Rac2RuntimeLoader integratedLoader = integrated.GetComponentInChildren<Rac2RuntimeLoader>(true);
            Rac2ProductController integratedProduct = integrated.GetComponentInChildren<Rac2ProductController>(true);
            if (integratedLoader == null || integratedProduct == null || integratedLoader.ProductController != integratedProduct ||
                integrated.GetComponentsInChildren<VRCAvatarPedestal>(true).Length != 1)
                throw new InvalidOperationException("Integrated prefab topology or references are invalid.");
            ValidateCompleteImagePadPool(integrated, "Integrated ImagePad");
            Rac2ProductLoader productLoader = pedestal.GetComponentInChildren<Rac2ProductLoader>(true);
            Rac2ProductController productController = pedestal.GetComponentInChildren<Rac2ProductController>(true);
            if (productLoader == null || productController == null || productLoader.ProductController != productController ||
                pedestal.GetComponentsInChildren<Rac2RuntimeLoader>(true).Length != 0 ||
                pedestal.GetComponentsInChildren<VRCAvatarPedestal>(true).Length != 1)
                throw new InvalidOperationException("Standalone pedestal prefab topology or references are invalid.");
            if (!File.Exists(Path.GetFullPath(DistributionRoot + "/README-JA.md")) ||
                !File.Exists(Path.GetFullPath(DistributionRoot + "/DEPENDENCIES-JA.md")))
                throw new InvalidOperationException("Distribution README files are missing.");

            string[] dependencies = AssetDatabase.GetDependencies(new[] { ImagePadPrefab, IntegratedPrefab, PedestalPrefab }, true);
            if (dependencies.Any(path => path.EndsWith("RemoteAvatarCatalogLoader.asset", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Release prefabs unexpectedly depend on the legacy RAC1 loader.");
        }

        private static void ValidateCompleteImagePadPool(GameObject root, string label)
        {
            Rac2RuntimeLoader loader =
                root.GetComponentInChildren<Rac2RuntimeLoader>(true);
            RuntimeRacImagePadControllerV2 controller =
                root.GetComponentInChildren<RuntimeRacImagePadControllerV2>(true);
            if (controller == null || controller.AdaptiveLoadingButton == null ||
                controller.AdaptiveLoadingText == null ||
                !controller.Rac2AdaptiveLoadingEnabled)
                throw new InvalidOperationException(
                    label + " does not contain the RAC2 adaptive loading control.");
            if (loader == null || !loader.AdaptiveLoadingEnabled ||
                loader.FixedLoadingPerformanceMode !=
                    Rac2RuntimeLoader.LoadPerformanceBalanced ||
                loader.SceneObjects == null ||
                loader.SceneObjects.Length < 16 ||
                loader.SceneMeshFilters == null ||
                loader.SceneMeshFilters.Length < 16 ||
                loader.SceneRenderers == null ||
                loader.SceneRenderers.Length < 16 ||
                loader.ParticlePlayers == null ||
                loader.ParticlePlayers.Length < 4)
                throw new InvalidOperationException(
                    label + " does not contain the complete RAC2 v3 pools.");

            ValidateBackingArray(loader, "SceneMeshFilters", 16, label);
            ValidateBackingArray(loader, "ParticlePlayers", 4, label);
            for (int index = 0; index < 4; index++)
            {
                Rac2ParticlePlayer player = loader.ParticlePlayers[index];
                if (player == null || player.PoolObjects == null ||
                    player.PoolObjects.Length < 32)
                    throw new InvalidOperationException(
                        label + " particle pool " + index + " is incomplete.");
                ValidateBackingArray(player, "PoolObjects", 32,
                    label + " particle pool " + index);
            }
        }

        private static void ValidateBackingArray(
            UdonSharpBehaviour proxy, string symbol, int minimum, string label)
        {
            UdonBehaviour backing =
                UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy);
            object value;
            Array array;
            if (backing == null ||
                !backing.publicVariables.TryGetVariableValue(symbol, out value) ||
                (array = value as Array) == null || array.Length < minimum)
                throw new InvalidOperationException(
                    label + " did not serialize Udon field " + symbol + ".");
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
