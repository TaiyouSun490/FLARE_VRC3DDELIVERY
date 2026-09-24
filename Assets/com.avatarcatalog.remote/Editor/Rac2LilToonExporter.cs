using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    /// <summary>Exports one selected static or currently posed lilToon renderer to RAC2 v0.1.</summary>
    public static class Rac2LilToonExporter
    {
        private const string MenuPath = "Tools/FLARE/Developer/Legacy Exporters/Export Selected lilToon Mesh to RAC2...";

        [MenuItem(MenuPath)]
        public static void ExportSelected()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null) throw new InvalidOperationException("Select one GameObject containing a MeshRenderer or SkinnedMeshRenderer.");

            Renderer renderer = selected.GetComponent<Renderer>();
            if (renderer == null) throw new InvalidOperationException("The selected GameObject has no Renderer.");
            Material[] materials = renderer.sharedMaterials;
            if (materials == null || materials.Length != 1 || materials[0] == null)
            {
                throw new NotSupportedException("RAC2 v0.1 lilToon export supports exactly one material.");
            }

            Material material = materials[0];
            if (material.shader == null || material.shader.name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new NotSupportedException("The selected material is not a lilToon material.");
            }

            Mesh working = null;
            try
            {
                var skinned = renderer as SkinnedMeshRenderer;
                if (skinned != null)
                {
                    working = new Mesh { name = selected.name + " RAC2 Pose" };
                    skinned.BakeMesh(working);
                }
                else
                {
                    MeshFilter filter = selected.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) throw new InvalidOperationException("The selected MeshRenderer has no MeshFilter mesh.");
                    working = UnityEngine.Object.Instantiate(filter.sharedMesh);
                    working.name = filter.sharedMesh.name + " RAC2 Copy";
                }

                Texture normal = material.HasProperty("_UseBumpMap") && material.GetFloat("_UseBumpMap") > 0.5f &&
                                 material.HasProperty("_BumpMap") ? material.GetTexture("_BumpMap") : null;
                if (normal != null)
                {
                    Vector2[] uv = working.uv;
                    if (uv == null || uv.Length != working.vertexCount)
                    {
                        throw new InvalidOperationException("The selected lilToon normal map requires UV0.");
                    }
                    Vector3[] normals = working.normals;
                    if (normals == null || normals.Length != working.vertexCount) working.RecalculateNormals();
                    Vector4[] tangents = working.tangents;
                    if (tangents == null || tangents.Length != working.vertexCount) working.RecalculateTangents();
                }

                string output = EditorUtility.SaveFilePanel(
                    "Save lilToon RAC2",
                    "",
                    selected.name,
                    "rac2");
                if (string.IsNullOrEmpty(output)) return;

                int interactionChoice = EditorUtility.DisplayDialogComplex(
                    "RAC2 Interaction",
                    "Choose whether the restored exhibit can be carried.",
                    "Portable (Pickup)",
                    "Fixed (Collider)",
                    "Cancel");
                if (interactionChoice == 2) return;
                var interaction = new Rac2BinaryExporter.InteractionData
                {
                    HasCollider = true,
                    IsPortable = interactionChoice == 0,
                };
                Rac2BinaryExporter.ProductData product = Rac2ProductMetadataUtility.From(selected);
                Rac2BinaryExporter.ExportSummary result = Rac2BinaryExporter.ExportMesh(
                    working, material, output, interaction: interaction, product: product);
                if (output.Replace('\\', '/').StartsWith(Application.dataPath.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase))
                {
                    AssetDatabase.Refresh();
                }

                EditorUtility.DisplayDialog(
                    "lilToon RAC2 exported",
                    result.VertexCount + " vertices / " + result.IndexCount + " indices / " +
                    result.FileSize + " stored / " + result.UncompressedFileSize + " raw bytes / " + result.CompressedSectionCount + " LZ4 chunks\nMain " + result.TextureWidth + "x" + result.TextureHeight +
                    "\nNormal " + result.NormalTextureWidth + "x" + result.NormalTextureHeight,
                    "OK");
            }
            finally
            {
                if (working != null) UnityEngine.Object.DestroyImmediate(working);
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateExportSelected()
        {
            return Selection.activeGameObject != null && Selection.activeGameObject.GetComponent<Renderer>() != null;
        }
    }
}

