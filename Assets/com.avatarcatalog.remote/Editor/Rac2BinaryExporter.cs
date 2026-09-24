using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    /// <summary>Writes the canonical chunked RAC2 v2 static-preview format.</summary>
    public static partial class Rac2BinaryExporter
    {
        [Flags]
        public enum MeshAttributes : uint
        {
            None = 0,
            Normals = 1,
            Uv0 = 2,
            Colors = 4,
            Tangents = 8,
            Uv1 = 16,
        }

        public enum MaterialProfile : uint
        {
            Legacy = 0,
            LilToon = 1,
        }

        public struct ExportSummary
        {
            public int VertexCount;
            public int IndexCount;
            public int TextureWidth;
            public int TextureHeight;
            public int NormalTextureWidth;
            public int NormalTextureHeight;
            public long FileSize;
            public MeshAttributes Attributes;
            public MaterialProfile Profile;
            public int VatFrameCount;
            public int VatTextureWidth;
            public int VatTextureHeight;
            public bool VatHasNormals;
            public long UncompressedFileSize;
            public int CompressedSectionCount;
            public bool HasParticles;
            public int ParticleMaximum;
            public int ParticleTextureWidth;
            public int ParticleTextureHeight;
            public bool HasCollider;
            public bool IsPortable;
            public bool HasProduct;
            public bool TrialEnabled;
        }

        public sealed class VatClipData
        {
            public string Name = "Loop";
            public float FramesPerSecond = 30f;
            public bool Loop = true;
            public Vector3[][] Positions;
            public Vector3[][] Normals;
        }

        public sealed class ParticleData
        {
            public bool Loop = true;
            public bool Billboard = true;
            public bool HideBaseMesh = true;
            public int ShaderProfile;
            public int MaximumParticles = 32;
            public float Duration = 5f;
            public float Lifetime = 1f;
            public float EmissionRate = 8f;
            public float SpeedMinimum = 0.2f;
            public float SpeedMaximum = 0.5f;
            public float SizeMinimum = 0.1f;
            public float SizeMaximum = 0.2f;
            public float Gravity;
            public float AngularSpeed;
            public Vector3 Origin;
            public Vector3 Direction = Vector3.up;
            public int Shape;
            public float ShapeRadius = 0.1f;
            public float ShapeAngle = 20f;
            public Vector3 ShapeScale = Vector3.one;
            public Color StartColor = Color.white;
            public Color EndColor = new Color(1f, 1f, 1f, 0f);
            public int FlipbookColumns = 1;
            public int FlipbookRows = 1;
            public Texture Texture;
        }

        public sealed class InteractionData
        {
            public bool HasCollider = true;
            public bool IsPortable;
        }

        public sealed class ProductData
        {
            public string ProductName = "";
            public string CreatorName = "";
            public string ProductUrl = "";
            public string AvatarBlueprintId = "";
            public bool TrialEnabled;
        }

        private sealed class VatPayload
        {
            public Bounds Bounds;
            public byte[] Info;
            public byte[] Positions;
            public byte[] Normals;
            public Vector2[] Uv1;
            public int FrameCount;
            public int Width;
            public int Height;
        }

        private sealed class TexturePayload
        {
            public int Width;
            public int Height;
            public byte[] Bytes;
        }

        private sealed class Section
        {
            public string Type;
            public byte[] Bytes;
            public byte[] StoredBytes;
            public uint Codec;
            public uint Checksum;
        }

        public static ExportSummary ExportMesh(
            Mesh mesh,
            Material material,
            string outputPath,
            Texture2D albedoOverride = null,
            int maximumTextureDimension = 1024,
            Texture2D normalOverride = null,
            VatClipData vat = null,
            bool compressSections = true,
            ParticleData particle = null,
            InteractionData interaction = null,
            ProductData product = null)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (!mesh.isReadable) throw new InvalidOperationException("Mesh must be readable before RAC2 export.");

            Vector3[] positions = mesh.vertices;
            if (positions == null || positions.Length == 0) throw new InvalidOperationException("Mesh contains no vertices.");
            Vector3[] normals = mesh.normals;
            Vector2[] uv = mesh.uv;
            VatPayload vatPayload = vat == null ? null : BuildVat(vat, positions.Length);
            Vector2[] uv1 = vatPayload == null ? null : vatPayload.Uv1;
            Color32[] colors = mesh.colors32;
            Vector4[] tangents = mesh.tangents;

            MeshAttributes attributes = MeshAttributes.None;
            if (normals != null && normals.Length == positions.Length) attributes |= MeshAttributes.Normals;
            if (uv != null && uv.Length == positions.Length) attributes |= MeshAttributes.Uv0;
            if (colors != null && colors.Length == positions.Length) attributes |= MeshAttributes.Colors;
            if (tangents != null && tangents.Length == positions.Length) attributes |= MeshAttributes.Tangents;
            if (uv1 != null && uv1.Length == positions.Length) attributes |= MeshAttributes.Uv1;

            uint[] indices = CollectIndices(mesh, positions.Length);
            Color baseColor = ReadColor(material);
            Texture albedoSource = albedoOverride != null ? albedoOverride : ReadTexture(material);
            bool isLilToon = IsLilToon(material);
            Texture normalSource = normalOverride != null ? normalOverride : isLilToon ? ReadNormalTexture(material) : null;
            if (normalSource != null &&
                ((attributes & MeshAttributes.Uv0) == 0 ||
                 (attributes & MeshAttributes.Normals) == 0 ||
                 (attributes & MeshAttributes.Tangents) == 0))
            {
                throw new InvalidOperationException("A lilToon normal map requires UV0, normals, and tangents on every vertex.");
            }

            TexturePayload albedo = albedoSource == null ? null : CopyTexture(albedoSource, maximumTextureDimension, false);
            TexturePayload normal = normalSource == null ? null : CopyTexture(normalSource, maximumTextureDimension, true);
            TexturePayload particleTexture = particle == null || particle.Texture == null ? null : CopyTexture(particle.Texture, maximumTextureDimension, false);
            byte[] particleBytes = particle == null ? null : BuildParticle(particle);
            byte[] particleTextureBytes = BuildTexture(particleTexture);
            byte[] interactionBytes = interaction == null ? null : BuildInteraction(interaction);
            byte[] productBytes = product == null ? null : BuildProduct(product);
            byte[] metaBytes = BuildMeta(mesh.bounds, baseColor);
            byte[] meshBytes = BuildMesh(positions, normals, uv, uv1, colors, tangents, indices, attributes);
            byte[] materialBytes = isLilToon ? BuildLilToonMaterial(material, normal != null) : null;
            byte[] albedoBytes = BuildTexture(albedo);
            byte[] normalBytes = BuildTexture(normal);

            var sections = new List<Section>
            {
                new Section { Type = "META", Bytes = metaBytes },
                new Section { Type = "MESH", Bytes = meshBytes },
            };
            if (materialBytes != null) sections.Add(new Section { Type = "MATL", Bytes = materialBytes });
            if (albedoBytes != null) sections.Add(new Section { Type = "TEX0", Bytes = albedoBytes });
            if (normalBytes != null) sections.Add(new Section { Type = "TEXN", Bytes = normalBytes });
            if (vatPayload != null)
            {
                sections.Add(new Section { Type = "VATI", Bytes = vatPayload.Info });
                sections.Add(new Section { Type = "VATP", Bytes = vatPayload.Positions });
                if (vatPayload.Normals != null) sections.Add(new Section { Type = "VATN", Bytes = vatPayload.Normals });
            }
            if (particleBytes != null)
            {
                sections.Add(new Section { Type = "PART", Bytes = particleBytes });
                if (particleTextureBytes != null) sections.Add(new Section { Type = "PTEX", Bytes = particleTextureBytes });
            }
            if (interactionBytes != null) sections.Add(new Section { Type = "INTR", Bytes = interactionBytes });
            if (productBytes != null) sections.Add(new Section { Type = "PROD", Bytes = productBytes });

            int uncompressedTotal = 24 + sections.Count * 16;
            foreach (Section section in sections) uncompressedTotal = checked(uncompressedTotal + section.Bytes.Length);

            int compressedSectionCount = 0;
            int tocEntryBytes = compressSections ? 24 : 16;
            int total = 24 + sections.Count * tocEntryBytes;
            foreach (Section section in sections)
            {
                section.Checksum = Adler32(section.Bytes, 0, section.Bytes.Length);
                section.StoredBytes = section.Bytes;
                section.Codec = 0u;
                if (compressSections)
                {
                    byte[] compressed = CompressLz4Block(section.Bytes);
                    if (compressed.Length < section.Bytes.Length)
                    {
                        section.StoredBytes = compressed;
                        section.Codec = 1u;
                        compressedSectionCount++;
                    }
                }
                total = checked(total + section.StoredBytes.Length);
            }

            byte[] file;
            using (var stream = new MemoryStream(total))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)'R');
                writer.Write((byte)'A');
                writer.Write((byte)'C');
                writer.Write((byte)'2');
                writer.Write(2u);
                writer.Write((uint)total);
                writer.Write((uint)sections.Count);
                writer.Write(compressSections ? 1u : 0u);
                writer.Write(compressSections ? 24u : 0u);

                int offset = 24 + sections.Count * tocEntryBytes;
                foreach (Section section in sections)
                {
                    if (compressSections)
                    {
                        WriteCompressedSection(writer, section, offset);
                    }
                    else
                    {
                        WriteSection(writer, section.Type, offset, section.Bytes.Length);
                    }
                    offset += section.StoredBytes.Length;
                }

                foreach (Section section in sections) writer.Write(section.StoredBytes);
                writer.Flush();
                file = stream.ToArray();
            }

            string full = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            File.WriteAllBytes(full, file);
            return new ExportSummary
            {
                VertexCount = positions.Length,
                IndexCount = indices.Length,
                TextureWidth = albedo == null ? 0 : albedo.Width,
                TextureHeight = albedo == null ? 0 : albedo.Height,
                NormalTextureWidth = normal == null ? 0 : normal.Width,
                NormalTextureHeight = normal == null ? 0 : normal.Height,
                FileSize = file.LongLength,
                Attributes = attributes,
                Profile = isLilToon ? MaterialProfile.LilToon : MaterialProfile.Legacy,
                VatFrameCount = vatPayload == null ? 0 : vatPayload.FrameCount,
                VatTextureWidth = vatPayload == null ? 0 : vatPayload.Width,
                VatTextureHeight = vatPayload == null ? 0 : vatPayload.Height,
                VatHasNormals = vatPayload != null && vatPayload.Normals != null,
                UncompressedFileSize = uncompressedTotal,
                CompressedSectionCount = compressedSectionCount,
                HasParticles = particle != null,
                ParticleMaximum = particle == null ? 0 : particle.MaximumParticles,
                ParticleTextureWidth = particleTexture == null ? 0 : particleTexture.Width,
                ParticleTextureHeight = particleTexture == null ? 0 : particleTexture.Height,
                HasCollider = interaction != null && interaction.HasCollider,
                IsPortable = interaction != null && interaction.IsPortable,
                HasProduct = product != null,
                TrialEnabled = product != null && product.TrialEnabled,
            };
        }

        private static byte[] BuildInteraction(InteractionData interaction)
        {
            if (interaction.IsPortable && !interaction.HasCollider)
                throw new InvalidOperationException("A portable RAC2 object requires a collider.");
            using (var stream = new MemoryStream(8))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1u);
                writer.Write((interaction.HasCollider ? 1u : 0u) | (interaction.IsPortable ? 2u : 0u));
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] BuildProduct(ProductData product)
        {
            string name = product.ProductName == null ? "" : product.ProductName.Trim();
            string creator = product.CreatorName == null ? "" : product.CreatorName.Trim();
            string url = product.ProductUrl == null ? "" : product.ProductUrl.Trim();
            string avatarId = product.AvatarBlueprintId == null ? "" : product.AvatarBlueprintId.Trim();
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            byte[] creatorBytes = Encoding.UTF8.GetBytes(creator);
            byte[] urlBytes = Encoding.UTF8.GetBytes(url);
            byte[] avatarBytes = Encoding.UTF8.GetBytes(avatarId);
            if (nameBytes.Length > 128 || creatorBytes.Length > 128 || urlBytes.Length > 1024 || avatarBytes.Length > 41)
                throw new InvalidOperationException("RAC2 product metadata exceeds its UTF-8 size limit.");
            if (urlBytes.Length > 0 && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("RAC2 product URL must use HTTPS.");
            bool validAvatar = avatarId.Length == 41 && avatarId.StartsWith("avtr_", StringComparison.Ordinal);
            if ((avatarBytes.Length > 0 && !validAvatar) || (product.TrialEnabled && !validAvatar))
                throw new InvalidOperationException("RAC2 trial avatar requires a canonical avtr_ Blueprint ID.");
            if (nameBytes.Length + creatorBytes.Length + urlBytes.Length + avatarBytes.Length == 0)
                throw new InvalidOperationException("RAC2 product metadata must contain at least one value.");

            using (var stream = new MemoryStream(24 + nameBytes.Length + creatorBytes.Length + urlBytes.Length + avatarBytes.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1u);
                writer.Write(product.TrialEnabled ? 1u : 0u);
                writer.Write((uint)nameBytes.Length);
                writer.Write((uint)creatorBytes.Length);
                writer.Write((uint)urlBytes.Length);
                writer.Write((uint)avatarBytes.Length);
                writer.Write(nameBytes);
                writer.Write(creatorBytes);
                writer.Write(urlBytes);
                writer.Write(avatarBytes);
                writer.Flush();
                return stream.ToArray();
            }
        }
        private static byte[] BuildMeta(Bounds bounds, Color baseColor)
        {
            using (var stream = new MemoryStream(40))
            using (var writer = new BinaryWriter(stream))
            {
                WriteVector3(writer, bounds.center);
                WriteVector3(writer, bounds.size);
                writer.Write(baseColor.r);
                writer.Write(baseColor.g);
                writer.Write(baseColor.b);
                writer.Write(baseColor.a);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] BuildMesh(
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uv,
            Vector2[] uv1,
            Color32[] colors,
            Vector4[] tangents,
            uint[] indices,
            MeshAttributes attributes)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((uint)positions.Length);
                writer.Write((uint)indices.Length);
                writer.Write((uint)attributes);
                writer.Write(32u);
                foreach (Vector3 value in positions) WriteVector3(writer, value);
                if ((attributes & MeshAttributes.Normals) != 0) foreach (Vector3 value in normals) WriteVector3(writer, value);
                if ((attributes & MeshAttributes.Uv0) != 0) foreach (Vector2 value in uv) { writer.Write(value.x); writer.Write(value.y); }
                if ((attributes & MeshAttributes.Uv1) != 0) foreach (Vector2 value in uv1) { writer.Write(value.x); writer.Write(value.y); }
                if ((attributes & MeshAttributes.Colors) != 0) foreach (Color32 value in colors) { writer.Write(value.r); writer.Write(value.g); writer.Write(value.b); writer.Write(value.a); }
                if ((attributes & MeshAttributes.Tangents) != 0) foreach (Vector4 value in tangents) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); writer.Write(value.w); }
                foreach (uint value in indices) writer.Write(value);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] BuildLilToonMaterial(Material material, bool hasNormal)
        {
            float fallbackMode = material != null && material.shader != null &&
                                 material.shader.name.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 ? 1f : 0f;
            int mode = Mathf.RoundToInt(ReadFloat(material, "_TransparentMode", fallbackMode, 0f, 6f));
            if (mode != 0 && mode != 1)
            {
                throw new NotSupportedException("RAC2 v0.1 supports lilToon Opaque and Cutout materials only.");
            }

            using (var stream = new MemoryStream(48))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1u); // MATL schema version
                writer.Write(1u); // lilToon profile
                writer.Write((uint)mode);
                writer.Write((uint)Mathf.RoundToInt(ReadFloat(material, "_Cull", 2f, 0f, 2f)));
                writer.Write(ReadFloat(material, "_Cutoff", 0.5f, -0.001f, 1.001f));
                writer.Write(ReadFloat(material, "_BumpScale", 1f, -10f, 10f));
                writer.Write(ReadFloat(material, "_ShadowStrength", 1f, 0f, 1f));
                writer.Write(ReadFloat(material, "_AsUnlit", 0f, 0f, 1f));
                writer.Write(ReadFloat(material, "_LightMinLimit", 0.05f, 0f, 1f));
                writer.Write(ReadFloat(material, "_LightMaxLimit", 1f, 0f, 10f));
                writer.Write(ReadFloat(material, "_MonochromeLighting", 0f, 0f, 1f));
                writer.Write(hasNormal ? 1u : 0u);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] BuildTexture(TexturePayload texture)
        {
            if (texture == null) return null;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1u);
                writer.Write((uint)texture.Width);
                writer.Write((uint)texture.Height);
                writer.Write((uint)texture.Bytes.Length);
                writer.Write(texture.Bytes);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] BuildParticle(ParticleData particle)
        {
            if (particle.MaximumParticles < 1 || particle.MaximumParticles > 32)
                throw new InvalidOperationException("RAC2 particle pool supports 1 to 32 particles.");
            if (particle.ShaderProfile < 0 || particle.ShaderProfile > 1)
                throw new InvalidOperationException("RAC2 particle shader profile must be Alpha or Additive.");
            if (particle.Shape < 0 || particle.Shape > 3)
                throw new InvalidOperationException("RAC2 particle shape must be Point, Sphere, Cone, or Box.");
            if (!Finite(particle.Duration) || particle.Duration <= 0f || particle.Duration > 120f ||
                !Finite(particle.Lifetime) || particle.Lifetime <= 0.02f || particle.Lifetime > 30f ||
                !Finite(particle.EmissionRate) || particle.EmissionRate < 0f || particle.EmissionRate > 60f ||
                !Finite(particle.SpeedMinimum) || !Finite(particle.SpeedMaximum) ||
                particle.SpeedMinimum < 0f || particle.SpeedMaximum < particle.SpeedMinimum || particle.SpeedMaximum > 20f ||
                !Finite(particle.SizeMinimum) || !Finite(particle.SizeMaximum) ||
                particle.SizeMinimum <= 0f || particle.SizeMaximum < particle.SizeMinimum || particle.SizeMaximum > 10f ||
                !Finite(particle.Gravity) || Mathf.Abs(particle.Gravity) > 20f ||
                !Finite(particle.AngularSpeed) || Mathf.Abs(particle.AngularSpeed) > 1440f ||
                !Finite(particle.ShapeRadius) || particle.ShapeRadius < 0f || particle.ShapeRadius > 10f ||
                !Finite(particle.ShapeAngle) || particle.ShapeAngle < 0f || particle.ShapeAngle > 89f)
                throw new InvalidOperationException("RAC2 particle numeric parameters are outside the supported profile.");
            if (!FiniteVector(particle.Origin) || !FiniteVector(particle.Direction) || !FiniteVector(particle.ShapeScale) ||
                particle.Direction.sqrMagnitude < 0.000001f || particle.ShapeScale.x < 0f ||
                particle.ShapeScale.y < 0f || particle.ShapeScale.z < 0f ||
                !FiniteColor(particle.StartColor) || !FiniteColor(particle.EndColor))
                throw new InvalidOperationException("RAC2 particle vector or color is invalid.");
            if (particle.FlipbookColumns < 1 || particle.FlipbookColumns > 16 ||
                particle.FlipbookRows < 1 || particle.FlipbookRows > 16 ||
                particle.FlipbookColumns * particle.FlipbookRows > 256)
                throw new InvalidOperationException("RAC2 particle flipbook supports at most 16x16 / 256 frames.");

            uint flags = (uint)((particle.Loop ? 1 : 0) | (particle.Billboard ? 2 : 0) | (particle.HideBaseMesh ? 4 : 0));
            using (var stream = new MemoryStream(140))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1u);
                writer.Write(flags);
                writer.Write((uint)particle.ShaderProfile);
                writer.Write((uint)particle.MaximumParticles);
                writer.Write(particle.Duration);
                writer.Write(particle.Lifetime);
                writer.Write(particle.EmissionRate);
                writer.Write(particle.SpeedMinimum);
                writer.Write(particle.SpeedMaximum);
                writer.Write(particle.SizeMinimum);
                writer.Write(particle.SizeMaximum);
                writer.Write(particle.Gravity);
                writer.Write(particle.AngularSpeed);
                WriteVector3(writer, particle.Origin);
                WriteVector3(writer, particle.Direction.normalized);
                writer.Write((uint)particle.Shape);
                writer.Write(particle.ShapeRadius);
                writer.Write(particle.ShapeAngle);
                WriteVector3(writer, particle.ShapeScale);
                WriteColor(writer, particle.StartColor);
                WriteColor(writer, particle.EndColor);
                writer.Write((uint)particle.FlipbookColumns);
                writer.Write((uint)particle.FlipbookRows);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static bool FiniteVector(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z) &&
                   Mathf.Abs(value.x) <= 1000000f && Mathf.Abs(value.y) <= 1000000f && Mathf.Abs(value.z) <= 1000000f;
        }

        private static bool FiniteColor(Color value)
        {
            return Finite(value.r) && Finite(value.g) && Finite(value.b) && Finite(value.a) &&
                   value.r >= 0f && value.r <= 16f && value.g >= 0f && value.g <= 16f &&
                   value.b >= 0f && value.b <= 16f && value.a >= 0f && value.a <= 1f;
        }

        private static void WriteColor(BinaryWriter writer, Color value)
        {
            writer.Write(value.r);
            writer.Write(value.g);
            writer.Write(value.b);
            writer.Write(value.a);
        }
        private static VatPayload BuildVat(VatClipData clip, int vertexCount)
        {
            if (clip.Positions == null || clip.Positions.Length < 2 || clip.Positions.Length > 240)
                throw new InvalidOperationException("VAT requires 2 to 240 baked frames.");
            if (!(clip.FramesPerSecond >= 1f && clip.FramesPerSecond <= 60f))
                throw new InvalidOperationException("VAT frame rate must be between 1 and 60 FPS.");

            bool hasNormals = clip.Normals != null;
            if (hasNormals && clip.Normals.Length != clip.Positions.Length)
                throw new InvalidOperationException("VAT normal frame count does not match position frames.");

            Vector3 minimum = Vector3.zero;
            Vector3 maximum = Vector3.zero;
            bool first = true;
            for (int frame = 0; frame < clip.Positions.Length; frame++)
            {
                Vector3[] values = clip.Positions[frame];
                if (values == null || values.Length != vertexCount)
                    throw new InvalidOperationException("VAT position vertex count changed while baking.");
                if (hasNormals && (clip.Normals[frame] == null || clip.Normals[frame].Length != vertexCount))
                    throw new InvalidOperationException("VAT normal vertex count changed while baking.");
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    Vector3 value = values[vertex];
                    if (!Finite(value.x) || !Finite(value.y) || !Finite(value.z))
                        throw new InvalidDataException("VAT contains a non-finite position.");
                    if (first) { minimum = value; maximum = value; first = false; }
                    else { minimum = Vector3.Min(minimum, value); maximum = Vector3.Max(maximum, value); }
                    if (hasNormals)
                    {
                        Vector3 normal = clip.Normals[frame][vertex];
                        if (!Finite(normal.x) || !Finite(normal.y) || !Finite(normal.z))
                            throw new InvalidDataException("VAT contains a non-finite normal.");
                    }
                }
            }

            Vector3 size = maximum - minimum;
            if (size.x < 0.000001f) size.x = 0.000001f;
            if (size.y < 0.000001f) size.y = 0.000001f;
            if (size.z < 0.000001f) size.z = 0.000001f;
            int width = Mathf.Min(2048, Mathf.NextPowerOfTwo(vertexCount));
            int rowsPerFrame = (vertexCount + width - 1) / width;
            int height = rowsPerFrame * clip.Positions.Length;
            if (height > 4096)
                throw new InvalidOperationException("VAT texture exceeds 4096 rows. Reduce frames or vertex count.");

            var uv1 = new Vector2[vertexCount];
            for (int vertex = 0; vertex < vertexCount; vertex++)
                uv1[vertex] = new Vector2(((vertex % width) + 0.5f) / width, vertex / width);

            int pixelCount = checked(width * height);
            byte[] positionRaw = new byte[checked(pixelCount * 8)];
            byte[] normalRaw = hasNormals ? new byte[checked(pixelCount * 4)] : null;
            for (int frame = 0; frame < clip.Positions.Length; frame++)
            {
                int firstPixel = frame * rowsPerFrame * width;
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    int pixel = firstPixel + vertex;
                    Vector3 value = clip.Positions[frame][vertex];
                    WriteHalf(positionRaw, pixel * 8, Mathf.Clamp01((value.x - minimum.x) / size.x));
                    WriteHalf(positionRaw, pixel * 8 + 2, Mathf.Clamp01((value.y - minimum.y) / size.y));
                    WriteHalf(positionRaw, pixel * 8 + 4, Mathf.Clamp01((value.z - minimum.z) / size.z));
                    WriteHalf(positionRaw, pixel * 8 + 6, 1f);
                    ValidateVatHalf(positionRaw, pixel * 8, value, minimum, size, frame, vertex);
                    if (hasNormals)
                    {
                        Vector3 normal = clip.Normals[frame][vertex].normalized;
                        normalRaw[pixel * 4] = ToByte(normal.x * 0.5f + 0.5f);
                        normalRaw[pixel * 4 + 1] = ToByte(normal.y * 0.5f + 0.5f);
                        normalRaw[pixel * 4 + 2] = ToByte(normal.z * 0.5f + 0.5f);
                        normalRaw[pixel * 4 + 3] = 255;
                    }
                }
            }

            string name = string.IsNullOrWhiteSpace(clip.Name) ? "Loop" : clip.Name;
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > 64) Array.Resize(ref nameBytes, 64);
            byte[] info;
            using (var stream = new MemoryStream(64 + nameBytes.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1u);
                writer.Write((uint)((clip.Loop ? 1 : 0) | (hasNormals ? 2 : 0)));
                writer.Write((uint)clip.Positions.Length);
                writer.Write(clip.FramesPerSecond);
                writer.Write((uint)width);
                writer.Write((uint)rowsPerFrame);
                writer.Write((uint)height);
                writer.Write(1u);
                writer.Write(hasNormals ? 2u : 0u);
                writer.Write((uint)nameBytes.Length);
                WriteVector3(writer, (minimum + maximum) * 0.5f);
                WriteVector3(writer, maximum - minimum);
                writer.Write(nameBytes);
                writer.Flush();
                info = stream.ToArray();
            }

            return new VatPayload
            {
                Bounds = new Bounds((minimum + maximum) * 0.5f, maximum - minimum),
                Info = info,
                Positions = BuildVatTexture(width, height, 1, positionRaw),
                Normals = hasNormals ? BuildVatTexture(width, height, 2, normalRaw) : null,
                Uv1 = uv1,
                FrameCount = clip.Positions.Length,
                Width = width,
                Height = height,
            };
        }

        private static byte[] BuildVatTexture(int width, int height, int format, byte[] raw)
        {
            using (var stream = new MemoryStream(20 + raw.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1u);
                writer.Write((uint)format);
                writer.Write((uint)width);
                writer.Write((uint)height);
                writer.Write((uint)raw.Length);
                writer.Write(raw);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void ValidateVatHalf(byte[] data, int offset, Vector3 source, Vector3 minimum, Vector3 size, int frame, int vertex)
        {
            Vector3 decoded = new Vector3(
                minimum.x + Mathf.HalfToFloat((ushort)(data[offset] | data[offset + 1] << 8)) * size.x,
                minimum.y + Mathf.HalfToFloat((ushort)(data[offset + 2] | data[offset + 3] << 8)) * size.y,
                minimum.z + Mathf.HalfToFloat((ushort)(data[offset + 4] | data[offset + 5] << 8)) * size.z);
            float allowed = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * 0.0011f + 0.00001f;
            if ((decoded - source).magnitude > allowed)
            {
                throw new InvalidDataException(
                    "VAT half encoding error at frame " + frame + ", vertex " + vertex + ". " +
                    "Source=" + source + ", decoded=" + decoded + ".");
            }
        }

        private static void WriteHalf(byte[] target, int offset, float value)        {
            uint bits = BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
            uint sign = (bits >> 16) & 0x8000u;
            int exponent = (int)((bits >> 23) & 0xffu) - 127 + 15;
            uint mantissa = bits & 0x7fffffu;
            uint half;
            if (exponent <= 0)
            {
                if (exponent < -10) half = sign;
                else
                {
                    mantissa = (mantissa | 0x800000u) >> (1 - exponent);
                    half = sign | ((mantissa + 0x1000u) >> 13);
                }
            }
            else if (exponent >= 31) half = sign | 0x7c00u;
            // Use addition so a rounded mantissa overflow carries into the half exponent.
            else half = sign | (((uint)exponent << 10) + ((mantissa + 0x1000u) >> 13));

            target[offset] = (byte)half;
            target[offset + 1] = (byte)(half >> 8);
        }

        private static byte ToByte(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 255f), 0, 255);
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void WriteSection(BinaryWriter writer, string type, int offset, int length)
        {
            foreach (char value in type) writer.Write((byte)value);
            writer.Write((uint)offset);
            writer.Write((uint)length);
            writer.Write(0u);
        }

        private static void WriteCompressedSection(BinaryWriter writer, Section section, int offset)
        {
            foreach (char value in section.Type) writer.Write((byte)value);
            writer.Write((uint)offset);
            writer.Write((uint)section.StoredBytes.Length);
            writer.Write((uint)section.Bytes.Length);
            writer.Write(section.Codec);
            writer.Write(section.Checksum);
        }

        private static byte[] CompressLz4Block(byte[] source)
        {
            if (source.Length == 0) return new byte[0];
            int maximum = checked(source.Length + source.Length / 255 + 32);
            byte[] output = new byte[maximum];
            int[] table = new int[65536];
            for (int index = 0; index < table.Length; index++) table[index] = -1;

            int input = 0;
            int anchor = 0;
            int written = 0;
            int matchLimit = source.Length - 12;
            while (input <= matchLimit)
            {
                uint sequence = ReadU32(source, input);
                int hash = (int)((sequence * 2654435761u) >> 16);
                int reference = table[hash];
                table[hash] = input;
                if (reference < 0 || input - reference > 65535 || ReadU32(source, reference) != sequence)
                {
                    input++;
                    continue;
                }

                int literalLength = input - anchor;
                int matchLength = 4;
                while (input + matchLength < source.Length - 5 && source[reference + matchLength] == source[input + matchLength])
                    matchLength++;

                int token = written++;
                output[token] = (byte)((literalLength < 15 ? literalLength : 15) << 4);
                if (literalLength >= 15) written = WriteLz4Length(output, written, literalLength - 15);
                Buffer.BlockCopy(source, anchor, output, written, literalLength);
                written += literalLength;

                int distance = input - reference;
                output[written++] = (byte)distance;
                output[written++] = (byte)(distance >> 8);
                int encodedMatch = matchLength - 4;
                output[token] |= (byte)(encodedMatch < 15 ? encodedMatch : 15);
                if (encodedMatch >= 15) written = WriteLz4Length(output, written, encodedMatch - 15);

                input += matchLength;
                anchor = input;
            }

            int remaining = source.Length - anchor;
            int finalToken = written++;
            output[finalToken] = (byte)((remaining < 15 ? remaining : 15) << 4);
            if (remaining >= 15) written = WriteLz4Length(output, written, remaining - 15);
            Buffer.BlockCopy(source, anchor, output, written, remaining);
            written += remaining;
            Array.Resize(ref output, written);
            return output;
        }

        private static int WriteLz4Length(byte[] output, int offset, int length)
        {
            while (length >= 255)
            {
                output[offset++] = 255;
                length -= 255;
            }
            output[offset++] = (byte)length;
            return offset;
        }

        private static uint ReadU32(byte[] data, int offset)
        {
            return (uint)data[offset] | ((uint)data[offset + 1] << 8) |
                   ((uint)data[offset + 2] << 16) | ((uint)data[offset + 3] << 24);
        }

        private static uint StandardAdler32(byte[] data, int offset, int length)
        {
            int a = 1, b = 0, end = offset + length;
            while (offset < end)
            {
                int stop = Math.Min(offset + 2776, end);
                while (offset < stop) { a += data[offset++]; b += a; }
                a %= 65521;
                b %= 65521;
            }
            return ((uint)b << 16) | (uint)a;
        }

        private static uint Adler32(byte[] data, int offset, int length)
        {
            const int modulus = 65521;
            int a = 1;
            int b = 0;
            int end = offset + length;
            while (offset < end)
            {
                int blockEnd = Math.Min(offset + 5552, end);
                while (offset < blockEnd)
                {
                    a += data[offset++];
                    b += a;
                }
                a %= modulus;
                b %= modulus;
            }
            return ((uint)b << 16) | (uint)a;
        }
        private static void WriteVector3(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static uint[] CollectIndices(Mesh mesh, int vertexCount)
        {
            var result = new List<uint>();
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles)
                {
                    throw new InvalidOperationException("RAC2 static profile supports triangle submeshes only.");
                }

                foreach (int value in mesh.GetIndices(subMesh))
                {
                    if (value < 0 || value >= vertexCount) throw new InvalidOperationException("Mesh contains an out-of-range index.");
                    result.Add((uint)value);
                }
            }

            if (result.Count == 0 || result.Count % 3 != 0) throw new InvalidOperationException("Mesh contains no valid triangles.");
            return result.ToArray();
        }

        private static Color ReadColor(Material material)
        {
            if (material == null) return Color.white;
            if (material.HasProperty("_BaseColor")) return material.GetColor("_BaseColor");
            if (material.HasProperty("_Color")) return material.GetColor("_Color");
            return Color.white;
        }

        private static Texture ReadTexture(Material material)
        {
            if (material == null) return null;
            if (material.HasProperty("_BaseMap"))
            {
                Texture value = material.GetTexture("_BaseMap");
                if (value != null) return value;
            }

            if (material.HasProperty("_MainTex")) return material.GetTexture("_MainTex");
            return material.mainTexture;
        }

        private static bool IsLilToon(Material material)
        {
            return material != null && material.shader != null &&
                   material.shader.name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Texture ReadNormalTexture(Material material)
        {
            return material != null &&
                   material.HasProperty("_UseBumpMap") &&
                   material.GetFloat("_UseBumpMap") > 0.5f &&
                   material.HasProperty("_BumpMap")
                ? material.GetTexture("_BumpMap")
                : null;
        }

        private static float ReadFloat(Material material, string property, float fallback, float minimum, float maximum)
        {
            return material != null && material.HasProperty(property)
                ? Mathf.Clamp(material.GetFloat(property), minimum, maximum)
                : fallback;
        }

        private static TexturePayload CopyTexture(Texture source, int maximumDimension, bool normalSemantic)
        {
            if (source.width <= 0 || source.height <= 0) throw new InvalidOperationException("Texture dimensions are invalid.");
            int limit = Mathf.Clamp(maximumDimension, 1, 1024);
            float scale = Mathf.Min(1f, (float)limit / Mathf.Max(source.width, source.height));
            int width = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = null;
            Texture2D copy = null;
            Material conversion = null;
            try
            {
                temporary = RenderTexture.GetTemporary(
                    width,
                    height,
                    0,
                    RenderTextureFormat.ARGB32,
                    normalSemantic ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.Default);
                if (normalSemantic)
                {
                    Shader shader = Shader.Find("Hidden/Avatar Catalog/RAC2 Normal Encode");
                    if (shader == null) throw new InvalidOperationException("RAC2 normal-map conversion shader is missing.");
                    conversion = new Material(shader);
                    string path = AssetDatabase.GetAssetPath(source);
                    TextureImporter importer = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path) as TextureImporter;
                    conversion.SetFloat("_SourceIsNormalMap", importer != null && importer.textureType == TextureImporterType.NormalMap ? 1f : 0f);
                    Graphics.Blit(source, temporary, conversion);
                }
                else
                {
                    Graphics.Blit(source, temporary);
                }

                RenderTexture.active = temporary;
                copy = new Texture2D(width, height, TextureFormat.RGBA32, false, normalSemantic);
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                copy.Apply(false, false);
                byte[] bytes = copy.GetRawTextureData();
                if (bytes == null || bytes.Length != width * height * 4)
                {
                    throw new InvalidDataException("RGBA32 conversion returned an unexpected byte count.");
                }

                return new TexturePayload { Width = width, Height = height, Bytes = bytes };
            }
            finally
            {
                RenderTexture.active = previous;
                if (copy != null) UnityEngine.Object.DestroyImmediate(copy);
                if (conversion != null) UnityEngine.Object.DestroyImmediate(conversion);
                if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
            }
        }
    }
}
