using System;
using System.IO;
using System.Reflection;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase;

namespace AvatarCatalog.Remote
{
    public static class Rac2ManagedCatalogSelfTest
    {
        private const string AvatarId = "avtr_00000000-0000-0000-0000-000000000000";

        [MenuItem("Tools/Avatar Catalog/Developer/Tests/Run Managed Catalog Self-Test")]
        public static void RunMenu()
        {
            Run();
            EditorUtility.DisplayDialog("Managed Catalog Self-Test", "PASS", "OK");
        }

        public static void Run()
        {
            GameObject root = new GameObject("Managed Catalog Self-Test");
            try
            {
                Rac2ManagedCatalogController controller = root.AddUdonSharpComponent<Rac2ManagedCatalogController>();
                controller.LoadRac2PreviewOnCardPress = false;
                controller.AtlasUrls = new[]
                {
                    new VRCUrl("https://example.com/v1/catalog/atlas/page-00-r0.png"),
                    new VRCUrl("https://example.com/v1/catalog/atlas/page-00-r1.png"),
                };

                MethodInfo parse = typeof(Rac2ManagedCatalogController).GetMethod("ParseManifest", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo select = typeof(Rac2ManagedCatalogController).GetMethod("SelectCell", BindingFlags.Instance | BindingFlags.NonPublic);
                if (parse == null || select == null) throw new MissingMethodException("Managed catalog parser or selector is missing.");

                string valid = Manifest(Item(7, AvatarId, 0, 0), 1);
                if (!(bool)parse.Invoke(controller, new object[] { valid }))
                    throw new InvalidDataException("Valid managed catalog manifest was rejected: " + controller.LastError);
                if (controller.LoadedRevision != "selftest" || controller.LoadedReleaseLane != 1 ||
                    controller.LoadedItemCount != 1 || controller.LoadedPageCount != 1)
                    throw new InvalidDataException("Managed catalog public load state is incorrect.");

                select.Invoke(controller, new object[] { 0 });
                if (controller.SelectedItem != 0 || controller.SelectedSlot != 7 ||
                    controller.SelectedAvatarId != AvatarId || controller.SelectionCount != 1)
                    throw new InvalidDataException("Managed catalog selection state is incorrect.");

                AssertRejected(parse, controller, Manifest(
                    Item(7, AvatarId, 0, 0) + "," + Item(7, AvatarId, 0, 1), 1),
                    "duplicate slot");
                AssertRejected(parse, controller, Manifest(Item(2, "avtr_invalid", 0, 0), 1), "invalid avatar ID");
                AssertRejected(parse, controller, Manifest(Item(2, AvatarId, 0, 1), 1), "non-canonical page/cell");

                controller.AtlasUrls = new VRCUrl[1];
                AssertRejected(parse, controller, valid, "incomplete atlas URL bank");

                controller.AtlasUrls = new VRCUrl[2];
                controller.LoadRac2PreviewOnCardPress = true;
                controller.Rac2SlotUrls = new VRCUrl[2];
                AssertRejected(parse, controller, valid, "incomplete RAC2 URL bank");

                Debug.Log("[RAC2 Managed Catalog Self-Test] manifest / fixed URL bank / selection PASS");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void AssertRejected(MethodInfo parse, Rac2ManagedCatalogController controller, string json, string caseName)
        {
            if ((bool)parse.Invoke(controller, new object[] { json }))
                throw new InvalidDataException("Managed catalog accepted " + caseName + ".");
        }

        private static string Manifest(string items, int pageCount)
        {
            return "{\"schemaVersion\":1,\"revision\":\"selftest\",\"releaseLane\":1," +
                   "\"publishedAt\":\"2026-08-12T00:00:00Z\",\"pageSize\":12,\"pageCount\":" + pageCount +
                   ",\"slotCapacity\":64,\"items\":[" + items + "]}";
        }

        private static string Item(int slot, string avatarId, int page, int cell)
        {
            return "{\"slot\":" + slot + ",\"name\":\"管理アバター\",\"creator\":\"運営\"," +
                   "\"avatarId\":\"" + avatarId + "\",\"productUrl\":\"https://example.com/avatar\"," +
                   "\"trial\":true,\"tags\":[],\"page\":" + page + ",\"cell\":" + cell + "}";
        }
    }
}
