using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public sealed class Rac2VatExporterWindow : EditorWindow
    {
        private GameObject _animationRoot;
        private SkinnedMeshRenderer _renderer;
        private AnimationClip _clip;
        private int _framesPerSecond = 30;
        private bool _loop = true;
        private bool _includeNormals = true;
        private bool _hasCollider = true;
        private bool _portable;

        [MenuItem("Tools/Avatar Catalog/Developer/Legacy Exporters/Export SkinnedMesh Animation to RAC2 VAT...")]
        private static void Open()
        {
            Rac2VatExporterWindow window = GetWindow<Rac2VatExporterWindow>("RAC2 VAT Export");
            window.minSize = new Vector2(460f, 260f);
            window.FromSelection();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("RAC2 Vertex Animation Texture", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Bakes one AnimationClip entirely in Unity. Houdini is not used. The source must have one lilToon material.", MessageType.Info);
            _animationRoot = (GameObject)EditorGUILayout.ObjectField("Animation Root", _animationRoot, typeof(GameObject), true);
            _renderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Skinned Mesh", _renderer, typeof(SkinnedMeshRenderer), true);
            DrawSourceMaterialStatus();
            _clip = (AnimationClip)EditorGUILayout.ObjectField("Loop Clip", _clip, typeof(AnimationClip), false);
            _framesPerSecond = EditorGUILayout.IntSlider("FPS", _framesPerSecond, 1, 60);
            _loop = EditorGUILayout.Toggle("Loop", _loop);
            _includeNormals = EditorGUILayout.Toggle("Bake VAT Normals", _includeNormals);
            _hasCollider = EditorGUILayout.Toggle("Add Box Collider", _hasCollider);
            _portable = EditorGUILayout.Toggle("Portable / VRC Pickup", _portable);
            if (_portable) _hasCollider = true;

            int frameCount = _clip == null ? 0 : Mathf.Max(2, _loop
                ? Mathf.CeilToInt(_clip.length * _framesPerSecond)
                : Mathf.CeilToInt(_clip.length * _framesPerSecond) + 1);
            EditorGUILayout.LabelField("Frames", frameCount == 0 ? "-" : frameCount.ToString());
            using (new EditorGUI.DisabledScope(!CanExport(frameCount)))
            {
                if (GUILayout.Button("Bake and Export .rac2", GUILayout.Height(38f))) Export(frameCount);
            }
            if (GUILayout.Button("Use Current Selection")) FromSelection();
        }

        private bool CanExport(int frameCount)
        {
            return _animationRoot != null && _renderer != null && _clip != null &&
                   frameCount >= 2 && frameCount <= 240;
        }

        private void FromSelection()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null) return;
            _renderer = FindPreferredRenderer(selected);
            Animator animator = _renderer == null ? selected.GetComponentInParent<Animator>() : _renderer.GetComponentInParent<Animator>();
            _animationRoot = animator != null ? animator.gameObject : selected.transform.root.gameObject;
        }

        private static SkinnedMeshRenderer FindPreferredRenderer(GameObject selected)
        {
            SkinnedMeshRenderer direct = selected.GetComponent<SkinnedMeshRenderer>();
            if (IsSupportedSourceRenderer(direct)) return direct;

            SkinnedMeshRenderer[] renderers = selected.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (IsSupportedSourceRenderer(renderers[i])) return renderers[i];
            }

            if (direct != null) return direct;
            return renderers.Length == 0 ? null : renderers[0];
        }

        private static bool IsSupportedSourceRenderer(SkinnedMeshRenderer renderer)
        {
            if (renderer == null) return false;
            Material[] materials = renderer.sharedMaterials;
            return materials != null && materials.Length == 1 && IsLilToon(materials[0]);
        }

        private static bool IsLilToon(Material material)
        {
            return material != null && material.shader != null &&
                   material.shader.name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void DrawSourceMaterialStatus()
        {
            if (_renderer == null)
            {
                EditorGUILayout.HelpBox("Select the SkinnedMeshRenderer that actually owns the source material.", MessageType.Warning);
                return;
            }

            Material[] materials = _renderer.sharedMaterials;
            if (materials == null || materials.Length != 1 || materials[0] == null)
            {
                EditorGUILayout.HelpBox(
                    "Selected renderer: " + GetRendererPath(_renderer) + "\nRAC2 VAT requires exactly one non-null material slot.",
                    MessageType.Error);
                return;
            }

            Material material = materials[0];
            string shaderName = material.shader == null ? "<missing shader>" : material.shader.name;
            EditorGUILayout.LabelField("Source Material", material.name);
            EditorGUILayout.LabelField("Detected Shader", shaderName);
            if (!IsLilToon(material))
            {
                string hint = shaderName == "Hidden/InternalErrorShader"
                    ? "lilToon is missing or the material shader failed to compile. Reimport lilToon, then reassign the material."
                    : "Assign a lilToon Opaque or Cutout material to this exact Skinned Mesh field.";
                EditorGUILayout.HelpBox(
                    "The selected renderer is " + GetRendererPath(_renderer) + ".\n" + hint,
                    MessageType.Error);
            }
        }

        private static string GetRendererPath(SkinnedMeshRenderer renderer)
        {
            if (renderer == null) return "<none>";
            string path = renderer.name;
            Transform current = renderer.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }

        private void Export(int frameCount)
        {
            if (_renderer.sharedMesh == null || !_renderer.sharedMesh.isReadable)
                throw new InvalidOperationException("The SkinnedMesh source must be readable.");
            Material[] materials = _renderer.sharedMaterials;
            if (materials == null || materials.Length != 1 || materials[0] == null)
                throw new NotSupportedException("RAC2 VAT MVP supports exactly one material.");
            if (!IsLilToon(materials[0]))
            {
                string shaderName = materials[0].shader == null ? "<missing shader>" : materials[0].shader.name;
                throw new NotSupportedException(
                    "RAC2 VAT requires lilToon Opaque/Cutout on the selected SkinnedMeshRenderer." +
                    "\nRenderer: " + GetRendererPath(_renderer) +
                    "\nMaterial: " + materials[0].name +
                    "\nDetected shader: " + shaderName +
                    (shaderName == "Hidden/InternalErrorShader"
                        ? "\nlilToon is missing or failed to compile; reimport lilToon and reassign this material."
                        : "\nChoose the intended mesh child in the Skinned Mesh field, or assign lilToon to this exact renderer."));
            }

            string output = EditorUtility.SaveFilePanel("Save RAC2 VAT", "", _renderer.name + "-" + _clip.name, "rac2");
            if (string.IsNullOrEmpty(output)) return;

            var positions = new Vector3[frameCount][];
            var normals = _includeNormals ? new Vector3[frameCount][] : null;
            Mesh baseMesh = null;
            Mesh baked = new Mesh { name = "RAC2 VAT Bake Frame" };
            bool animationModeStarted = false;
            try
            {
                AnimationMode.StartAnimationMode();
                animationModeStarted = true;
                for (int frame = 0; frame < frameCount; frame++)
                {
                    float denominator = _loop ? frameCount : frameCount - 1f;
                    float time = Mathf.Min(_clip.length, frame * _clip.length / denominator);
                    AnimationMode.BeginSampling();
                    AnimationMode.SampleAnimationClip(_animationRoot, _clip, time);
                    AnimationMode.EndSampling();
                    baked.Clear();
                    _renderer.BakeMesh(baked);
                    if (baked.vertexCount != _renderer.sharedMesh.vertexCount)
                        throw new InvalidOperationException("Baked VAT topology changed.");
                    positions[frame] = baked.vertices;
                    if (_includeNormals)
                    {
                        Vector3[] frameNormals = baked.normals;
                        if (frameNormals == null || frameNormals.Length != baked.vertexCount)
                        {
                            baked.RecalculateNormals();
                            frameNormals = baked.normals;
                        }
                        normals[frame] = frameNormals;
                    }
                    if (frame == 0)
                    {
                        baseMesh = Instantiate(baked);
                        baseMesh.name = _renderer.sharedMesh.name + " RAC2 VAT Base";
                    }
                    EditorUtility.DisplayProgressBar("RAC2 VAT Bake", "Frame " + (frame + 1) + " / " + frameCount, (frame + 1f) / frameCount);
                }

                var vat = new Rac2BinaryExporter.VatClipData
                {
                    Name = _clip.name,
                    FramesPerSecond = _framesPerSecond,
                    Loop = _loop,
                    Positions = positions,
                    Normals = normals,
                };
                var interaction = new Rac2BinaryExporter.InteractionData
                {
                    HasCollider = _hasCollider,
                    IsPortable = _portable,
                };
                Rac2BinaryExporter.ProductData product = Rac2ProductMetadataUtility.From(_renderer.gameObject);
                Rac2BinaryExporter.ExportSummary summary = Rac2BinaryExporter.ExportMesh(
                    baseMesh, materials[0], output, vat: vat, interaction: interaction, product: product);
                if (output.Replace('\\', '/').StartsWith(Application.dataPath.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase))
                    AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("RAC2 VAT exported",
                    summary.VertexCount + " vertices / " + summary.VatFrameCount + " frames / " +
                    summary.VatTextureWidth + "x" + summary.VatTextureHeight + " VAT / " + summary.FileSize + " stored / " + summary.UncompressedFileSize + " raw bytes / " + summary.CompressedSectionCount + " LZ4 chunks", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (animationModeStarted) AnimationMode.StopAnimationMode();
                if (baseMesh != null) DestroyImmediate(baseMesh);
                DestroyImmediate(baked);
            }
        }
    }
}
