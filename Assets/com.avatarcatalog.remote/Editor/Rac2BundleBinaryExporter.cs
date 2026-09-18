using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    /// <summary>RAC2 v3 composite-scene writer.</summary>
    public static partial class Rac2BinaryExporter
    {
        public sealed class RenderNodeData
        {
            public string Name = "Renderer";
            public Mesh Mesh;
            public Material[] Materials;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation = Quaternion.identity;
            public Vector3 LocalScale = Vector3.one;
            public VatClipData Vat;
        }

        public sealed class ParticleEmitterData
        {
            public string Name = "Particle";
            public Mesh Mesh;
            public ParticleData Particle;
        }

        public sealed class BundleData
        {
            public readonly List<RenderNodeData> RenderNodes = new List<RenderNodeData>();
            public readonly List<ParticleEmitterData> ParticleEmitters = new List<ParticleEmitterData>();
            public Bounds Bounds;
            public InteractionData Interaction;
            public ProductData Product;
        }

        public struct BundleExportSummary
        {
            public int FormatVersion;
            public int RenderNodeCount;
            public int MaterialCount;
            public int ParticleEmitterCount;
            public int ParticleMaximum;
            public int VertexCount;
            public int IndexCount;
            public int VatNodeCount;
            public long FileSize;
            public long UncompressedFileSize;
            public int CompressedSectionCount;
        }

        private const int BundleMaxRenderNodes = 16;
        private const int BundleMaxMaterials = 64;
        private const int BundleMaxParticleEmitters = 4;
        private const int BundleMaxVertices = 40000;
        private const int BundleMaxIndices = 120000;
        private const int BundleMaxBytes = 67108864;

        public static BundleExportSummary ExportBundle(
            BundleData bundle,
            string outputPath,
            int maximumTextureDimension = 1024,
            bool compressSections = true,
            bool shareTextures = true)
        {
            if (bundle == null) throw new ArgumentNullException(nameof(bundle));
            if (bundle.RenderNodes.Count == 0 && bundle.ParticleEmitters.Count == 0)
                throw new InvalidOperationException("RAC2 product contains no renderers or particles.");
            if (bundle.RenderNodes.Count > BundleMaxRenderNodes)
                throw new InvalidOperationException("RAC2 supports at most 16 renderer nodes per product.");
            if (bundle.ParticleEmitters.Count > BundleMaxParticleEmitters)
                throw new InvalidOperationException("RAC2 supports at most 4 particle emitters per product.");
            if (!FiniteVector(bundle.Bounds.center) || !FiniteVector(bundle.Bounds.size) ||
                bundle.Bounds.size.x < 0f || bundle.Bounds.size.y < 0f || bundle.Bounds.size.z < 0f)
                throw new InvalidOperationException("RAC2 product bounds are invalid.");

            int materialCount;
            int vertexCount;
            int indexCount;
            int vatNodeCount;
            var sharedTextures = shareTextures ? new Dictionary<string, int>() : null;
            byte[] nodeBytes = BuildBundleNodes(
                bundle.RenderNodes,
                maximumTextureDimension,
                sharedTextures,
                out materialCount,
                out vertexCount,
                out indexCount,
                out vatNodeCount);
            int particleMaximum;
            byte[] particleBytes = BuildBundleParticles(
                bundle.ParticleEmitters,
                maximumTextureDimension,
                out particleMaximum);

            if (materialCount > BundleMaxMaterials)
                throw new InvalidOperationException("RAC2 supports at most 64 materials per product.");
            if (vertexCount > BundleMaxVertices || indexCount > BundleMaxIndices)
                throw new InvalidOperationException("RAC2 product exceeds 40,000 vertices or 120,000 indices.");

            var sections = new List<Section>
            {
                new Section { Type = "META", Bytes = BuildMeta(bundle.Bounds, Color.white) },
                new Section
                {
                    Type = "SCNE",
                    Bytes = BuildBundleSceneInfo(
                        bundle.RenderNodes.Count,
                        materialCount,
                        bundle.ParticleEmitters.Count,
                        vertexCount,
                        indexCount,
                        vatNodeCount)
                },
                new Section { Type = "NODE", Bytes = nodeBytes },
            };
            if (particleBytes != null) sections.Add(new Section { Type = "PART", Bytes = particleBytes });
            if (bundle.Interaction != null) sections.Add(new Section { Type = "INTR", Bytes = BuildInteraction(bundle.Interaction) });
            if (bundle.Product != null) sections.Add(new Section { Type = "PROD", Bytes = BuildProduct(bundle.Product) });

            long uncompressedTotal = 24L + sections.Count * 16L;
            foreach (Section section in sections) uncompressedTotal += section.Bytes.LongLength;
            if (uncompressedTotal > BundleMaxBytes)
                throw new InvalidOperationException("RAC2 product exceeds the 64 MB decoded limit.");

            int compressedCount;
            byte[] file = BuildBundleContainer(sections, compressSections, shareTextures, out compressedCount);
            string full = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException(directory);
            File.WriteAllBytes(full, file);
            return new BundleExportSummary
            {
                FormatVersion = 3,
                RenderNodeCount = bundle.RenderNodes.Count,
                MaterialCount = materialCount,
                ParticleEmitterCount = bundle.ParticleEmitters.Count,
                ParticleMaximum = particleMaximum,
                VertexCount = vertexCount,
                IndexCount = indexCount,
                VatNodeCount = vatNodeCount,
                FileSize = file.LongLength,
                UncompressedFileSize = uncompressedTotal,
                CompressedSectionCount = compressedCount,
            };
        }

        private static byte[] BuildBundleSceneInfo(
            int renderNodes,
            int materials,
            int particleEmitters,
            int vertices,
            int indices,
            int vatNodes)
        {
            using (var stream = new MemoryStream(32))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1u);
                writer.Write((uint)renderNodes);
                writer.Write((uint)materials);
                writer.Write((uint)particleEmitters);
                writer.Write((uint)vertices);
                writer.Write((uint)indices);
                writer.Write((uint)vatNodes);
                writer.Write(0u);
                return stream.ToArray();
            }
        }

        private static byte[] BuildBundleNodes(
            IList<RenderNodeData> nodes,
            int maximumTextureDimension,
            Dictionary<string, int> sharedTextures,
            out int materialTotal,
            out int vertexTotal,
            out int indexTotal,
            out int vatTotal)
        {
            materialTotal = 0;
            vertexTotal = 0;
            indexTotal = 0;
            vatTotal = 0;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1u);
                writer.Write((uint)nodes.Count);
                for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
                {
                    RenderNodeData node = nodes[nodeIndex];
                    if (node == null || node.Mesh == null)
                        throw new InvalidOperationException("RAC2 renderer node " + nodeIndex + " has no mesh.");
                    if (!node.Mesh.isReadable)
                        throw new InvalidOperationException("Mesh '" + node.Mesh.name + "' must be readable.");
                    int subMeshCount = node.Mesh.subMeshCount;
                    if (subMeshCount < 1 || subMeshCount > 16)
                        throw new InvalidOperationException("A RAC2 renderer supports 1 to 16 submeshes.");
                    if (node.Materials == null || node.Materials.Length < subMeshCount)
                        throw new InvalidOperationException("Renderer '" + node.Name + "' needs one material per submesh.");
                    if (!FiniteVector(node.LocalPosition) || !FiniteVector(node.LocalScale) ||
                        !Finite(node.LocalRotation.x) || !Finite(node.LocalRotation.y) ||
                        !Finite(node.LocalRotation.z) || !Finite(node.LocalRotation.w))
                        throw new InvalidOperationException("Renderer '" + node.Name + "' has an invalid transform.");

                    Vector3[] positions = node.Mesh.vertices;
                    Vector3[] normals = node.Mesh.normals;
                    Vector2[] uv = node.Mesh.uv;
                    Color32[] colors = node.Mesh.colors32;
                    Vector4[] tangents = node.Mesh.tangents;
                    VatPayload vat = node.Vat == null ? null : BuildVat(node.Vat, positions.Length);
                    Vector2[] uv1 = vat == null ? node.Mesh.uv2 : vat.Uv1;
                    MeshAttributes attributes = BundleAttributes(positions, normals, uv, uv1, colors, tangents);
                    ValidateBundleMaterials(node.Materials, subMeshCount, attributes);

                    int nodeIndexCount;
                    byte[] meshBytes = BuildBundleMesh(
                        node.Mesh,
                        positions,
                        normals,
                        uv,
                        uv1,
                        colors,
                        tangents,
                        attributes,
                        vat != null,
                        out nodeIndexCount);
                    byte[] materialBytes = BuildBundleMaterials(
                        node.Materials,
                        subMeshCount,
                        maximumTextureDimension, sharedTextures);
                    byte[] vatInfo = vat == null ? null : vat.Info;
                    byte[] vatPosition = vat == null ? null : vat.Positions;
                    byte[] vatNormal = vat == null ? null : vat.Normals;
                    byte[] name = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(node.Name) ? "Renderer" : node.Name);
                    if (name.Length > 128) Array.Resize(ref name, 128);

                    const int headerBytes = 80;
                    int recordBytes = checked(headerBytes + name.Length + meshBytes.Length + materialBytes.Length +
                                              (vatInfo == null ? 0 : vatInfo.Length) +
                                              (vatPosition == null ? 0 : vatPosition.Length) +
                                              (vatNormal == null ? 0 : vatNormal.Length));
                    writer.Write((uint)recordBytes);
                    // Bit 0: VAT payloads are present. Bit 1: mesh positions are
                    // reconstructed from the absolute VAT position texture's frame zero.
                    writer.Write(vat == null ? 0u : 3u);
                    writer.Write((uint)subMeshCount);
                    writer.Write((uint)name.Length);
                    writer.Write((uint)meshBytes.Length);
                    writer.Write((uint)materialBytes.Length);
                    writer.Write((uint)(vatInfo == null ? 0 : vatInfo.Length));
                    writer.Write((uint)(vatPosition == null ? 0 : vatPosition.Length));
                    writer.Write((uint)(vatNormal == null ? 0 : vatNormal.Length));
                    writer.Write(0u);
                    WriteVector3(writer, node.LocalPosition);
                    writer.Write(node.LocalRotation.x);
                    writer.Write(node.LocalRotation.y);
                    writer.Write(node.LocalRotation.z);
                    writer.Write(node.LocalRotation.w);
                    WriteVector3(writer, node.LocalScale);
                    writer.Write(name);
                    writer.Write(meshBytes);
                    writer.Write(materialBytes);
                    if (vatInfo != null) writer.Write(vatInfo);
                    if (vatPosition != null) writer.Write(vatPosition);
                    if (vatNormal != null) writer.Write(vatNormal);

                    materialTotal += subMeshCount;
                    vertexTotal += positions.Length;
                    indexTotal += nodeIndexCount;
                    if (vat != null) vatTotal++;
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static MeshAttributes BundleAttributes(
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uv,
            Vector2[] uv1,
            Color32[] colors,
            Vector4[] tangents)
        {
            MeshAttributes result = MeshAttributes.None;
            int count = positions == null ? 0 : positions.Length;
            if (normals != null && normals.Length == count) result |= MeshAttributes.Normals;
            if (uv != null && uv.Length == count) result |= MeshAttributes.Uv0;
            if (uv1 != null && uv1.Length == count) result |= MeshAttributes.Uv1;
            if (colors != null && colors.Length == count) result |= MeshAttributes.Colors;
            if (tangents != null && tangents.Length == count) result |= MeshAttributes.Tangents;
            return result;
        }

        private static void ValidateBundleMaterials(Material[] materials, int count, MeshAttributes attributes)
        {
            for (int index = 0; index < count; index++)
            {
                Material material = materials[index];
                if (material == null) throw new InvalidOperationException("RAC2 material " + index + " is missing.");
                Texture normal = IsLilToon(material) ? ReadNormalTexture(material) : null;
                if (normal != null &&
                    ((attributes & MeshAttributes.Uv0) == 0 ||
                     (attributes & MeshAttributes.Normals) == 0 ||
                     (attributes & MeshAttributes.Tangents) == 0))
                    throw new InvalidOperationException("Normal-mapped material '" + material.name + "' requires UV0, normals, and tangents.");
            }
        }

        private static byte[] BuildBundleMesh(
            Mesh mesh,
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uv,
            Vector2[] uv1,
            Color32[] colors,
            Vector4[] tangents,
            MeshAttributes attributes,
            bool positionsFromVatFrameZero,
            out int totalIndices)
        {
            int subMeshCount = mesh.subMeshCount;
            int[][] indices = new int[subMeshCount][];
            totalIndices = 0;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles)
                    throw new NotSupportedException("RAC2 supports triangle submeshes only.");
                indices[subMesh] = mesh.GetIndices(subMesh);
                if (indices[subMesh].Length == 0 || indices[subMesh].Length % 3 != 0)
                    throw new InvalidOperationException("Submesh " + subMesh + " contains no valid triangles.");
                foreach (int value in indices[subMesh])
                    if (value < 0 || value >= positions.Length)
                        throw new InvalidOperationException("Mesh contains an out-of-range index.");
                totalIndices = checked(totalIndices + indices[subMesh].Length);
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(positionsFromVatFrameZero ? 2u : 1u);
                writer.Write((uint)positions.Length);
                writer.Write((uint)subMeshCount);
                writer.Write((uint)attributes);
                writer.Write(32u);
                writer.Write((uint)totalIndices);
                if (!positionsFromVatFrameZero)
                    foreach (Vector3 value in positions) WriteVector3(writer, value);
                if ((attributes & MeshAttributes.Normals) != 0) foreach (Vector3 value in normals) WriteVector3(writer, value);
                if ((attributes & MeshAttributes.Uv0) != 0) foreach (Vector2 value in uv) { writer.Write(value.x); writer.Write(value.y); }
                if ((attributes & MeshAttributes.Uv1) != 0) foreach (Vector2 value in uv1) { writer.Write(value.x); writer.Write(value.y); }
                if ((attributes & MeshAttributes.Colors) != 0) foreach (Color32 value in colors) { writer.Write(value.r); writer.Write(value.g); writer.Write(value.b); writer.Write(value.a); }
                if ((attributes & MeshAttributes.Tangents) != 0) foreach (Vector4 value in tangents) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); }
                for (int subMesh = 0; subMesh < subMeshCount; subMesh++) writer.Write((uint)indices[subMesh].Length);
                for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                    foreach (int value in indices[subMesh]) writer.Write((uint)value);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] BuildBundleMaterials(Material[] materials, int count, int maximumTextureDimension,
            Dictionary<string, int> sharedTextures)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1u);
                writer.Write((uint)count);
                for (int index = 0; index < count; index++)
                {
                    Material material = materials[index];
                    bool lilToon = IsLilToon(material);
                    Texture albedoSource = ReadTexture(material);
                    Texture normalSource = lilToon ? ReadNormalTexture(material) : null;
                    TexturePayload albedo = albedoSource == null ? null : CopyTexture(albedoSource, maximumTextureDimension, false);
                    TexturePayload normal = normalSource == null ? null : CopyTexture(normalSource, maximumTextureDimension, true);
                    byte[] materialBytes = lilToon ? BuildLilToonMaterial(material, normal != null) : null;
                    byte[] albedoBytes = ShareBundleTexture(BuildTexture(albedo), false, sharedTextures);
                    byte[] normalBytes = ShareBundleTexture(BuildTexture(normal), true, sharedTextures);
                    const int headerBytes = 36;
                    int recordBytes = checked(headerBytes +
                                              (materialBytes == null ? 0 : materialBytes.Length) +
                                              (albedoBytes == null ? 0 : albedoBytes.Length) +
                                              (normalBytes == null ? 0 : normalBytes.Length));
                    writer.Write((uint)recordBytes);
                    writer.Write(lilToon ? 1u : 0u);
                    WriteColor(writer, ReadColor(material));
                    writer.Write((uint)(materialBytes == null ? 0 : materialBytes.Length));
                    writer.Write((uint)(albedoBytes == null ? 0 : albedoBytes.Length));
                    writer.Write((uint)(normalBytes == null ? 0 : normalBytes.Length));
                    if (materialBytes != null) writer.Write(materialBytes);
                    if (albedoBytes != null) writer.Write(albedoBytes);
                    if (normalBytes != null) writer.Write(normalBytes);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        // Texture payload v2 is a backward reference to a preceding full v1 texture.
        // IDs count full textures only, in NODE material order, albedo before normal.
        private static byte[] ShareBundleTexture(byte[] payload, bool linear, Dictionary<string, int> resources)
        {
            if (payload == null || resources == null) return payload;
            string key;
            using (SHA256 sha = SHA256.Create())
                key = (linear ? "normal:" : "albedo:") + Convert.ToBase64String(sha.ComputeHash(payload));
            int index;
            if (!resources.TryGetValue(key, out index))
            {
                resources.Add(key, resources.Count);
                return payload;
            }
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(2u);
                writer.Write((uint)index);
                return stream.ToArray();
            }
        }

        private static byte[] BuildBundleParticles(
            IList<ParticleEmitterData> emitters,
            int maximumTextureDimension,
            out int particleMaximum)
        {
            particleMaximum = 0;
            if (emitters.Count == 0) return null;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(2u);
                writer.Write((uint)emitters.Count);
                for (int index = 0; index < emitters.Count; index++)
                {
                    ParticleEmitterData emitter = emitters[index];
                    if (emitter == null || emitter.Mesh == null || emitter.Particle == null)
                        throw new InvalidOperationException("RAC2 particle emitter " + index + " is incomplete.");
                    if (!emitter.Mesh.isReadable)
                        throw new InvalidOperationException("Particle mesh '" + emitter.Mesh.name + "' must be readable.");
                    Vector3[] positions = emitter.Mesh.vertices;
                    Vector3[] normals = emitter.Mesh.normals;
                    Vector2[] uv = emitter.Mesh.uv;
                    Color32[] colors = emitter.Mesh.colors32;
                    Vector4[] tangents = emitter.Mesh.tangents;
                    MeshAttributes attributes = BundleAttributes(positions, normals, uv, null, colors, tangents);
                    uint[] indices = CollectIndices(emitter.Mesh, positions.Length);
                    byte[] mesh = BuildMesh(positions, normals, uv, null, colors, tangents, indices, attributes);
                    byte[] particle = BuildParticle(emitter.Particle);
                    TexturePayload copied = emitter.Particle.Texture == null
                        ? null
                        : CopyTexture(emitter.Particle.Texture, maximumTextureDimension, false);
                    byte[] texture = BuildTexture(copied);
                    byte[] name = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(emitter.Name) ? "Particle" : emitter.Name);
                    if (name.Length > 128) Array.Resize(ref name, 128);
                    const int headerBytes = 32;
                    int recordBytes = checked(headerBytes + name.Length + mesh.Length + particle.Length +
                                              (texture == null ? 0 : texture.Length));
                    writer.Write((uint)recordBytes);
                    writer.Write((uint)name.Length);
                    writer.Write((uint)mesh.Length);
                    writer.Write((uint)particle.Length);
                    writer.Write((uint)(texture == null ? 0 : texture.Length));
                    writer.Write(0u);
                    writer.Write(0u);
                    writer.Write(0u);
                    writer.Write(name);
                    writer.Write(mesh);
                    writer.Write(particle);
                    if (texture != null) writer.Write(texture);
                    particleMaximum += emitter.Particle.MaximumParticles;
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] BuildBundleContainer(List<Section> sections, bool compress, bool modern, out int compressedCount)
        {
            compressedCount = 0;
            int tocBytes = compress ? 24 : 16;
            int total = checked(24 + sections.Count * tocBytes);
            foreach (Section section in sections)
            {
                section.Checksum = modern ? StandardAdler32(section.Bytes, 0, section.Bytes.Length)
                    : Adler32(section.Bytes, 0, section.Bytes.Length);
                section.StoredBytes = section.Bytes;
                section.Codec = 0u;
                if (compress)
                {
                    byte[] candidate = CompressLz4Block(section.Bytes);
                    if (candidate.Length < section.Bytes.Length)
                    {
                        section.StoredBytes = candidate;
                        section.Codec = 1u;
                        compressedCount++;
                    }
                }
                total = checked(total + section.StoredBytes.Length);
            }
            if (total > BundleMaxBytes) throw new InvalidOperationException("RAC2 product exceeds the 64 MB stored limit.");

            using (var stream = new MemoryStream(total))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)'R');
                writer.Write((byte)'A');
                writer.Write((byte)'C');
                writer.Write((byte)'2');
                writer.Write(3u);
                writer.Write((uint)total);
                writer.Write((uint)sections.Count);
                writer.Write(compress ? modern ? 3u : 1u : 0u);
                writer.Write(compress ? 24u : 0u);
                int offset = 24 + sections.Count * tocBytes;
                foreach (Section section in sections)
                {
                    if (compress) WriteCompressedSection(writer, section, offset);
                    else WriteSection(writer, section.Type, offset, section.Bytes.Length);
                    offset += section.StoredBytes.Length;
                }
                foreach (Section section in sections) writer.Write(section.StoredBytes);
                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
