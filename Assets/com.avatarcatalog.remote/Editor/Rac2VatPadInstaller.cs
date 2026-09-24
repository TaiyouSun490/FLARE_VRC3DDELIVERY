using System;
using System.IO;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace AvatarCatalog.Remote
{
    public static class Rac2VatPadInstaller
    {
        private const string RequestPath = "Library/NightSlotRac2Vat.install";
        private const string PrefabPath = "Assets/NightSlotMall/Prefabs/RAC-3D-ImagePad.prefab";
        private const string OpaquePath = "Assets/NightSlotMall/Materials/RAC2-VAT-Opaque.mat";
        private const string CutoutPath = "Assets/NightSlotMall/Materials/RAC2-VAT-Cutout.mat";

        [InitializeOnLoadMethod]
        private static void InstallWhenRequested()
        {
            string path = Path.GetFullPath(RequestPath);
            if (!File.Exists(path)) return;
            EditorApplication.delayCall += () =>
            {
                try
                {
                    File.Delete(path);
                    Install();
                }
                catch (Exception exception) { Debug.LogException(exception); }
            };
        }

        [MenuItem("Tools/FLARE/Developer/Installers/Install RAC2 VAT Support")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before installing RAC2 VAT.");
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
            Material opaque = EnsureMaterial(OpaquePath, "Avatar Catalog/RAC2 VAT Opaque");
            Material cutout = EnsureMaterial(CutoutPath, "Avatar Catalog/RAC2 VAT Cutout");

            GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                Rac2RuntimeLoader loader = root.GetComponentInChildren<Rac2RuntimeLoader>(true);
                if (loader == null) throw new InvalidOperationException("RAC2 Runtime Loader is missing from the Pad.");
                loader.VatOpaqueTemplate = opaque;
                loader.VatCutoutTemplate = cutout;
                loader.VatPlaybackSpeed = 1f;
                UdonSharpEditorUtility.CopyProxyToUdon(loader, ProxySerializationPolicy.All);

                Transform panel = root.transform.Find("Canvas/Panel");
                if (panel == null)
                {
                    Canvas canvas = root.GetComponentInChildren<Canvas>(true);
                    panel = canvas == null ? null : canvas.transform.Find("Panel");
                }
                if (panel != null)
                {
                    Transform title = panel.Find("Title");
                    Text label = title == null ? null : title.GetComponent<Text>();
                    if (label != null) label.text = "RAC 3D IMAGEPAD v0.2 / VAT";
                    Transform instructions = panel.Find("Instructions");
                    Text info = instructions == null ? null : instructions.GetComponent<Text>();
                    if (info != null) info.text = "DIRECT GLB = basic preview / RAC2 = lilToon, Normal and VAT animation";
                }

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log("[RAC2 VAT] Pad templates installed; GLB + RAC2 controls were preserved.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static Material EnsureMaterial(string path, string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null) throw new InvalidOperationException("VAT shader is missing: " + shaderName);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(material, path);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
            }
            material.SetFloat("_UseBumpMap", 1f);
            material.SetFloat("_BumpScale", 1f);
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
