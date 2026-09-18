using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AvatarCatalog.Remote
{
    /// <summary>
    /// Builds the RAC1 booth MVP: every enabled MeshRenderer/MeshFilter and
    /// SkinnedMeshRenderer is converted to BoothRoot-local static triangles,
    /// and every source base map is baked into one 1024 RGBA atlas.
    /// </summary>
    public static class Rac1BoothBaker
    {
        public const int AtlasSize = 1024;
        public const int MaximumVertices = 40000;
        public const int MaximumIndices = 120000;
        public const long MaximumFileBytes = 10_000_000L;
        public const float BoundaryTolerance = 0.005f;
        public const int MaximumRepeatUvTilesPerAxis = 4;

        private const int AtlasPadding = 4;
        private const float FloatEpsilon = 0.0001f;
        private const int Rac1HeaderBytes = 60;

        private sealed class SourceEntry
        {
            public Renderer Renderer;
            public Mesh Mesh;
            public bool OwnsMesh;
            public Matrix4x4 ToBooth;
            public bool Mirrored;
            public MaterialEntry[] Materials;
        }

        private sealed class MaterialEntry
        {
            public Material Source;
            public Texture SourceTexture;
            public string TextureProperty;
            public Color BaseColor;
            public bool AlphaClip;
            public float Cutoff;
            public Texture2D Tile;
            public Rect AtlasRect;
            public bool HasUsedUvBounds;
            public bool HasInvalidUv;
            public Vector2 UsedUvMin;
            public Vector2 UsedUvMax;
            public Vector2 BakedUvMin = Vector2.zero;
            public Vector2 BakedUvSize = Vector2.one;
            public Renderer UvContext;
            public string UvMeshName;
        }

        private sealed class BuildOutput
        {
            public Rac1BoothValidationReport Report;
            public Mesh Mesh;
            public Texture2D Atlas;
            public bool HasAlphaClip;
        }

        public static Rac1BoothValidationReport Validate(Rac1BoothAuthoring authoring)
        {
            BuildOutput output = Build(authoring);
            DestroyEditorObject(output.Mesh);
            DestroyEditorObject(output.Atlas);
            if (authoring != null)
            {
                authoring.SetLastReport(output.Report);
            }

            return output.Report;
        }

        public static Rac1BoothValidationReport Capture(Rac1BoothAuthoring authoring)
        {
            BuildOutput output = Build(authoring);
            if (authoring == null)
            {
                DestroyEditorObject(output.Mesh);
                DestroyEditorObject(output.Atlas);
                return output.Report;
            }

            authoring.SetLastReport(output.Report);
            if (output.Report.HasErrors || output.Mesh == null || output.Atlas == null)
            {
                DestroyEditorObject(output.Mesh);
                DestroyEditorObject(output.Atlas);
                return output.Report;
            }

            Material previewMaterial = CreateCatalogMaterial(output.Atlas);
            authoring.ReplaceCapture(output.Mesh, output.Atlas, previewMaterial, output.Report);
            return output.Report;
        }

        public static Rac1BinaryExporter.ExportSummary Export(
            Rac1BoothAuthoring authoring,
            string outputPath,
            out Rac1BoothValidationReport report)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("An output path is required.", nameof(outputPath));
            }

            report = Capture(authoring);
            if (report.HasErrors || authoring == null || !authoring.HasCapture)
            {
                throw new InvalidOperationException(
                    "The booth could not be exported because validation failed. See the Booth Authoring report.");
            }

            GameObject temporary = new GameObject("__RAC1 Booth Export Source");
            temporary.hideFlags = HideFlags.HideAndDontSave;
            Material material = null;
            try
            {
                MeshFilter filter = temporary.AddComponent<MeshFilter>();
                MeshRenderer renderer = temporary.AddComponent<MeshRenderer>();
                filter.sharedMesh = authoring.CapturedMesh;
                material = CreateCatalogMaterial(authoring.CapturedAtlas);
                renderer.sharedMaterial = material;

                Rac1BinaryExporter.ExportSummary summary = Rac1BinaryExporter.Export(
                    filter,
                    outputPath,
                    new Rac1BinaryExporter.Options
                    {
                        IncludeAlbedoTexture = true,
                        MaximumTextureDimension = AtlasSize,
                        AlbedoOverride = authoring.CapturedAtlas
                    });

                if (summary.FileSize > MaximumFileBytes)
                {
                    throw new InvalidDataException(
                        $"The generated RAC1 is {EditorUtility.FormatBytes(summary.FileSize)}; " +
                        $"the booth limit is {EditorUtility.FormatBytes(MaximumFileBytes)}.");
                }

                return summary;
            }
            finally
            {
                DestroyEditorObject(material);
                DestroyEditorObject(temporary);
            }
        }

        public static Material CreateCatalogMaterial(Texture atlas)
        {
            Shader shader = Shader.Find("Avatar Catalog/RAC1 Opaque Cutout");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                throw new InvalidOperationException(
                    "Neither Avatar Catalog/RAC1 Opaque Cutout nor Unity Standard shader is available.");
            }

            Material material = new Material(shader)
            {
                name = "RAC1 Booth Catalog Material",
                hideFlags = HideFlags.HideAndDontSave
            };

            if (material.HasProperty("_MainTex"))
            {
                material.SetTexture("_MainTex", atlas);
            }
            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", atlas);
            }
            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", Color.white);
            }
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", Color.white);
            }
            if (material.HasProperty("_Cutoff"))
            {
                material.SetFloat("_Cutoff", 0.5f);
            }

            return material;
        }

        private static BuildOutput Build(Rac1BoothAuthoring authoring)
        {
            BuildOutput output = new BuildOutput
            {
                Report = new Rac1BoothValidationReport()
            };

            Rac1BoothValidationReport report = output.Report;
            if (authoring == null)
            {
                report.Add(Rac1BoothMessageSeverity.Error, "Booth Authoring component is missing.");
                return output;
            }

            ValidateRoots(authoring, report);
            if (report.HasErrors)
            {
                return output;
            }

            List<SourceEntry> sources = new List<SourceEntry>();
            HashSet<int> rendererIds = new HashSet<int>();
            try
            {
                CollectSources(authoring, authoring.AvatarRoot, "Avatar", rendererIds, sources, report);
                if (authoring.ShopVisualRoot != null)
                {
                    CollectSources(authoring, authoring.ShopVisualRoot, "Shop", rendererIds, sources, report);
                }

                ScanIgnoredComponents(authoring, report);
                report.SourceRendererCount = sources.Count;
                if (sources.Count == 0)
                {
                    report.Add(
                        Rac1BoothMessageSeverity.Error,
                        "No enabled MeshRenderer/MeshFilter or SkinnedMeshRenderer was found under the configured roots.",
                        authoring);
                    return output;
                }

                List<MaterialEntry> materials = PrepareSourcesAndMaterials(authoring, sources, report);
                report.MaterialCount = materials.Count;
                if (report.HasErrors)
                {
                    DestroyMaterialTiles(materials);
                    return output;
                }

                output.Atlas = BuildAtlas(materials, report);
                if (output.Atlas == null || report.HasErrors)
                {
                    DestroyMaterialTiles(materials);
                    return output;
                }

                for (int i = 0; i < materials.Count; i++)
                {
                    output.HasAlphaClip |= materials[i].AlphaClip;
                }

                output.Mesh = CombineSources(sources, report);
                DestroyMaterialTiles(materials);
                if (output.Mesh == null || report.HasErrors)
                {
                    DestroyEditorObject(output.Mesh);
                    DestroyEditorObject(output.Atlas);
                    output.Mesh = null;
                    output.Atlas = null;
                    return output;
                }

                report.Add(
                    Rac1BoothMessageSeverity.Info,
                    $"Ready: {report.SourceRendererCount:N0} renderers, {report.VertexCount:N0} vertices, " +
                    $"{report.IndexCount:N0} indices, one {AtlasSize} x {AtlasSize} atlas.",
                    authoring);
                return output;
            }
            finally
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    if (sources[i].OwnsMesh)
                    {
                        DestroyEditorObject(sources[i].Mesh);
                    }
                }
            }
        }

        private static void ValidateRoots(
            Rac1BoothAuthoring authoring,
            Rac1BoothValidationReport report)
        {
            if (!authoring.gameObject.CompareTag("EditorOnly"))
            {
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    "BoothRoot must use the EditorOnly tag so source avatar/shop assets are excluded from the world build.",
                    authoring.gameObject);
            }

            Vector3 scale = authoring.transform.lossyScale;
            if (!Approximately(scale, Vector3.one, 0.001f))
            {
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    "BoothRoot world scale must be (1, 1, 1), so the 3 x 3 x 2.7 metre profile remains authoritative.",
                    authoring.transform);
            }

            if (authoring.AvatarRoot == null)
            {
                report.Add(Rac1BoothMessageSeverity.Error, "Avatar Root is required.", authoring);
            }
            else if (!IsSelfOrChild(authoring.AvatarRoot, authoring.transform))
            {
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    "Avatar Root must be BoothRoot itself or one of its descendants.",
                    authoring.AvatarRoot);
            }

            if (authoring.ShopVisualRoot == null)
            {
                report.Add(
                    Rac1BoothMessageSeverity.Warning,
                    "Shop Visual Root is empty; this upload will contain only the avatar snapshot.",
                    authoring);
            }
            else if (!IsSelfOrChild(authoring.ShopVisualRoot, authoring.transform))
            {
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    "Shop Visual Root must be BoothRoot itself or one of its descendants.",
                    authoring.ShopVisualRoot);
            }

            if (authoring.AvatarRoot != null && authoring.ShopVisualRoot != null &&
                (IsSelfOrChild(authoring.AvatarRoot, authoring.ShopVisualRoot) ||
                 IsSelfOrChild(authoring.ShopVisualRoot, authoring.AvatarRoot)))
            {
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    "Avatar Root and Shop Visual Root must be disjoint hierarchies to prevent duplicate collection.",
                    authoring);
            }
        }

        private static void CollectSources(
            Rac1BoothAuthoring authoring,
            Transform root,
            string groupName,
            HashSet<int> rendererIds,
            List<SourceEntry> sources,
            Rac1BoothValidationReport report)
        {
            if (root == null)
            {
                return;
            }

            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(false);
            for (int i = 0; i < meshRenderers.Length; i++)
            {
                MeshRenderer renderer = meshRenderers[i];
                if (!renderer.enabled || IsGeneratedHelperRenderer(authoring, renderer) ||
                    !rendererIds.Add(renderer.GetInstanceID()))
                {
                    continue;
                }

                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                {
                    report.Add(
                        Rac1BoothMessageSeverity.Error,
                        $"{groupName} MeshRenderer '{renderer.name}' has no MeshFilter mesh.",
                        renderer);
                    continue;
                }

                if (!filter.sharedMesh.isReadable)
                {
                    report.Add(
                        Rac1BoothMessageSeverity.Error,
                        $"Mesh '{filter.sharedMesh.name}' is not readable. Enable Read/Write on its importer.",
                        filter.sharedMesh);
                    continue;
                }

                AddSource(authoring, renderer, filter.sharedMesh, false, sources, report);
            }

            SkinnedMeshRenderer[] skinnedRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(false);
            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = skinnedRenderers[i];
                if (!renderer.enabled || IsGeneratedHelperRenderer(authoring, renderer) ||
                    !rendererIds.Add(renderer.GetInstanceID()))
                {
                    continue;
                }

                if (renderer.sharedMesh == null)
                {
                    report.Add(
                        Rac1BoothMessageSeverity.Error,
                        $"{groupName} SkinnedMeshRenderer '{renderer.name}' has no mesh.",
                        renderer);
                    continue;
                }

                Mesh baked = new Mesh
                {
                    name = renderer.sharedMesh.name + " (RAC1 Booth Pose)",
                    hideFlags = HideFlags.HideAndDontSave
                };
                try
                {
                    // This freezes the currently evaluated bones and blendshape weights.
                    // Animator/PhysBone data itself is never serialized.
                    renderer.BakeMesh(baked, false);
                    AddSource(authoring, renderer, baked, true, sources, report);
                }
                catch (Exception exception)
                {
                    DestroyEditorObject(baked);
                    report.Add(
                        Rac1BoothMessageSeverity.Error,
                        $"Could not bake SkinnedMeshRenderer '{renderer.name}': {exception.Message}",
                        renderer);
                }
            }
        }

        private static void AddSource(
            Rac1BoothAuthoring authoring,
            Renderer renderer,
            Mesh mesh,
            bool ownsMesh,
            List<SourceEntry> sources,
            Rac1BoothValidationReport report)
        {
            Matrix4x4 matrix = authoring.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            float determinant = Determinant3x3(matrix);
            if (!IsFinite(determinant) || Mathf.Abs(determinant) < 0.0000001f)
            {
                if (ownsMesh)
                {
                    DestroyEditorObject(mesh);
                }
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    $"Renderer '{renderer.name}' has a singular or invalid transform.",
                    renderer);
                return;
            }

            sources.Add(new SourceEntry
            {
                Renderer = renderer,
                Mesh = mesh,
                OwnsMesh = ownsMesh,
                ToBooth = matrix,
                Mirrored = determinant < 0f
            });
        }

        private static List<MaterialEntry> PrepareSourcesAndMaterials(
            Rac1BoothAuthoring authoring,
            List<SourceEntry> sources,
            Rac1BoothValidationReport report)
        {
            List<MaterialEntry> materials = new List<MaterialEntry>();
            Dictionary<int, MaterialEntry> byInstanceId = new Dictionary<int, MaterialEntry>();

            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                SourceEntry source = sources[sourceIndex];
                Mesh mesh = source.Mesh;
                if (mesh == null || mesh.vertexCount == 0)
                {
                    report.Add(Rac1BoothMessageSeverity.Error, "A renderer contains an empty mesh.", source.Renderer);
                    continue;
                }

                Vector2[] uv = mesh.uv;

                Material[] sharedMaterials = source.Renderer.sharedMaterials;
                if (sharedMaterials == null || sharedMaterials.Length < mesh.subMeshCount)
                {
                    report.Add(
                        Rac1BoothMessageSeverity.Error,
                        $"Renderer '{source.Renderer.name}' needs one material for every sub-mesh.",
                        source.Renderer);
                    continue;
                }

                source.Materials = new MaterialEntry[mesh.subMeshCount];
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    if (mesh.GetTopology(subMesh) != MeshTopology.Triangles)
                    {
                        report.Add(
                            Rac1BoothMessageSeverity.Error,
                            $"Mesh '{mesh.name}' sub-mesh {subMesh} uses {mesh.GetTopology(subMesh)}; only triangles are supported.",
                            source.Renderer);
                        continue;
                    }

                    int[] subMeshIndices = mesh.GetIndices(subMesh);
                    if (subMeshIndices.Length == 0 || (subMeshIndices.Length % 3) != 0)
                    {
                        report.Add(
                            Rac1BoothMessageSeverity.Error,
                            $"Mesh '{mesh.name}' sub-mesh {subMesh} has an invalid triangle index list.",
                            source.Renderer);
                    }

                    Material material = sharedMaterials[subMesh];
                    if (material == null)
                    {
                        report.Add(
                            Rac1BoothMessageSeverity.Error,
                            $"Renderer '{source.Renderer.name}' sub-mesh {subMesh} has no material.",
                            source.Renderer);
                        continue;
                    }

                    int materialId = material.GetInstanceID();
                    if (!byInstanceId.TryGetValue(materialId, out MaterialEntry entry))
                    {
                        entry = CreateMaterialEntry(material, report);
                        byInstanceId.Add(materialId, entry);
                        materials.Add(entry);
                    }

                    source.Materials[subMesh] = entry;
                    AccumulateTexturedSubMeshUv(
                        mesh,
                        subMesh,
                        subMeshIndices,
                        uv,
                        entry,
                        source.Renderer,
                        report);
                }
            }

            FinalizeMaterialUvDomains(authoring, materials, report);
            if (!report.HasErrors)
            {
                for (int i = 0; i < materials.Count; i++)
                {
                    MaterialEntry entry = materials[i];
                    try
                    {
                        entry.Tile = CreateMaterialTile(entry);
                    }
                    catch (Exception exception)
                    {
                        report.Add(
                            Rac1BoothMessageSeverity.Error,
                            $"Could not read material '{entry.Source.name}' base texture: {exception.Message}",
                            entry.Source);
                    }
                }
            }

            return materials;
        }

        private static void AccumulateTexturedSubMeshUv(
            Mesh mesh,
            int subMesh,
            int[] subMeshIndices,
            Vector2[] uv,
            MaterialEntry material,
            Renderer renderer,
            Rac1BoothValidationReport report)
        {
            // A solid-color atlas tile never samples the source UVs. Keeping
            // validation scoped to textured sub-meshes lets creators use
            // unwrapped or UV-less display geometry without modifying the FBX.
            if (material == null || material.SourceTexture == null)
            {
                return;
            }

            if (uv == null || uv.Length != mesh.vertexCount)
            {
                material.HasInvalidUv = true;
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    $"Mesh '{mesh.name}' sub-mesh {subMesh} needs UV0 because material " +
                    $"'{material.Source.name}' uses a base texture.",
                    renderer);
                return;
            }

            for (int i = 0; i < subMeshIndices.Length; i++)
            {
                int vertex = subMeshIndices[i];
                if (vertex < 0 || vertex >= uv.Length)
                {
                    material.HasInvalidUv = true;
                    report.Add(
                        Rac1BoothMessageSeverity.Error,
                        $"Mesh '{mesh.name}' sub-mesh {subMesh} references invalid vertex {vertex}.",
                        renderer);
                    return;
                }

                Vector2 coordinate = uv[vertex];
                if (!IsFinite(coordinate.x) || !IsFinite(coordinate.y) ||
                    Mathf.Abs(coordinate.x) > 1000000f || Mathf.Abs(coordinate.y) > 1000000f)
                {
                    material.HasInvalidUv = true;
                    report.Add(
                        Rac1BoothMessageSeverity.Error,
                        $"Mesh '{mesh.name}' sub-mesh {subMesh} has a non-finite or unsafe UV0 " +
                        $"for material '{material.Source.name}' (vertex {vertex}).",
                        renderer);
                    return;
                }

                if (!material.HasUsedUvBounds)
                {
                    material.HasUsedUvBounds = true;
                    material.UsedUvMin = coordinate;
                    material.UsedUvMax = coordinate;
                    material.UvContext = renderer;
                    material.UvMeshName = mesh.name;
                }
                else
                {
                    material.UsedUvMin = Vector2.Min(material.UsedUvMin, coordinate);
                    material.UsedUvMax = Vector2.Max(material.UsedUvMax, coordinate);
                }

                if (coordinate.x < -FloatEpsilon || coordinate.x > 1f + FloatEpsilon ||
                    coordinate.y < -FloatEpsilon || coordinate.y > 1f + FloatEpsilon)
                {
                    // Keep Select focused on a renderer that actually caused
                    // the actionable Repeat-domain validation message.
                    material.UvContext = renderer;
                    material.UvMeshName = mesh.name;
                }
            }
        }

        private static void FinalizeMaterialUvDomains(
            Rac1BoothAuthoring authoring,
            List<MaterialEntry> materials,
            Rac1BoothValidationReport report)
        {
            for (int i = 0; i < materials.Count; i++)
            {
                MaterialEntry entry = materials[i];
                if (entry.SourceTexture == null || entry.HasInvalidUv || !entry.HasUsedUvBounds)
                {
                    continue;
                }

                bool repeatX = entry.UsedUvMin.x < -FloatEpsilon ||
                               entry.UsedUvMax.x > 1f + FloatEpsilon;
                bool repeatY = entry.UsedUvMin.y < -FloatEpsilon ||
                               entry.UsedUvMax.y > 1f + FloatEpsilon;
                if (!repeatX && !repeatY)
                {
                    entry.BakedUvMin = Vector2.zero;
                    entry.BakedUvSize = Vector2.one;
                    continue;
                }

                TextureWrapMode wrapX = entry.SourceTexture.wrapModeU;
                TextureWrapMode wrapY = entry.SourceTexture.wrapModeV;
                if ((repeatX && wrapX != TextureWrapMode.Repeat) ||
                    (repeatY && wrapY != TextureWrapMode.Repeat))
                {
                    string axes = repeatX && repeatY ? "U and V" : repeatX ? "U" : "V";
                    report.Add(
                        Rac1BoothMessageSeverity.Error,
                        $"Mesh '{entry.UvMeshName}' uses {axes} UV0 outside 0..1, but texture " +
                        $"'{entry.SourceTexture.name}' is not Repeat on every affected axis. " +
                        "Automatic baking is only safe for Repeat textures.",
                        entry.UvContext);
                    continue;
                }

                Vector2 domainMin = new Vector2(
                    repeatX ? Mathf.Floor(entry.UsedUvMin.x) : 0f,
                    repeatY ? Mathf.Floor(entry.UsedUvMin.y) : 0f);
                Vector2 domainMax = new Vector2(
                    repeatX ? Mathf.Ceil(entry.UsedUvMax.x) : 1f,
                    repeatY ? Mathf.Ceil(entry.UsedUvMax.y) : 1f);
                Vector2 domainSize = domainMax - domainMin;
                if (!IsFinite(domainSize.x) || !IsFinite(domainSize.y) ||
                    domainSize.x < 1f || domainSize.y < 1f ||
                    domainSize.x > MaximumRepeatUvTilesPerAxis ||
                    domainSize.y > MaximumRepeatUvTilesPerAxis)
                {
                    report.Add(
                        Rac1BoothMessageSeverity.Error,
                        $"Material '{entry.Source.name}' needs a {domainSize.x:F0} x {domainSize.y:F0} " +
                        $"Repeat UV bake, exceeding the safe {MaximumRepeatUvTilesPerAxis} x " +
                        $"{MaximumRepeatUvTilesPerAxis} limit.",
                        entry.UvContext);
                    continue;
                }

                if (!authoring.BakeRepeatUvDomains)
                {
                    report.Add(
                        Rac1BoothMessageSeverity.Error,
                        $"Mesh '{entry.UvMeshName}' UV0 spans U {entry.UsedUvMin.x:F3}..{entry.UsedUvMax.x:F3}, " +
                        $"V {entry.UsedUvMin.y:F3}..{entry.UsedUvMax.y:F3}. Texture " +
                        $"'{entry.SourceTexture.name}' uses Repeat, so Fix can bake this finite " +
                        $"{domainSize.x:F0} x {domainSize.y:F0} domain into the RAC1 atlas without " +
                        "changing the source Mesh or FBX.",
                        entry.UvContext,
                        Rac1BoothValidationFixKind.EnableRepeatUvBake);
                    continue;
                }

                entry.BakedUvMin = domainMin;
                entry.BakedUvSize = domainSize;
                report.Add(
                    Rac1BoothMessageSeverity.Info,
                    $"Material '{entry.Source.name}' Repeat UV domain U {domainMin.x:F0}..{domainMax.x:F0}, " +
                    $"V {domainMin.y:F0}..{domainMax.y:F0} will be baked into a temporary atlas tile. " +
                    "The source Mesh and FBX remain unchanged.",
                    entry.UvContext);
            }
        }

        private static MaterialEntry CreateMaterialEntry(
            Material material,
            Rac1BoothValidationReport report)
        {
            MaterialEntry entry = new MaterialEntry
            {
                Source = material,
                BaseColor = ReadBaseColor(material),
                Cutoff = material.HasProperty("_Cutoff")
                    ? Mathf.Clamp01(material.GetFloat("_Cutoff"))
                    : 0.5f
            };

            bool standardTransparent = material.HasProperty("_Mode") && material.GetFloat("_Mode") >= 1.5f;
            bool surfaceTransparent = material.HasProperty("_Surface") && material.GetFloat("_Surface") > 0.5f;
            int renderQueue = material.renderQueue >= 0
                ? material.renderQueue
                : material.shader != null ? material.shader.renderQueue : -1;
            bool keywordTransparent = material.IsKeywordEnabled("_ALPHABLEND_ON") ||
                                      material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON");
            bool transparent = standardTransparent || surfaceTransparent ||
                               keywordTransparent || renderQueue >= (int)RenderQueue.Transparent;

            entry.AlphaClip = material.IsKeywordEnabled("_ALPHATEST_ON") ||
                              (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f) ||
                              (material.HasProperty("_Mode") &&
                               Mathf.Abs(material.GetFloat("_Mode") - 1f) < 0.25f) ||
                              (renderQueue >= (int)RenderQueue.AlphaTest &&
                               renderQueue < (int)RenderQueue.Transparent);

            if (transparent)
            {
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    $"Material '{material.name}' uses transparent blending. RAC1 booth MVP supports Opaque and AlphaClip only.",
                    material);
            }

            if (material.shader == null)
            {
                report.Add(Rac1BoothMessageSeverity.Error, $"Material '{material.name}' has no shader.", material);
            }

            entry.TextureProperty = SelectBaseTextureProperty(material, out Texture texture);
            entry.SourceTexture = texture;
            if (entry.SourceTexture != null && !string.IsNullOrEmpty(entry.TextureProperty))
            {
                Vector2 scale = material.GetTextureScale(entry.TextureProperty);
                Vector2 offset = material.GetTextureOffset(entry.TextureProperty);
                if (!Approximately(scale, Vector2.one, FloatEpsilon) ||
                    !Approximately(offset, Vector2.zero, FloatEpsilon))
                {
                    report.Add(
                        Rac1BoothMessageSeverity.Error,
                        $"Material '{material.name}' must use identity texture tiling (1,1) and offset (0,0) for {entry.TextureProperty}.",
                        material);
                }
            }

            if (!material.HasProperty("_BaseColor") && !material.HasProperty("_Color"))
            {
                report.Add(
                    Rac1BoothMessageSeverity.Warning,
                    $"Material '{material.name}' has no _BaseColor or _Color; white will be used.",
                    material);
            }

            if (entry.SourceTexture == null)
            {
                report.Add(
                    Rac1BoothMessageSeverity.Info,
                    $"Material '{material.name}' has no _BaseMap/_MainTex texture; its base color will be atlased " +
                    "and source UV0 will be ignored.",
                    material);
            }

            return entry;
        }

        private static Texture2D BuildAtlas(
            List<MaterialEntry> materials,
            Rac1BoothValidationReport report)
        {
            if (materials.Count == 0)
            {
                report.Add(Rac1BoothMessageSeverity.Error, "No material tiles were available for the atlas.");
                return null;
            }

            Texture2D packed = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "RAC1 Booth Packed Atlas",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            try
            {
                Texture2D[] tiles = new Texture2D[materials.Count];
                for (int i = 0; i < materials.Count; i++)
                {
                    tiles[i] = materials[i].Tile;
                    if (tiles[i] == null)
                    {
                        throw new InvalidOperationException($"Material tile {i} was not generated.");
                    }
                }

                Rect[] rects = packed.PackTextures(tiles, AtlasPadding, AtlasSize, false);
                if (rects == null || rects.Length != materials.Count)
                {
                    throw new InvalidDataException("Unity did not return one atlas rectangle per material.");
                }

                for (int i = 0; i < materials.Count; i++)
                {
                    materials[i].AtlasRect = rects[i];
                }

                if (packed.width == AtlasSize && packed.height == AtlasSize)
                {
                    packed.name = "RAC1 Booth 1024 Atlas";
                    return packed;
                }

                Texture2D fixedSize = ResizeReadableTexture(packed, AtlasSize, AtlasSize);
                fixedSize.name = "RAC1 Booth 1024 Atlas";
                DestroyEditorObject(packed);
                return fixedSize;
            }
            catch (Exception exception)
            {
                DestroyEditorObject(packed);
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    "Could not build the 1024 atlas: " + exception.Message);
                return null;
            }
        }

        private static Mesh CombineSources(
            List<SourceEntry> sources,
            Rac1BoothValidationReport report)
        {
            List<Vector3> positions = new List<Vector3>();
            List<Vector3> normals = new List<Vector3>();
            List<Vector2> uv0 = new List<Vector2>();
            List<int> indices = new List<int>();
            bool completeNormals = true;
            bool boundsInitialized = false;
            Bounds actualBounds = default;
            Bounds allowed = Rac1BoothAuthoring.ProfileBounds;
            HashSet<int> outOfBoundsRenderers = new HashSet<int>();

            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                SourceEntry source = sources[sourceIndex];
                Mesh mesh = source.Mesh;
                Vector3[] sourcePositions = mesh.vertices;
                Vector3[] sourceNormals = mesh.normals;
                Vector2[] sourceUv = mesh.uv;
                bool sourceHasNormals = sourceNormals != null && sourceNormals.Length == sourcePositions.Length;
                completeNormals &= sourceHasNormals;
                Matrix4x4 normalMatrix = source.ToBooth.inverse.transpose;

                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    MaterialEntry material = source.Materials != null && subMesh < source.Materials.Length
                        ? source.Materials[subMesh]
                        : null;
                    if (material == null)
                    {
                        continue;
                    }

                    int[] sourceIndices = mesh.GetIndices(subMesh);
                    Dictionary<int, int> remap = new Dictionary<int, int>();
                    for (int triangle = 0; triangle < sourceIndices.Length; triangle += 3)
                    {
                        int a = AddVertex(sourceIndices[triangle], source, material, sourcePositions,
                            sourceNormals, sourceUv, sourceHasNormals, normalMatrix, remap,
                            positions, normals, uv0, allowed, ref boundsInitialized, ref actualBounds,
                            outOfBoundsRenderers, report);
                        int b = AddVertex(sourceIndices[triangle + 1], source, material, sourcePositions,
                            sourceNormals, sourceUv, sourceHasNormals, normalMatrix, remap,
                            positions, normals, uv0, allowed, ref boundsInitialized, ref actualBounds,
                            outOfBoundsRenderers, report);
                        int c = AddVertex(sourceIndices[triangle + 2], source, material, sourcePositions,
                            sourceNormals, sourceUv, sourceHasNormals, normalMatrix, remap,
                            positions, normals, uv0, allowed, ref boundsInitialized, ref actualBounds,
                            outOfBoundsRenderers, report);

                        indices.Add(a);
                        if (source.Mirrored)
                        {
                            indices.Add(c);
                            indices.Add(b);
                        }
                        else
                        {
                            indices.Add(b);
                            indices.Add(c);
                        }
                    }
                }
            }

            report.VertexCount = positions.Count;
            report.IndexCount = indices.Count;
            report.ContentBounds = actualBounds;
            report.EstimatedFileSize = EstimateRac1FileSize(positions.Count, indices.Count);

            if (positions.Count == 0 || indices.Count == 0)
            {
                report.Add(Rac1BoothMessageSeverity.Error, "The flattened booth contains no triangles.");
            }
            if (positions.Count > MaximumVertices)
            {
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    $"Flattened vertex count {positions.Count:N0} exceeds the {MaximumVertices:N0} booth limit.");
            }
            if (indices.Count > MaximumIndices)
            {
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    $"Flattened index count {indices.Count:N0} exceeds the {MaximumIndices:N0} booth limit.");
            }
            if (report.EstimatedFileSize > MaximumFileBytes)
            {
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    $"Estimated RAC1 size {EditorUtility.FormatBytes(report.EstimatedFileSize)} exceeds " +
                    $"the {EditorUtility.FormatBytes(MaximumFileBytes)} booth limit.");
            }
            if (report.HasErrors)
            {
                return null;
            }

            Mesh combined = new Mesh
            {
                name = "RAC1 Booth Static Snapshot",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            combined.SetVertices(positions);
            combined.SetUVs(0, uv0);
            combined.SetTriangles(indices, 0, true);
            if (completeNormals)
            {
                combined.SetNormals(normals);
            }
            else
            {
                combined.RecalculateNormals();
                report.Add(
                    Rac1BoothMessageSeverity.Warning,
                    "At least one source mesh had no complete normal array; normals were recalculated after flattening.");
            }

            // Write the tight, actual post-flatten AABB into RAC1. The general
            // validator may accept legacy BakeMesh padding, but this exporter
            // does not intentionally add any header padding.
            combined.RecalculateBounds();
            report.ContentBounds = combined.bounds;
            return combined;
        }

        private static int AddVertex(
            int sourceIndex,
            SourceEntry source,
            MaterialEntry material,
            Vector3[] sourcePositions,
            Vector3[] sourceNormals,
            Vector2[] sourceUv,
            bool sourceHasNormals,
            Matrix4x4 normalMatrix,
            Dictionary<int, int> remap,
            List<Vector3> positions,
            List<Vector3> normals,
            List<Vector2> uv0,
            Bounds allowed,
            ref bool boundsInitialized,
            ref Bounds actualBounds,
            HashSet<int> outOfBoundsRenderers,
            Rac1BoothValidationReport report)
        {
            if (remap.TryGetValue(sourceIndex, out int mapped))
            {
                return mapped;
            }

            if (sourceIndex < 0 || sourceIndex >= sourcePositions.Length ||
                (material.SourceTexture != null && sourceIndex >= sourceUv.Length))
            {
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    $"Mesh '{source.Mesh.name}' contains an out-of-range index {sourceIndex}.",
                    source.Renderer);
                return 0;
            }

            Vector3 position = source.ToBooth.MultiplyPoint3x4(sourcePositions[sourceIndex]);
            if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z))
            {
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    $"Renderer '{source.Renderer.name}' produced a non-finite vertex.",
                    source.Renderer);
                position = Vector3.zero;
            }

            if (!ContainsWithTolerance(allowed, position, BoundaryTolerance) &&
                outOfBoundsRenderers.Add(source.Renderer.GetInstanceID()))
            {
                report.Add(
                    Rac1BoothMessageSeverity.Error,
                    $"Renderer '{source.Renderer.name}' has geometry outside the 3 x 3 x 2.7 m booth " +
                    $"at local ({position.x:F3}, {position.y:F3}, {position.z:F3}).",
                    source.Renderer);
            }

            if (!boundsInitialized)
            {
                actualBounds = new Bounds(position, Vector3.zero);
                boundsInitialized = true;
            }
            else
            {
                actualBounds.Encapsulate(position);
            }

            Vector2 atlasCoordinate;
            if (material.SourceTexture == null)
            {
                // The generated tile is a single solid color. Sampling its
                // centre avoids atlas-edge filtering and makes source UV0
                // irrelevant without altering the source Mesh/FBX.
                atlasCoordinate = material.AtlasRect.center;
            }
            else
            {
                Vector2 sourceCoordinate = sourceUv[sourceIndex];
                Vector2 tileCoordinate = new Vector2(
                    (sourceCoordinate.x - material.BakedUvMin.x) / material.BakedUvSize.x,
                    (sourceCoordinate.y - material.BakedUvMin.y) / material.BakedUvSize.y);
                atlasCoordinate = new Vector2(
                    material.AtlasRect.xMin + Mathf.Clamp01(tileCoordinate.x) * material.AtlasRect.width,
                    material.AtlasRect.yMin + Mathf.Clamp01(tileCoordinate.y) * material.AtlasRect.height);
            }

            Vector3 normal = Vector3.up;
            if (sourceHasNormals)
            {
                normal = normalMatrix.MultiplyVector(sourceNormals[sourceIndex]);
                if (normal.sqrMagnitude > 0.0000001f)
                {
                    normal.Normalize();
                }
            }

            mapped = positions.Count;
            remap.Add(sourceIndex, mapped);
            positions.Add(position);
            normals.Add(normal);
            uv0.Add(atlasCoordinate);
            return mapped;
        }

        private static Texture2D CreateMaterialTile(MaterialEntry entry)
        {
            Texture2D tile;
            if (entry.SourceTexture == null)
            {
                tile = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                Color32[] solid = new Color32[16];
                Color32 color = MultiplyPixel(new Color32(255, 255, 255, 255), entry);
                for (int i = 0; i < solid.Length; i++)
                {
                    solid[i] = color;
                }
                tile.SetPixels32(solid);
            }
            else
            {
                int width = entry.SourceTexture.width;
                int height = entry.SourceTexture.height;
                if (width <= 0 || height <= 0)
                {
                    throw new InvalidDataException("Texture dimensions must be positive.");
                }

                float expandedWidth = width * entry.BakedUvSize.x;
                float expandedHeight = height * entry.BakedUvSize.y;
                float resizeScale = Mathf.Min(
                    1f,
                    (float)AtlasSize / Mathf.Max(expandedWidth, expandedHeight));
                int targetWidth = Mathf.Max(1, Mathf.RoundToInt(expandedWidth * resizeScale));
                int targetHeight = Mathf.Max(1, Mathf.RoundToInt(expandedHeight * resizeScale));
                tile = ReadTexture(
                    entry.SourceTexture, targetWidth, targetHeight,
                    entry.BakedUvSize, entry.BakedUvMin);

                Color32[] pixels = tile.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = MultiplyPixel(pixels[i], entry);
                }
                tile.SetPixels32(pixels);
            }

            tile.name = entry.Source.name + " (RAC1 Atlas Tile)";
            tile.hideFlags = HideFlags.HideAndDontSave;
            tile.wrapMode = TextureWrapMode.Clamp;
            tile.filterMode = FilterMode.Bilinear;
            tile.Apply(false, false);
            return tile;
        }

        private static Color32 MultiplyPixel(Color32 pixel, MaterialEntry entry)
        {
            Color color = entry.BaseColor;
            byte red = ToByte((pixel.r / 255f) * color.r);
            byte green = ToByte((pixel.g / 255f) * color.g);
            byte blue = ToByte((pixel.b / 255f) * color.b);
            float alpha = (pixel.a / 255f) * color.a;
            byte outputAlpha = entry.AlphaClip
                ? (byte)(alpha >= entry.Cutoff ? 255 : 0)
                : (byte)255;
            return new Color32(red, green, blue, outputAlpha);
        }

        private static Texture2D ReadTexture(
            Texture source,
            int width,
            int height,
            Vector2 scale,
            Vector2 offset)
        {
            if (source is Texture2D readable && readable.isReadable &&
                readable.width == width && readable.height == height &&
                Approximately(scale, Vector2.one, FloatEpsilon) &&
                Approximately(offset, Vector2.zero, FloatEpsilon))
            {
                Texture2D copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
                copy.SetPixels32(readable.GetPixels32());
                copy.Apply(false, false);
                return copy;
            }

            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = null;
            Texture2D result = null;
            try
            {
                temporary = RenderTexture.GetTemporary(
                    width,
                    height,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default);
                Graphics.Blit(source, temporary, scale, offset);
                RenderTexture.active = temporary;
                result = new Texture2D(width, height, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                result.Apply(false, false);
                return result;
            }
            catch
            {
                DestroyEditorObject(result);
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
                if (temporary != null)
                {
                    RenderTexture.ReleaseTemporary(temporary);
                }
            }
        }

        private static Texture2D ResizeReadableTexture(Texture2D source, int width, int height)
        {
            Color32[] sourcePixels = source.GetPixels32();
            Color32[] targetPixels = new Color32[checked(width * height)];
            for (int y = 0; y < height; y++)
            {
                int sourceY = Mathf.Min(source.height - 1, y * source.height / height);
                int sourceRow = sourceY * source.width;
                int targetRow = y * width;
                for (int x = 0; x < width; x++)
                {
                    int sourceX = Mathf.Min(source.width - 1, x * source.width / width);
                    targetPixels[targetRow + x] = sourcePixels[sourceRow + sourceX];
                }
            }

            Texture2D result = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            result.SetPixels32(targetPixels);
            result.Apply(false, false);
            return result;
        }

        private static void ScanIgnoredComponents(
            Rac1BoothAuthoring authoring,
            Rac1BoothValidationReport report)
        {
            HashSet<int> visited = new HashSet<int>();
            HashSet<string> warnedTypes = new HashSet<string>();
            ScanIgnoredRoot(authoring.AvatarRoot, authoring, visited, warnedTypes, report);
            ScanIgnoredRoot(authoring.ShopVisualRoot, authoring, visited, warnedTypes, report);
        }

        private static void ScanIgnoredRoot(
            Transform root,
            Rac1BoothAuthoring authoring,
            HashSet<int> visited,
            HashSet<string> warnedTypes,
            Rac1BoothValidationReport report)
        {
            if (root == null)
            {
                return;
            }

            Component[] components = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || component == authoring || !visited.Add(component.GetInstanceID()) ||
                    component is Transform || component is MeshFilter || component is MeshRenderer ||
                    component is SkinnedMeshRenderer)
                {
                    continue;
                }

                Type type = component.GetType();
                string typeName = type.FullName ?? type.Name;
                string message = null;
                if (component is Animator)
                {
                    message = "Animator is not uploaded; Capture freezes only the currently evaluated hierarchy pose.";
                }
                else if (component is ParticleSystem || component is TrailRenderer || component is LineRenderer)
                {
                    message = type.Name + " is not supported by the RAC1 booth MVP and will be omitted.";
                }
                else if (component is Cloth)
                {
                    message = "Cloth simulation is not uploaded; only the currently baked SkinnedMesh shape is retained.";
                }
                else if (component is Collider || component is Rigidbody || component is Light ||
                         component is AudioSource || component is Camera)
                {
                    message = type.Name + " is world behaviour and will not be uploaded.";
                }
                else if (component is LODGroup)
                {
                    message = "LODGroup is not serialized; every enabled child mesh is flattened into the snapshot.";
                }
                else if (typeName.IndexOf("PhysBone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         typeName.IndexOf("Contact", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         typeName.IndexOf("Constraint", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    message = type.Name + " is not uploaded; only its currently evaluated transform can affect the capture.";
                }
                else if (component is MonoBehaviour)
                {
                    message = type.Name + " script is not included in the visual-only RAC1 snapshot.";
                }
                else if (component is Renderer)
                {
                    message = type.Name + " is not a supported mesh renderer and will be omitted.";
                }

                if (message != null && warnedTypes.Add(typeName))
                {
                    report.Add(Rac1BoothMessageSeverity.Warning, message, component);
                }
            }
        }

        private static string SelectBaseTextureProperty(Material material, out Texture texture)
        {
            texture = null;
            if (material.HasProperty("_BaseMap"))
            {
                texture = material.GetTexture("_BaseMap");
                if (texture != null)
                {
                    return "_BaseMap";
                }
            }

            if (material.HasProperty("_MainTex"))
            {
                texture = material.GetTexture("_MainTex");
                return "_MainTex";
            }

            return string.Empty;
        }

        private static Color ReadBaseColor(Material material)
        {
            if (material.HasProperty("_BaseColor"))
            {
                return material.GetColor("_BaseColor");
            }
            if (material.HasProperty("_Color"))
            {
                return material.GetColor("_Color");
            }
            return Color.white;
        }

        private static long EstimateRac1FileSize(int vertexCount, int indexCount)
        {
            // Booth output always contains positions, normals, UV0, indices and
            // one exact 1024 RGBA32 texture. It intentionally writes no colors.
            return Rac1HeaderBytes +
                   (long)vertexCount * 12L +
                   (long)vertexCount * 12L +
                   (long)vertexCount * 8L +
                   (long)indexCount * 4L +
                   12L +
                   (long)AtlasSize * AtlasSize * 4L;
        }

        private static bool ContainsWithTolerance(Bounds bounds, Vector3 point, float tolerance)
        {
            Vector3 min = bounds.min - Vector3.one * tolerance;
            Vector3 max = bounds.max + Vector3.one * tolerance;
            return point.x >= min.x && point.x <= max.x &&
                   point.y >= min.y && point.y <= max.y &&
                   point.z >= min.z && point.z <= max.z;
        }

        private static bool IsGeneratedHelperRenderer(Rac1BoothAuthoring authoring, Renderer renderer)
        {
            return authoring != null && authoring.IsGeneratedHelper(renderer);
        }

        private static bool IsSelfOrChild(Transform candidate, Transform root)
        {
            return candidate != null && root != null &&
                   (candidate == root || candidate.IsChildOf(root));
        }

        private static bool Approximately(Vector3 a, Vector3 b, float epsilon)
        {
            return Mathf.Abs(a.x - b.x) <= epsilon &&
                   Mathf.Abs(a.y - b.y) <= epsilon &&
                   Mathf.Abs(a.z - b.z) <= epsilon;
        }

        private static bool Approximately(Vector2 a, Vector2 b, float epsilon)
        {
            return Mathf.Abs(a.x - b.x) <= epsilon && Mathf.Abs(a.y - b.y) <= epsilon;
        }

        private static float Determinant3x3(Matrix4x4 matrix)
        {
            return matrix.m00 * (matrix.m11 * matrix.m22 - matrix.m12 * matrix.m21) -
                   matrix.m01 * (matrix.m10 * matrix.m22 - matrix.m12 * matrix.m20) +
                   matrix.m02 * (matrix.m10 * matrix.m21 - matrix.m11 * matrix.m20);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static byte ToByte(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 255f), 0, 255);
        }

        private static void DestroyMaterialTiles(List<MaterialEntry> materials)
        {
            for (int i = 0; i < materials.Count; i++)
            {
                DestroyEditorObject(materials[i].Tile);
                materials[i].Tile = null;
            }
        }

        private static void DestroyEditorObject(UnityEngine.Object value)
        {
            if (value == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(value);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }
    }
}
