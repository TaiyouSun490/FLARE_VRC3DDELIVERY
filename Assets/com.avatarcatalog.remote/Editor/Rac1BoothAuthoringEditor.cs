using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    [CustomEditor(typeof(Rac1BoothAuthoring))]
    public sealed class Rac1BoothAuthoringEditor : Editor
    {
        private const string CreateMenuPath = "GameObject/Avatar Catalog/Create RAC1 Booth Authoring Root";

        private SerializedProperty avatarRootProperty;
        private SerializedProperty shopRootProperty;
        private SerializedProperty showPreviewProperty;
        private SerializedProperty showFloorGuideProperty;

        private void OnEnable()
        {
            avatarRootProperty = serializedObject.FindProperty("AvatarRoot");
            shopRootProperty = serializedObject.FindProperty("ShopVisualRoot");
            showPreviewProperty = serializedObject.FindProperty("showCapturedPreview");
            showFloorGuideProperty = serializedObject.FindProperty("showFloorGuide");
            ((Rac1BoothAuthoring)target).RefreshFloorGuide();
        }

        public override void OnInspectorGUI()
        {
            Rac1BoothAuthoring authoring = (Rac1BoothAuthoring)target;
            serializedObject.Update();

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Weekly Mall Booth Snapshot", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Profile", Rac1BoothAuthoring.ProfileId);
                EditorGUILayout.Vector3Field("Content Size (m)", Rac1BoothAuthoring.ProfileSize);
            }

            EditorGUILayout.HelpBox(
                "Place BoothRoot at the floor centre. +Y is up and +Z faces the customer. " +
                "Every enabled mesh below the two roots is frozen into one static RAC1 mesh and one 1024 atlas.",
                MessageType.Info);

            if (!authoring.gameObject.CompareTag("EditorOnly"))
            {
                EditorGUILayout.HelpBox(
                    "BoothRoot must use the EditorOnly tag. This prevents the source avatar and shop assets from being bundled into the VRChat world.",
                    MessageType.Error);
                if (GUILayout.Button("Set BoothRoot tag to EditorOnly"))
                {
                    Undo.RecordObject(authoring.gameObject, "Mark BoothRoot EditorOnly");
                    authoring.gameObject.tag = "EditorOnly";
                    EditorUtility.SetDirty(authoring.gameObject);
                }
            }

            EditorGUILayout.PropertyField(avatarRootProperty, new GUIContent("Avatar Root"));
            EditorGUILayout.PropertyField(shopRootProperty, new GUIContent("Shop Visual Root"));
            EditorGUILayout.PropertyField(showFloorGuideProperty, new GUIContent("Show Floor Guide"));
            EditorGUILayout.PropertyField(showPreviewProperty, new GUIContent("Show Captured Preview"));
            serializedObject.ApplyModifiedProperties();
            authoring.ShowFloorGuide = showFloorGuideProperty.boolValue;
            authoring.ShowCapturedPreview = showPreviewProperty.boolValue;

            EditorGUILayout.Space(5f);
            DrawLimits();

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Validate", GUILayout.Height(28f)))
                    {
                        RunValidate(authoring);
                    }

                    if (GUILayout.Button("Capture / Refresh", GUILayout.Height(28f)))
                    {
                        RunCapture(authoring);
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!authoring.HasCapture))
                    {
                        if (GUILayout.Button("Clear Capture"))
                        {
                            authoring.ReleaseCapture();
                            SceneView.RepaintAll();
                        }
                    }

                    if (GUILayout.Button("Capture + Export RAC1..."))
                    {
                        RunExport(authoring);
                    }
                }
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorGUILayout.HelpBox("Booth authoring is disabled during Play Mode.", MessageType.Warning);
            }

            EditorGUILayout.HelpBox(
                "The upload is visual-only. Animator, PhysBone, particles, scripts, physics, lights and audio are not stored. " +
                "Use the package's Avatar Catalog/RAC1 Opaque Cutout shader as the runtime loader MaterialTemplate.",
                MessageType.None);

            DrawReport(authoring, authoring.LastReport);
        }

        [MenuItem(CreateMenuPath, false, 10)]
        private static void CreateBoothAuthoringRoot(MenuCommand command)
        {
            GameObject root = new GameObject("RAC1 Booth Root");
            root.tag = "EditorOnly";
            GameObjectUtility.SetParentAndAlign(root, command.context as GameObject);
            Undo.RegisterCreatedObjectUndo(root, "Create RAC1 Booth Authoring Root");

            GameObject avatar = new GameObject("Avatar Content");
            avatar.transform.SetParent(root.transform, false);
            Undo.RegisterCreatedObjectUndo(avatar, "Create Avatar Content Root");

            GameObject shop = new GameObject("Shop Visuals");
            shop.transform.SetParent(root.transform, false);
            Undo.RegisterCreatedObjectUndo(shop, "Create Shop Visual Root");

            Rac1BoothAuthoring authoring = Undo.AddComponent<Rac1BoothAuthoring>(root);
            authoring.AvatarRoot = avatar.transform;
            authoring.ShopVisualRoot = shop.transform;
            EditorUtility.SetDirty(authoring);

            Selection.activeGameObject = root;
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        private static void RunValidate(Rac1BoothAuthoring authoring)
        {
            try
            {
                Rac1BoothValidationReport report = Rac1BoothBaker.Validate(authoring);
                SceneView.RepaintAll();
                ShowSceneNotification(report.HasErrors
                    ? $"Booth validation failed: {report.ErrorCount} error(s)"
                    : $"Booth validation passed: {report.WarningCount} warning(s)");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Booth validation failed", exception.Message, "OK");
            }
        }

        private static void RunCapture(Rac1BoothAuthoring authoring)
        {
            try
            {
                Rac1BoothValidationReport report = Rac1BoothBaker.Capture(authoring);
                SceneView.RepaintAll();
                if (report.HasErrors)
                {
                    ShowSceneNotification($"Capture blocked: {report.ErrorCount} validation error(s)");
                    return;
                }

                ShowSceneNotification(
                    $"Captured {report.VertexCount:N0} vertices / {report.IndexCount:N0} indices");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Booth capture failed", exception.Message, "OK");
            }
        }

        private static void RunExport(Rac1BoothAuthoring authoring)
        {
            string defaultName = SanitizeFileName(authoring.gameObject.name);
            string outputPath = EditorUtility.SaveFilePanel(
                "Export static mall booth RAC1",
                Application.dataPath,
                defaultName,
                "rac1");
            if (string.IsNullOrEmpty(outputPath))
            {
                return;
            }

            try
            {
                Rac1BinaryExporter.ExportSummary summary = Rac1BoothBaker.Export(
                    authoring,
                    outputPath,
                    out Rac1BoothValidationReport report);

                if (IsInsideAssets(outputPath))
                {
                    AssetDatabase.Refresh();
                }

                EditorUtility.DisplayDialog(
                    "Booth RAC1 export complete",
                    $"{Path.GetFileName(outputPath)}\n" +
                    $"Renderers: {report.SourceRendererCount:N0}\n" +
                    $"Materials atlased: {report.MaterialCount:N0}\n" +
                    $"Vertices: {summary.VertexCount:N0}\n" +
                    $"Triangle indices: {summary.IndexCount:N0}\n" +
                    $"Atlas: {summary.TextureWidth} x {summary.TextureHeight} RGBA32\n" +
                    $"File size: {EditorUtility.FormatBytes(summary.FileSize)}\n" +
                    $"Warnings: {report.WarningCount:N0}",
                    "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Booth RAC1 export failed", exception.Message, "OK");
            }
        }

        private static void DrawLimits()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("MVP limits", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Geometry", "40,000 vertices / 120,000 indices");
                EditorGUILayout.LabelField("Texture", "one 1024 x 1024 RGBA atlas");
                EditorGUILayout.LabelField("Materials", "Opaque or AlphaClip; BaseMap/MainTex + base color");
                EditorGUILayout.LabelField("UV", "UV0 inside 0..1; finite Repeat domains can be fixed and baked");
                EditorGUILayout.LabelField("RAC1 size", "10 MB maximum");
                EditorGUILayout.LabelField("Boundary tolerance", "0.005 m");
            }
        }

        private static void DrawReport(
            Rac1BoothAuthoring authoring,
            Rac1BoothValidationReport report)
        {
            if (report == null)
            {
                return;
            }

            EditorGUILayout.Space(7f);
            EditorGUILayout.LabelField(
                $"Validation report - {report.ErrorCount} error(s), {report.WarningCount} warning(s)",
                EditorStyles.boldLabel);

            if (report.VertexCount > 0)
            {
                EditorGUILayout.LabelField(
                    "Flattened output",
                    $"{report.VertexCount:N0} vertices / {report.IndexCount:N0} indices / " +
                    EditorUtility.FormatBytes(report.EstimatedFileSize));
                EditorGUILayout.BoundsField("Actual local AABB", report.ContentBounds);
            }

            for (int i = 0; i < report.Messages.Count; i++)
            {
                Rac1BoothValidationMessage item = report.Messages[i];
                MessageType messageType = item.Severity == Rac1BoothMessageSeverity.Error
                    ? MessageType.Error
                    : item.Severity == Rac1BoothMessageSeverity.Warning
                        ? MessageType.Warning
                        : MessageType.Info;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox(item.Message, messageType);
                    if (item.Context != null && GUILayout.Button("Select", GUILayout.Width(54f), GUILayout.Height(38f)))
                    {
                        Selection.activeObject = item.Context;
                        EditorGUIUtility.PingObject(item.Context);
                    }
                    if (item.FixKind != Rac1BoothValidationFixKind.None &&
                        GUILayout.Button("Fix", GUILayout.Width(42f), GUILayout.Height(38f)))
                    {
                        ApplyValidationFix(authoring, item.FixKind);
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        internal static Rac1BoothValidationReport ApplyValidationFix(
            Rac1BoothAuthoring authoring,
            Rac1BoothValidationFixKind fixKind)
        {
            if (authoring == null)
            {
                throw new ArgumentNullException(nameof(authoring));
            }

            switch (fixKind)
            {
                case Rac1BoothValidationFixKind.EnableRepeatUvBake:
                    Undo.RecordObject(authoring, "Enable RAC1 Repeat UV Bake");
                    authoring.BakeRepeatUvDomains = true;
                    authoring.ReleaseCapture();
                    EditorUtility.SetDirty(authoring);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(authoring);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(fixKind), fixKind, "Unsupported booth validation fix.");
            }

            Rac1BoothValidationReport report = Rac1BoothBaker.Validate(authoring);
            SceneView.RepaintAll();
            if (report.HasErrors)
            {
                ShowSceneNotification(
                    $"Fix applied; {report.ErrorCount} validation error(s) remain");
            }
            else
            {
                ShowSceneNotification(
                    $"Fix applied; booth validation passed with {report.WarningCount} warning(s)");
            }

            Debug.Log(
                report.HasErrors
                    ? $"[RAC1 Booth] Fix applied; {report.ErrorCount} validation error(s) remain."
                    : "[RAC1 Booth] Fix applied and validation passed.",
                authoring);
            return report;
        }

        private static string SanitizeFileName(string value)
        {
            string result = string.IsNullOrWhiteSpace(value) ? "mall-booth" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                result = result.Replace(invalid, '_');
            }
            return result;
        }

        private static bool IsInsideAssets(string path)
        {
            string assetsPath = Path.GetFullPath(Application.dataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(assetsPath, StringComparison.OrdinalIgnoreCase);
        }

        private static void ShowSceneNotification(string message)
        {
            SceneView.lastActiveSceneView?.ShowNotification(new GUIContent(message), 4f);
        }
    }
}
