using System;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase;

namespace AvatarCatalog.Remote
{
    public sealed class Rac2ManagedCatalogUrlConfiguratorV2 : EditorWindow
    {
        private const string Prefix = "AvatarCatalog.Managed.";
        private string _baseUrl = "https://example.github.io/catalog";
        private int _slotCapacity = 64;
        private int _pageCapacity = 6;

        [MenuItem("Tools/Avatar Catalog/Catalog/Configure Discovery Catalog Fixed URLs...")]
        private static void Open()
        {
            var window = GetWindow<Rac2ManagedCatalogUrlConfiguratorV2>("Discovery Catalog URLs");
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
            Rac2ManagedCatalogControllerV2 controller = FindSelected();
            if (controller == null) throw new InvalidOperationException("Select a GameObject containing Rac2ManagedCatalogController.");
            string value = Normalize(_baseUrl);
            if (!value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Public Base URL must use HTTPS.");
            Undo.RecordObject(controller, "Configure managed catalog fixed URLs");
            controller.ManifestUrl = new VRCUrl(value + "/v1/catalog/manifest.json");
            controller.SearchAtlasUrls = new VRCUrl[2];
            for (int lane = 0; lane < 2; lane++)
                controller.SearchAtlasUrls[lane] = new VRCUrl(value + "/v1/catalog/atlas/search-r" + lane + ".png");
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
            Debug.Log("[RAC2 Managed Catalog] Serialized one manifest, " + controller.SearchAtlasUrls.Length + " search atlas URLs, " + controller.AtlasUrls.Length + " legacy atlas URLs and " + controller.Rac2SlotUrls.Length + " RAC2 URLs.");
        }

        private static Rac2ManagedCatalogControllerV2 FindSelected()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null) return null;
            Rac2ManagedCatalogControllerV2 value = selected.GetComponent<Rac2ManagedCatalogControllerV2>();
            return value != null ? value : selected.GetComponentInChildren<Rac2ManagedCatalogControllerV2>(true);
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().TrimEnd('/');
        }
    }
}
