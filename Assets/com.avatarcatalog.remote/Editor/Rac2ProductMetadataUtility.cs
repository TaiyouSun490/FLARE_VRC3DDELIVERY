using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public static class Rac2ProductMetadataUtility
    {
        private const string Prefix = "AvatarCatalog.Rac2.Product.";

        public static Rac2BinaryExporter.ProductData From(GameObject source)
        {
            Rac2ProductMetadata component = source == null ? null : source.GetComponent<Rac2ProductMetadata>();
            if (component == null && source != null) component = source.GetComponentInParent<Rac2ProductMetadata>();
            if (component != null)
            {
                if (!component.IncludeProductMetadata) return null;
                return Create(component.ProductName, component.CreatorName, component.ProductUrl, component.AvatarBlueprintId, component.TrialEnabled);
            }
            if (!EditorPrefs.GetBool(Prefix + "Enabled", false)) return null;
            return Create(
                EditorPrefs.GetString(Prefix + "Name", ""),
                EditorPrefs.GetString(Prefix + "Creator", ""),
                EditorPrefs.GetString(Prefix + "Url", ""),
                EditorPrefs.GetString(Prefix + "Avatar", ""),
                EditorPrefs.GetBool(Prefix + "Trial", false));
        }

        public static void SaveDefaults(bool enabled, string name, string creator, string url, string avatar, bool trial)
        {
            EditorPrefs.SetBool(Prefix + "Enabled", enabled);
            EditorPrefs.SetString(Prefix + "Name", name ?? "");
            EditorPrefs.SetString(Prefix + "Creator", creator ?? "");
            EditorPrefs.SetString(Prefix + "Url", url ?? "");
            EditorPrefs.SetString(Prefix + "Avatar", avatar ?? "");
            EditorPrefs.SetBool(Prefix + "Trial", trial);
        }

        public static void LoadDefaults(out bool enabled, out string name, out string creator, out string url, out string avatar, out bool trial)
        {
            enabled = EditorPrefs.GetBool(Prefix + "Enabled", false);
            name = EditorPrefs.GetString(Prefix + "Name", "");
            creator = EditorPrefs.GetString(Prefix + "Creator", "");
            url = EditorPrefs.GetString(Prefix + "Url", "");
            avatar = EditorPrefs.GetString(Prefix + "Avatar", "");
            trial = EditorPrefs.GetBool(Prefix + "Trial", false);
        }

        private static Rac2BinaryExporter.ProductData Create(string name, string creator, string url, string avatar, bool trial)
        {
            return new Rac2BinaryExporter.ProductData
            {
                ProductName = name ?? "",
                CreatorName = creator ?? "",
                ProductUrl = url ?? "",
                AvatarBlueprintId = avatar ?? "",
                TrialEnabled = trial,
            };
        }
    }

    public sealed class Rac2ProductSettingsWindow : EditorWindow
    {
        private bool _enabled;
        private string _name;
        private string _creator;
        private string _url;
        private string _avatar;
        private bool _trial;

        [MenuItem("Tools/Avatar Catalog/Developer/Legacy Exporters/Configure RAC2 Product Metadata...")]
        private static void Open()
        {
            Rac2ProductSettingsWindow window = GetWindow<Rac2ProductSettingsWindow>("RAC2 Product");
            window.minSize = new Vector2(480f, 270f);
            Rac2ProductMetadataUtility.LoadDefaults(out window._enabled, out window._name, out window._creator, out window._url, out window._avatar, out window._trial);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Default RAC2 Product Metadata", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Used by GLB conversion and when the selected source has no Rac2ProductMetadata component.", MessageType.Info);
            _enabled = EditorGUILayout.Toggle("Include PROD chunk", _enabled);
            using (new EditorGUI.DisabledScope(!_enabled))
            {
                _name = EditorGUILayout.TextField("Product Name", _name);
                _creator = EditorGUILayout.TextField("Creator Name", _creator);
                _url = EditorGUILayout.TextField("Product HTTPS URL", _url);
                _avatar = EditorGUILayout.TextField("Avatar Blueprint ID", _avatar);
                _trial = EditorGUILayout.Toggle("Trial Enabled", _trial);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Save Defaults", GUILayout.Height(34f)))
            {
                Rac2ProductMetadataUtility.SaveDefaults(_enabled, _name, _creator, _url, _avatar, _trial);
                Close();
            }
        }
    }
}
