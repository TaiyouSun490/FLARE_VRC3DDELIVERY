using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    /// <summary>
    /// Writes a static snapshot of a MeshFilter or a baked SkinnedMeshRenderer
    /// using the Remote Avatar Catalog version 1 binary format.
    /// </summary>
    public static class Rac1BinaryExporter
    {
        public const uint Version = 1;

        [Flags]
        public enum DataFlags : uint
        {
            None = 0,
            HasNormals = 1u << 0,
            HasUv0 = 1u << 1,
            HasColors = 1u << 2,
            HasTextureRgba32 = 1u << 3
        }

        public sealed class Options
        {
            public bool IncludeAlbedoTexture = true;
            public int MaximumTextureDimension = 1024;
            public Texture2D AlbedoOverride;
        }

        public struct ExportSummary
        {
            public int VertexCount;
            public int IndexCount;
            public int TextureWidth;
            public int TextureHeight;
            public DataFlags Flags;
            public long FileSize;
            public string TextureWarning;
        }

        private sealed class TexturePayload
        {
            public int Width;
            public int Height;
            public byte[] Bytes;
        }

        /// <summary>
        /// Exports a MeshFilter or SkinnedMeshRenderer component. A GameObject is
        /// also accepted and is resolved to one of those components.
        /// </summary>
        public static ExportSummary Export(UnityEngine.Object source, string outputPath, Options options = null)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("An output path is required.", nameof(outputPath));
            }

            options = options ?? new Options();
            ResolveSource(source, out MeshFilter meshFilter, out SkinnedMeshRenderer skinnedRenderer);

            Mesh temporaryBakedMesh = null;
            Mesh mesh;
            Renderer materialRenderer;

            if (skinnedRenderer != null)
            {
                if (skinnedRenderer.sharedMesh == null)
                {
                    throw new InvalidOperationException("The selected SkinnedMeshRenderer has no mesh.");
                }

                temporaryBakedMesh = new Mesh { name = skinnedRenderer.sharedMesh.name + " (RAC1 Bake)" };
                skinnedRenderer.BakeMesh(temporaryBakedMesh);
                mesh = temporaryBakedMesh;
                materialRenderer = skinnedRenderer;
            }
            else
            {
                mesh = meshFilter.sharedMesh;
                if (mesh == null)
                {
                    throw new InvalidOperationException("The selected MeshFilter has no mesh.");
                }

                materialRenderer = meshFilter.GetComponent<Renderer>();
            }

            try
            {
                return ExportMesh(mesh, materialRenderer, outputPath, options);
            }
            finally
            {
                if (temporaryBakedMesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(temporaryBakedMesh);
                }
            }
        }

        private static ExportSummary ExportMesh(Mesh mesh, Renderer renderer, string outputPath, Options options)
        {
            if (!mesh.isReadable)
            {
                throw new InvalidOperationException(
                    $"Mesh '{mesh.name}' is not readable. Enable Read/Write in its model import settings before exporting.");
            }

            Vector3[] positions = mesh.vertices;
            if (positions == null || positions.Length == 0)
            {
                throw new InvalidOperationException("The mesh contains no vertices.");
            }

            Vector3[] normals = mesh.normals;
            Vector2[] uv0 = mesh.uv;
            Color32[] colors = mesh.colors32;
            uint[] indices = CollectTriangleIndices(mesh, positions.Length);

            DataFlags flags = DataFlags.None;
            if (normals != null && normals.Length == positions.Length)
            {
                flags |= DataFlags.HasNormals;
            }

            if (uv0 != null && uv0.Length == positions.Length)
            {
                flags |= DataFlags.HasUv0;
            }

            if (colors != null && colors.Length == positions.Length)
            {
                flags |= DataFlags.HasColors;
            }

            Material material = renderer != null ? renderer.sharedMaterial : null;
            Color baseColor = ReadBaseColor(material);
            Texture albedo = options.AlbedoOverride != null ? options.AlbedoOverride : ReadAlbedoTexture(material);

            TexturePayload texturePayload = null;
            string textureWarning = null;
            if (options.IncludeAlbedoTexture)
            {
                if (albedo == null)
                {
                    textureWarning = "No albedo texture was found; the RAC1 file was exported without texture data.";
                }
                else
                {
                    try
                    {
                        texturePayload = CopyTextureToRgba32(albedo, options.MaximumTextureDimension);
                        flags |= DataFlags.HasTextureRgba32;
                    }
                    catch (Exception exception)
                    {
                        textureWarning = $"The albedo texture could not be copied ({exception.Message}); " +
                                         "the RAC1 file was exported without texture data.";
                    }
                }
            }

            byte[] fileBytes;
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, flags, positions.Length, indices.Length, mesh.bounds, baseColor);
                WriteVector3Array(writer, positions);

                if ((flags & DataFlags.HasNormals) != 0)
                {
                    WriteVector3Array(writer, normals);
                }

                if ((flags & DataFlags.HasUv0) != 0)
                {
                    WriteVector2Array(writer, uv0);
                }

                if ((flags & DataFlags.HasColors) != 0)
                {
                    WriteColor32Array(writer, colors);
                }

                WriteUInt32Array(writer, indices);

                if (texturePayload != null)
                {
                    writer.Write(checked((uint)texturePayload.Width));
                    writer.Write(checked((uint)texturePayload.Height));
                    writer.Write(checked((uint)texturePayload.Bytes.Length));
                    writer.Write(texturePayload.Bytes);
                }

                writer.Flush();
                fileBytes = stream.ToArray();
            }

            string fullPath = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException($"Output directory does not exist: {directory}");
            }

            File.WriteAllBytes(fullPath, fileBytes);

            return new ExportSummary
            {
                VertexCount = positions.Length,
                IndexCount = indices.Length,
                TextureWidth = texturePayload != null ? texturePayload.Width : 0,
                TextureHeight = texturePayload != null ? texturePayload.Height : 0,
                Flags = flags,
                FileSize = fileBytes.LongLength,
                TextureWarning = textureWarning
            };
        }

        private static void ResolveSource(
            UnityEngine.Object source,
            out MeshFilter meshFilter,
            out SkinnedMeshRenderer skinnedRenderer)
        {
            meshFilter = null;
            skinnedRenderer = null;

            if (source is SkinnedMeshRenderer selectedSkinned)
            {
                skinnedRenderer = selectedSkinned;
                return;
            }

            if (source is MeshFilter selectedFilter)
            {
                meshFilter = selectedFilter;
                return;
            }

            GameObject gameObject = source as GameObject;
            if (gameObject == null && source is Component component)
            {
                gameObject = component.gameObject;
            }

            if (gameObject != null)
            {
                skinnedRenderer = gameObject.GetComponent<SkinnedMeshRenderer>();
                if (skinnedRenderer == null)
                {
                    meshFilter = gameObject.GetComponent<MeshFilter>();
                }
            }

            if (skinnedRenderer == null && meshFilter == null)
            {
                throw new ArgumentException(
                    "Select a GameObject with a MeshFilter or SkinnedMeshRenderer, or select the component itself.",
                    nameof(source));
            }
        }

        private static uint[] CollectTriangleIndices(Mesh mesh, int vertexCount)
        {
            List<uint> result = new List<uint>();

            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles)
                {
                    throw new InvalidOperationException(
                        $"Sub-mesh {subMesh} uses {mesh.GetTopology(subMesh)} topology. RAC1 v1 supports triangles only.");
                }

                int[] subMeshIndices = mesh.GetIndices(subMesh);
                if ((subMeshIndices.Length % 3) != 0)
                {
                    throw new InvalidOperationException($"Sub-mesh {subMesh} has an invalid triangle index count.");
                }

                for (int index = 0; index < subMeshIndices.Length; index++)
                {
                    int vertexIndex = subMeshIndices[index];
                    if (vertexIndex < 0 || vertexIndex >= vertexCount)
                    {
                        throw new InvalidOperationException(
                            $"Sub-mesh {subMesh} contains out-of-range vertex index {vertexIndex}.");
                    }

                    result.Add((uint)vertexIndex);
                }
            }

            if (result.Count == 0)
            {
                throw new InvalidOperationException("The mesh contains no triangle indices.");
            }

            return result.ToArray();
        }

        private static Color ReadBaseColor(Material material)
        {
            if (material == null)
            {
                return Color.white;
            }

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

        private static Texture ReadAlbedoTexture(Material material)
        {
            if (material == null)
            {
                return null;
            }

            if (material.HasProperty("_BaseMap"))
            {
                Texture texture = material.GetTexture("_BaseMap");
                if (texture != null)
                {
                    return texture;
                }
            }

            if (material.HasProperty("_MainTex"))
            {
                return material.GetTexture("_MainTex");
            }

            return material.mainTexture;
        }

        private static TexturePayload CopyTextureToRgba32(Texture source, int maximumDimension)
        {
            if (source.width <= 0 || source.height <= 0)
            {
                throw new InvalidOperationException("The source texture has invalid dimensions.");
            }

            int sizeLimit = Mathf.Clamp(maximumDimension, 1, 8192);
            float scale = Mathf.Min(1f, (float)sizeLimit / Mathf.Max(source.width, source.height));
            int targetWidth = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int targetHeight = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));

            // A direct copy avoids a GPU color-space round trip when the source is
            // already readable and no resize is needed.
            if (source is Texture2D readableTexture &&
                readableTexture.isReadable &&
                targetWidth == source.width &&
                targetHeight == source.height)
            {
                try
                {
                    Color32[] pixels = readableTexture.GetPixels32();
                    if (pixels.Length == checked(targetWidth * targetHeight))
                    {
                        byte[] bytes = new byte[checked(pixels.Length * 4)];
                        for (int i = 0, offset = 0; i < pixels.Length; i++, offset += 4)
                        {
                            Color32 pixel = pixels[i];
                            bytes[offset] = pixel.r;
                            bytes[offset + 1] = pixel.g;
                            bytes[offset + 2] = pixel.b;
                            bytes[offset + 3] = pixel.a;
                        }

                        return new TexturePayload
                        {
                            Width = targetWidth,
                            Height = targetHeight,
                            Bytes = bytes
                        };
                    }
                }
                catch (UnityException)
                {
                    // Some readable compressed/imported formats still reject a CPU
                    // read. The RenderTexture path below handles those safely.
                }
            }

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture temporary = null;
            Texture2D copy = null;

            try
            {
                temporary = RenderTexture.GetTemporary(
                    targetWidth,
                    targetHeight,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default);
                temporary.filterMode = FilterMode.Bilinear;
                Graphics.Blit(source, temporary);

                RenderTexture.active = temporary;
                copy = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0, false);
                copy.Apply(false, false);

                byte[] bytes = copy.GetRawTextureData();
                int expectedLength = checked(targetWidth * targetHeight * 4);
                if (bytes == null || bytes.Length != expectedLength)
                {
                    throw new InvalidDataException(
                        $"RGBA32 conversion produced {bytes?.Length ?? 0} bytes; expected {expectedLength}.");
                }

                return new TexturePayload
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Bytes = bytes
                };
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (copy != null)
                {
                    UnityEngine.Object.DestroyImmediate(copy);
                }

                if (temporary != null)
                {
                    RenderTexture.ReleaseTemporary(temporary);
                }
            }
        }

        private static void WriteHeader(
            BinaryWriter writer,
            DataFlags flags,
            int vertexCount,
            int indexCount,
            Bounds bounds,
            Color baseColor)
        {
            writer.Write((byte)'R');
            writer.Write((byte)'A');
            writer.Write((byte)'C');
            writer.Write((byte)'1');
            writer.Write(Version);
            writer.Write((uint)flags);
            writer.Write(checked((uint)vertexCount));
            writer.Write(checked((uint)indexCount));

            WriteVector3(writer, bounds.center);
            WriteVector3(writer, bounds.size);
            writer.Write(baseColor.r);
            writer.Write(baseColor.g);
            writer.Write(baseColor.b);
            writer.Write(baseColor.a);
        }

        private static void WriteVector3Array(BinaryWriter writer, Vector3[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                WriteVector3(writer, values[i]);
            }
        }

        private static void WriteVector2Array(BinaryWriter writer, Vector2[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                writer.Write(values[i].x);
                writer.Write(values[i].y);
            }
        }

        private static void WriteColor32Array(BinaryWriter writer, Color32[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                writer.Write(values[i].r);
                writer.Write(values[i].g);
                writer.Write(values[i].b);
                writer.Write(values[i].a);
            }
        }

        private static void WriteUInt32Array(BinaryWriter writer, uint[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                writer.Write(values[i]);
            }
        }

        private static void WriteVector3(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }
    }
}
