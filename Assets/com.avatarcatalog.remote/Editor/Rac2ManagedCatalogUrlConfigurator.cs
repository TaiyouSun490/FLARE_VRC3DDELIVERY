using System;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase;

namespace AvatarCatalog.Remote
{
    public sealed class Rac2ManagedCatalogUrlConfigurator : EditorWindow
    {
        private const string Prefix = "AvatarCatalog.Managed.";
        private string _baseUrl = "https://example.github.io/catalog";
        private int _slotCapacity = 64;
        private int _pageCapacity = 6;

        [MenuItem("Tools/FLARE/Catalog/Configure Fixed URLs...")]
        private static void Open()
        {
            var window = GetWindow<Rac2ManagedCatalogUrlConfigurator>("Managed Catalog URLs");
            window.minSize = new Vector2(520f, 250f);
            window._baseUrl = EditorPrefs.GetString(Prefix + "BaseUrl", window._baseUrl);
            window._slotCapacity = EditorPrefs.GetInt(Prefix + "Slots", 64);
            window._pageCapacity = EditorPrefs.GetInt(Prefix + "Pages", 6);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Managed RAC2 Catalog Fixed URL Bank", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Select a placed RAC2 Managed Avatar Catalog. URLs are constructed only in the Unity Editor and serialized into the World.", MessageType.Info);
            _baseUrl = EditorGUILayout.TextField("Public Base URL", _baseUrl);
            _slotCapacity = EditorGUILayout.IntSlider("Slot Capacity", _slotCapacity, 1, 64);
            _pageCapacity = EditorGUILayout.IntSlider("Page Capacity", _pageCapacity, 1, 6);
            EditorGUILayout.LabelField("Manifest", Normalize(_baseUrl) + "/v1/catalog/manifest.json");
            EditorGUILayout.LabelField("RAC2 URLs", (_slotCapacity * 2).ToString());
            EditorGUILayout.LabelField("Atlas URLs", (_pageCapacity * 2).ToString());
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(FindSelected() == null))
            {
                if (GUILayout.Button("Serialize Fixed URLs Into Selected Catalog", GUILayout.Height(38f))) Apply();
            }
        }

        private void Apply()
        {
            Rac2ManagedCatalogController controller = FindSelected();
            if (controller == null) throw new InvalidOperationException("Select a GameObject containing Rac2ManagedCatalogController.");
            string value = Normalize(_baseUrl);
            if (!value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Public Base URL must use HTTPS.");
            Undo.RecordObject(controller, "Configure managed catalog fixed URLs");
            controller.ManifestUrl = new VRCUrl(value + "/v1/catalog/manifest.json");
            controller.AtlasUrls = new VRCUrl[_pageCapacity * 2];
            for (int page = 0; page < _pageCapacity; page++)
                for (int lane = 0; lane < 2; lane++)
                    controller.AtlasUrls[page * 2 + lane] = new VRCUrl(value + "/v1/catalog/atlas/page-" + page.ToString("00") + "-r" + lane + ".png");
            controller.Rac2SlotUrls = new VRCUrl[_slotCapacity * 2];
            for (int slot = 0; slot < _slotCapacity; slot++)
                for (int lane = 0; lane < 2; lane++)
                    controller.Rac2SlotUrls[slot * 2 + lane] = new VRCUrl(value + "/v1/catalog/slots/" + slot.ToString("000") + "/r" + lane + ".rac2");
            UdonSharpEditorUtility.CopyProxyToUdon(controller, ProxySerializationPolicy.All);
            EditorUtility.SetDirty(controller);
            EditorPrefs.SetString(Prefix + "BaseUrl", value);
            EditorPrefs.SetInt(Prefix + "Slots", _slotCapacity);
            EditorPrefs.SetInt(Prefix + "Pages", _pageCapacity);
            Debug.Log("[RAC2 Managed Catalog] Serialized one manifest, " + controller.AtlasUrls.Length + " atlas URLs and " + controller.Rac2SlotUrls.Length + " RAC2 URLs.");
        }

        private static Rac2ManagedCatalogController FindSelected()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null) return null;
            Rac2ManagedCatalogController value = selected.GetComponent<Rac2ManagedCatalogController>();
            return value != null ? value : selected.GetComponentInChildren<Rac2ManagedCatalogController>(true);
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().TrimEnd('/');
        }
    }
}
