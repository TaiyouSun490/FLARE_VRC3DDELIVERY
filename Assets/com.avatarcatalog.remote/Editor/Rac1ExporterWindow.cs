using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public sealed class Rac1ExporterWindow : EditorWindow
    {
        private const string MenuPath = "Tools/Avatar Catalog/Developer/Legacy Exporters/RAC1 Exporter";
        private const string IncludeTextureKey = "AvatarCatalog.Remote.IncludeTexture";
        private const string TextureSizeKey = "AvatarCatalog.Remote.TextureSize";

        [SerializeField] private UnityEngine.Object source;
        [SerializeField] private bool includeAlbedoTexture = true;
        [SerializeField] private int maximumTextureDimension = 1024;
        [SerializeField] private Texture2D albedoOverride;

        [MenuItem(MenuPath)]
        private static void Open()
        {
            Rac1ExporterWindow window = GetWindow<Rac1ExporterWindow>();
            window.titleContent = new GUIContent("RAC1 Exporter");
            window.minSize = new Vector2(390f, 230f);
            window.UseSelectionIfSupported();
            window.Show();
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateOpen()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private void OnEnable()
        {
            includeAlbedoTexture = EditorPrefs.GetBool(IncludeTextureKey, true);
            maximumTextureDimension = EditorPrefs.GetInt(TextureSizeKey, 1024);
            UseSelectionIfSupported();
        }

        private void OnSelectionChange()
        {
            UseSelectionIfSupported();
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Remote Avatar Catalog", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Exports a MeshFilter or a baked snapshot of a SkinnedMeshRenderer to RAC1. " +
                "All sub-meshes must use triangle topology; RAC1 v1 stores one base material.",
                MessageType.Info);

            source = EditorGUILayout.ObjectField(
                new GUIContent("Source", "A GameObject, MeshFilter, or SkinnedMeshRenderer."),
                source,
                typeof(UnityEngine.Object),
                true);

            includeAlbedoTexture = EditorGUILayout.Toggle(
                new GUIContent("Include Albedo", "Copies _BaseMap or _MainTex to an RGBA32 payload."),
                includeAlbedoTexture);

            using (new EditorGUI.DisabledScope(!includeAlbedoTexture))
            {
                albedoOverride = (Texture2D)EditorGUILayout.ObjectField(
                    new GUIContent("Albedo Override", "Optional texture to use instead of the first material's albedo."),
                    albedoOverride,
                    typeof(Texture2D),
                    false);
                maximumTextureDimension = EditorGUILayout.IntPopup(
                    "Maximum Dimension",
                    maximumTextureDimension,
                    new[] { "256", "512", "1024", "2048", "4096", "8192" },
                    new[] { 256, 512, 1024, 2048, 4096, 8192 });
            }

            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(source == null || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Export RAC1…", GUILayout.Height(32f)))
                {
                    Export();
                }
            }
            EditorGUILayout.Space(8f);
        }

        private void Export()
        {
            string defaultName = GetDefaultFileName(source);
            string outputPath = EditorUtility.SaveFilePanel(
                "Export Remote Avatar Catalog Model",
                Application.dataPath,
                defaultName,
                "rac1");

            if (string.IsNullOrEmpty(outputPath))
            {
                return;
            }

            EditorPrefs.SetBool(IncludeTextureKey, includeAlbedoTexture);
            EditorPrefs.SetInt(TextureSizeKey, maximumTextureDimension);

            try
            {
                Rac1BinaryExporter.ExportSummary summary = Rac1BinaryExporter.Export(
                    source,
                    outputPath,
                    new Rac1BinaryExporter.Options
                    {
                        IncludeAlbedoTexture = includeAlbedoTexture,
                        MaximumTextureDimension = maximumTextureDimension,
                        AlbedoOverride = albedoOverride
                    });

                if (IsInsideAssets(outputPath))
                {
                    AssetDatabase.Refresh();
                }

                string textureText = (summary.Flags & Rac1BinaryExporter.DataFlags.HasTextureRgba32) != 0
                    ? $"\nTexture: {summary.TextureWidth} × {summary.TextureHeight} RGBA32"
                    : "\nTexture: none";
                string warningText = string.IsNullOrEmpty(summary.TextureWarning)
                    ? string.Empty
                    : "\n\n" + summary.TextureWarning;

                EditorUtility.DisplayDialog(
                    "RAC1 export complete",
                    $"{Path.GetFileName(outputPath)}\n" +
                    $"Vertices: {summary.VertexCount:N0}\n" +
                    $"Triangle indices: {summary.IndexCount:N0}" +
                    textureText +
                    $"\nFile size: {EditorUtility.FormatBytes(summary.FileSize)}" +
                    warningText,
                    "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("RAC1 export failed", exception.Message, "OK");
            }
        }

        private void UseSelectionIfSupported()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                return;
            }

            SkinnedMeshRenderer skinned = selected.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null)
            {
                source = skinned;
                return;
            }

            MeshFilter filter = selected.GetComponent<MeshFilter>();
            if (filter != null)
            {
                source = filter;
            }
        }

        private static string GetDefaultFileName(UnityEngine.Object value)
        {
            string objectName = value != null ? value.name : "catalog-model";
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
            {
                objectName = objectName.Replace(invalidCharacter, '_');
            }

            return string.IsNullOrWhiteSpace(objectName) ? "catalog-model" : objectName;
        }

        private static bool IsInsideAssets(string path)
        {
            string assetsPath = Path.GetFullPath(Application.dataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(assetsPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
