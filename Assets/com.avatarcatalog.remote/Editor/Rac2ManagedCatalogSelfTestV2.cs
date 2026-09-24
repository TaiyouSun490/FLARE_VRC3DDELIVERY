using System;
using System.IO;
using System.Reflection;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace AvatarCatalog.Remote
{
    public static class Rac2ManagedCatalogSelfTestV2
    {
        private const string AvatarId = "avtr_00000000-0000-0000-0000-000000000000";

        [MenuItem("Tools/FLARE/Developer/Tests/Run Managed Catalog Discovery Self-Test")]
        public static void RunMenu()
        {
            Run();
            EditorUtility.DisplayDialog("Managed Catalog Discovery Self-Test", "PASS", "OK");
        }

        public static void Run()
        {
            GameObject root = new GameObject("Managed Catalog Discovery Self-Test");
            try
            {
                Rac2ManagedCatalogControllerV2 controller = root.AddUdonSharpComponent<Rac2ManagedCatalogControllerV2>();
                controller.LoadRac2PreviewOnCardPress = false;
                controller.SearchAtlasUrls = new[] { new VRCUrl("https://example.com/search-r0.png"), new VRCUrl("https://example.com/search-r1.png") };
                GameObject inputObject = new GameObject("Search", typeof(RectTransform), typeof(InputField));
                inputObject.transform.SetParent(root.transform, false);
                controller.SearchInput = inputObject.GetComponent<InputField>();

                MethodInfo parse = typeof(Rac2ManagedCatalogControllerV2).GetMethod("ParseManifest", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo rebuild = typeof(Rac2ManagedCatalogControllerV2).GetMethod("RebuildVisibleItems", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo visible = typeof(Rac2ManagedCatalogControllerV2).GetField("_visibleItems", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo count = typeof(Rac2ManagedCatalogControllerV2).GetField("_visibleCount", BindingFlags.Instance | BindingFlags.NonPublic);
                if (parse == null || rebuild == null || visible == null || count == null) throw new MissingMethodException("Discovery implementation is incomplete.");

                string json = Manifest(
                    Item(5, "Legacy", "Maker", "avatar", 5, 1, "2025-01-01T00:00:00Z", 0) + "," +
                    Item(9, "Rising Star", "New Maker", "outfit", 900, 35, "2026-08-12T00:00:00Z", 1));
                if (!(bool)parse.Invoke(controller, new object[] { json })) throw new InvalidDataException("Discovery manifest was rejected.");
                rebuild.Invoke(controller, null);
                int[] order = (int[])visible.GetValue(controller);
                if ((int)count.GetValue(controller) != 2 || order[0] != 1) throw new InvalidDataException("HOT order is incorrect.");

                controller.SearchInput.text = "legacy";
                rebuild.Invoke(controller, null);
                order = (int[])visible.GetValue(controller);
                if ((int)count.GetValue(controller) != 1 || order[0] != 0) throw new InvalidDataException("Search filtering is incorrect.");

                controller.SearchInput.text = "";
                controller.CategoryFilter = 2;
                rebuild.Invoke(controller, null);
                order = (int[])visible.GetValue(controller);
                if ((int)count.GetValue(controller) != 1 || order[0] != 1) throw new InvalidDataException("Category filtering is incorrect.");
                Debug.Log("[RAC2 Managed Catalog Discovery Self-Test] search / filter / HOT order PASS");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static string Manifest(string items)
        {
            return "{\"schemaVersion\":1,\"revision\":\"discovery-test\",\"releaseLane\":1," +
                   "\"pageSize\":12,\"pageCount\":1,\"slotCapacity\":64,\"items\":[" + items + "]}";
        }

        private static string Item(int slot, string name, string creator, string category, int hot, int booth, string published, int cell)
        {
            return "{\"slot\":" + slot + ",\"name\":\"" + name + "\",\"creator\":\"" + creator + "\"," +
                   "\"avatarId\":\"" + AvatarId + "\",\"productUrl\":\"https://booth.pm/ja/items/5813187\"," +
                   "\"trial\":true,\"tags\":[\"featured\"],\"category\":\"" + category + "\"," +
                   "\"hotScore\":" + hot + ",\"boothRank\":" + booth + ",\"recommendedOrder\":" + cell + "," +
                   "\"publishedAt\":\"" + published + "\",\"page\":0,\"cell\":" + cell + "}";
        }
    }
}
