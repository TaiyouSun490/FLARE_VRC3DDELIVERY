using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AvatarCatalog.Remote
{
    /// <summary>
    /// Builds a deterministic side-by-side Editor scene that exports X Bot to
    /// RAC1 and reconstructs it through the actual runtime loader decoder.
    /// </summary>
    public static class Rac1RoundTripTest
    {
        private const string MenuPath = "Tools/Avatar Catalog/Developer/Tests/RAC1/Create X Bot Round-Trip Test";
        private const string XBotAssetPath = "Assets/TestAssets/X Bot.fbx";
        private const string OutputFolder = "Assets/AvatarCatalogRoundTrip";
        private const string Rac1AssetPath = OutputFolder + "/XBot.rac1";
        private const string MeshAssetPath = OutputFolder + "/XBot-Restored-Mesh.asset";
        private const string MaterialAssetPath = OutputFolder + "/XBot-Restored-Material.mat";
        private const string TextureAssetPath = OutputFolder + "/XBot-Restored-Albedo.asset";
        private const string SceneAssetPath = OutputFolder + "/XBot-RoundTrip.unity";
        private const string ScreenshotAssetPath = OutputFolder + "/XBot-RoundTrip.png";
        private const string LoaderTypeName = "AvatarCatalog.Remote.RemoteAvatarCatalogLoader";

        [MenuItem(MenuPath)]
        public static void CreateXBotTestScene()
        {
            try
            {
                RunRoundTrip();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                if (!Application.isBatchMode)
                {
                    EditorUtility.DisplayDialog("RAC1 round-trip failed", exception.Message, "OK");
                }

                throw;
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateCreateXBotTestScene()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private static void RunRoundTrip()
        {
            EnsureOutputFolder();
            ConfigureXBotImporter();

            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(XBotAssetPath);
            if (model == null)
            {
                throw new FileNotFoundException(
                    "X Bot was not found. Copy it to " + XBotAssetPath + " before running the test.",
                    XBotAssetPath);
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject sourceRoot = PrefabUtility.InstantiatePrefab(model) as GameObject;
            if (sourceRoot == null)
            {
                throw new InvalidOperationException("Unity could not instantiate the X Bot model asset.");
            }

            sourceRoot.name = "SOURCE - X Bot";
            sourceRoot.transform.position = new Vector3(-0.8f, 0f, 0f);
            SkinnedMeshRenderer sourceRenderer = FindPrimarySkinnedMeshRenderer(sourceRoot);
            if (sourceRenderer == null)
            {
                throw new InvalidOperationException("X Bot contains no SkinnedMeshRenderer.");
            }

            // X Bot contains separate Surface and Joints renderers. RAC1 v1 is a
            // single-renderer format, so make the reference view show exactly the
            // same primary renderer that is exported and reconstructed.
            foreach (SkinnedMeshRenderer renderer in sourceRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                renderer.enabled = renderer == sourceRenderer;
            }

            sourceRenderer.updateWhenOffscreen = true;
            string rac1AbsolutePath = AssetPathToAbsolute(Rac1AssetPath);
            Rac1BinaryExporter.ExportSummary exportSummary = Rac1BinaryExporter.Export(
                sourceRenderer,
                rac1AbsolutePath,
                new Rac1BinaryExporter.Options
                {
                    IncludeAlbedoTexture = true,
                    MaximumTextureDimension = 1024
                });

            GameObject restoredRoot = new GameObject("RAC1 RESTORED - X Bot");
            restoredRoot.transform.position = new Vector3(0.8f, 0f, 0f);
            MeshFilter targetMeshFilter = restoredRoot.AddComponent<MeshFilter>();
            MeshRenderer targetRenderer = restoredRoot.AddComponent<MeshRenderer>();

            Shader shader = Shader.Find("Standard");
            if (shader == null)
            {
                throw new InvalidOperationException("Unity's Standard shader was not found.");
            }

            Material decoderTemplate = new Material(shader)
            {
                name = "RAC1 Round-Trip Decoder Template"
            };

            Component loader = null;
            try
            {
                Type loaderType = FindRuntimeLoaderType();
                loader = restoredRoot.AddComponent(loaderType);
                SetPublicField(loaderType, loader, "TargetMeshFilter", targetMeshFilter);
                SetPublicField(loaderType, loader, "TargetRenderer", targetRenderer);
                SetPublicField(loaderType, loader, "MaterialTemplate", decoderTemplate);
                SetPublicField(loaderType, loader, "BaseColorProperty", "_Color");
                SetPublicField(loaderType, loader, "MainTextureProperty", "_MainTex");

                MethodInfo parseAndApply = loaderType.GetMethod(
                    "ParseAndApply",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (parseAndApply == null)
                {
                    throw new MissingMethodException(loaderType.FullName, "ParseAndApply");
                }

                byte[] payload = File.ReadAllBytes(rac1AbsolutePath);
                bool loaded = (bool)parseAndApply.Invoke(loader, new object[] { payload, 0 });
                if (!loaded)
                {
                    string parseError = ReadStringField(loaderType, loader, "LastError");
                    if (string.IsNullOrEmpty(parseError))
                    {
                        parseError = ReadStringField(loaderType, loader, "_parseError");
                    }

                    throw new InvalidDataException("Runtime RAC1 decoder rejected X Bot: " + parseError);
                }

                PersistDecodedAssets(targetMeshFilter, targetRenderer);
            }
            finally
            {
                if (loader != null)
                {
                    UnityEngine.Object.DestroyImmediate(loader);
                }

                UnityEngine.Object.DestroyImmediate(decoderTemplate);
            }

            Mesh restoredMesh = targetMeshFilter.sharedMesh;
            if (restoredMesh == null)
            {
                throw new InvalidOperationException("The runtime decoder did not assign a Mesh.");
            }

            if (restoredMesh.vertexCount != exportSummary.VertexCount ||
                restoredMesh.triangles.Length != exportSummary.IndexCount)
            {
                throw new InvalidDataException(
                    $"Round-trip counts differ. Exported {exportSummary.VertexCount:N0} vertices / " +
                    $"{exportSummary.IndexCount:N0} indices, restored {restoredMesh.vertexCount:N0} / " +
                    $"{restoredMesh.triangles.Length:N0}.");
            }

            AddEnvironment(sourceRoot, restoredRoot);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, SceneAssetPath))
            {
                throw new IOException("Unity could not save the round-trip scene at " + SceneAssetPath);
            }

            Camera camera = UnityEngine.Object.FindObjectOfType<Camera>();
            if (camera == null)
            {
                throw new InvalidOperationException("The test camera was not created.");
            }

            RenderScreenshot(camera, ScreenshotAssetPath);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Selection.activeGameObject = restoredRoot;
            SceneView.lastActiveSceneView?.FrameSelected();
            SceneView.lastActiveSceneView?.ShowNotification(
                new GUIContent("X Bot RAC1 round-trip succeeded"),
                4f);

            string textureNote = string.IsNullOrEmpty(exportSummary.TextureWarning)
                ? "texture included when available"
                : exportSummary.TextureWarning;
            Debug.Log(
                "[Remote Avatar Catalog] X Bot RAC1 round-trip succeeded. " +
                $"Vertices: {exportSummary.VertexCount:N0}, indices: {exportSummary.IndexCount:N0}, " +
                $"RAC1: {EditorUtility.FormatBytes(exportSummary.FileSize)}, {textureNote}. " +
                $"Source renderer: {sourceRenderer.name}. " +
                $"Scene: {SceneAssetPath}, screenshot: {ScreenshotAssetPath}");
        }

        private static void ConfigureXBotImporter()
        {
            ModelImporter importer = AssetImporter.GetAtPath(XBotAssetPath) as ModelImporter;
            if (importer == null)
            {
                AssetDatabase.ImportAsset(XBotAssetPath, ImportAssetOptions.ForceSynchronousImport);
                importer = AssetImporter.GetAtPath(XBotAssetPath) as ModelImporter;
            }

            if (importer == null)
            {
                throw new FileNotFoundException("X Bot model importer was not found at " + XBotAssetPath);
            }

            bool changed = false;
            if (!importer.isReadable)
            {
                importer.isReadable = true;
                changed = true;
            }

            if (importer.importNormals == ModelImporterNormals.None)
            {
                importer.importNormals = ModelImporterNormals.Import;
                changed = true;
            }

            if (changed)
            {
                importer.SaveAndReimport();
            }
        }

        private static SkinnedMeshRenderer FindPrimarySkinnedMeshRenderer(GameObject root)
        {
            SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRenderer largest = null;
            int largestVertexCount = -1;

            foreach (SkinnedMeshRenderer renderer in renderers)
            {
                int vertexCount = renderer.sharedMesh != null ? renderer.sharedMesh.vertexCount : -1;
                if (vertexCount > largestVertexCount)
                {
                    largest = renderer;
                    largestVertexCount = vertexCount;
                }
            }

            return largest;
        }

        private static Type FindRuntimeLoaderType()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(LoaderTypeName, false);
                if (type != null)
                {
                    return type;
                }
            }

            throw new TypeLoadException(
                LoaderTypeName + " is not loaded. Ensure the Remote Avatar Catalog runtime assembly compiled.");
        }

        private static void SetPublicField(Type type, object instance, string fieldName, object value)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
            if (field == null)
            {
                throw new MissingFieldException(type.FullName, fieldName);
            }

            field.SetValue(instance, value);
        }

        private static string ReadStringField(Type type, object instance, string fieldName)
        {
            FieldInfo field = type.GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(instance) as string;
        }

        private static void PersistDecodedAssets(MeshFilter meshFilter, MeshRenderer meshRenderer)
        {
            Mesh decodedMesh = meshFilter.sharedMesh;
            Material decodedMaterial = meshRenderer.sharedMaterial;
            if (decodedMesh == null || decodedMaterial == null)
            {
                throw new InvalidOperationException("Runtime decoder did not create both Mesh and Material.");
            }

            DeleteOutputAsset(MeshAssetPath);
            DeleteOutputAsset(MaterialAssetPath);
            DeleteOutputAsset(TextureAssetPath);

            Mesh meshAsset = UnityEngine.Object.Instantiate(decodedMesh);
            meshAsset.name = "X Bot RAC1 Restored Mesh";
            AssetDatabase.CreateAsset(meshAsset, MeshAssetPath);

            Texture decodedTexture = decodedMaterial.HasProperty("_MainTex")
                ? decodedMaterial.GetTexture("_MainTex")
                : null;
            Texture2D textureAsset = null;
            if (decodedTexture is Texture2D decodedTexture2D)
            {
                textureAsset = UnityEngine.Object.Instantiate(decodedTexture2D);
                textureAsset.name = "X Bot RAC1 Restored Albedo";
                AssetDatabase.CreateAsset(textureAsset, TextureAssetPath);
            }

            Material materialAsset = new Material(decodedMaterial)
            {
                name = "X Bot RAC1 Restored Material"
            };
            if (textureAsset != null && materialAsset.HasProperty("_MainTex"))
            {
                materialAsset.SetTexture("_MainTex", textureAsset);
            }
            AssetDatabase.CreateAsset(materialAsset, MaterialAssetPath);

            meshFilter.sharedMesh = meshAsset;
            meshRenderer.sharedMaterial = materialAsset;
            EditorUtility.SetDirty(meshFilter);
            EditorUtility.SetDirty(meshRenderer);
            AssetDatabase.SaveAssets();
        }

        private static void AddEnvironment(GameObject sourceRoot, GameObject restoredRoot)
        {
            Bounds bounds = CalculateBounds(sourceRoot, restoredRoot);
            float labelHeight = bounds.max.y + 0.15f;
            CreateLabel("SOURCE", new Vector3(sourceRoot.transform.position.x, labelHeight, 0f));
            CreateLabel("RAC1 RESTORED", new Vector3(restoredRoot.transform.position.x, labelHeight, 0f));

            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            ground.transform.localScale = new Vector3(0.45f, 1f, 0.25f);
            Material groundMaterial = new Material(Shader.Find("Standard"));
            groundMaterial.color = new Color(0.08f, 0.09f, 0.1f, 1f);
            ground.GetComponent<Renderer>().sharedMaterial = groundMaterial;

            GameObject keyLightObject = new GameObject("Key Light");
            Light keyLight = keyLightObject.AddComponent<Light>();
            keyLight.type = LightType.Directional;
            keyLight.intensity = 1.2f;
            keyLight.color = new Color(1f, 0.95f, 0.86f);
            keyLightObject.transform.rotation = Quaternion.Euler(45f, 150f, 0f);

            GameObject fillLightObject = new GameObject("Fill Light");
            Light fillLight = fillLightObject.AddComponent<Light>();
            fillLight.type = LightType.Directional;
            fillLight.intensity = 0.7f;
            fillLight.color = new Color(0.55f, 0.7f, 1f);
            fillLightObject.transform.rotation = Quaternion.Euler(25f, -45f, 0f);

            GameObject cameraObject = new GameObject("Round-Trip Camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.03f, 0.04f, 1f);
            camera.fieldOfView = 35f;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 100f;
            PositionCamera(camera, bounds);
        }

        private static Bounds CalculateBounds(params GameObject[] roots)
        {
            bool initialized = false;
            Bounds combined = default;
            foreach (GameObject root in roots)
            {
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!initialized)
                    {
                        combined = renderer.bounds;
                        initialized = true;
                    }
                    else
                    {
                        combined.Encapsulate(renderer.bounds);
                    }
                }
            }

            if (!initialized)
            {
                throw new InvalidOperationException("No render bounds were found for the test models.");
            }

            return combined;
        }

        private static void PositionCamera(Camera camera, Bounds bounds)
        {
            float aspect = 16f / 9f;
            float verticalHalfAngle = camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float requiredHeight = Mathf.Max(bounds.size.y * 0.62f, 0.5f);
            float requiredWidth = Mathf.Max(bounds.size.x * 0.62f / aspect, 0.5f);
            float distance = Mathf.Max(requiredHeight, requiredWidth) / Mathf.Tan(verticalHalfAngle);
            distance += bounds.extents.z + 0.5f;
            Vector3 target = bounds.center + Vector3.up * bounds.size.y * 0.04f;
            camera.transform.position = target + Vector3.forward * distance;
            camera.transform.LookAt(target, Vector3.up);
        }

        private static void CreateLabel(string text, Vector3 position)
        {
            GameObject labelObject = new GameObject(text + " Label");
            labelObject.transform.position = position;
            labelObject.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            TextMesh label = labelObject.AddComponent<TextMesh>();
            label.text = text;
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.fontSize = 64;
            label.characterSize = 0.035f;
            label.color = text.StartsWith("RAC1", StringComparison.Ordinal)
                ? new Color(0.69f, 1f, 0.16f)
                : Color.white;
        }

        private static void RenderScreenshot(Camera camera, string assetPath)
        {
            const int width = 1280;
            const int height = 720;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            Texture2D screenshot = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                screenshot.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                screenshot.Apply(false, false);
                File.WriteAllBytes(AssetPathToAbsolute(assetPath), screenshot.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(screenshot);
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        private static void EnsureOutputFolder()
        {
            string absolute = AssetPathToAbsolute(OutputFolder);
            Directory.CreateDirectory(absolute);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        private static string AssetPathToAbsolute(string assetPath)
        {
            if (!assetPath.StartsWith("Assets", StringComparison.Ordinal))
            {
                throw new ArgumentException("Expected an Assets-relative path: " + assetPath, nameof(assetPath));
            }

            string relative = assetPath.Length == "Assets".Length
                ? string.Empty
                : assetPath.Substring("Assets/".Length);
            return Path.Combine(Application.dataPath, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void DeleteOutputAsset(string assetPath)
        {
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
            {
                if (!AssetDatabase.DeleteAsset(assetPath))
                {
                    throw new IOException("Unity could not replace generated test asset " + assetPath);
                }
            }
        }
    }
}
