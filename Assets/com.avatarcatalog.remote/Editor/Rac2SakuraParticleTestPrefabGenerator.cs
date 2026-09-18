using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    internal static class Rac2SakuraParticleTestPrefabGenerator
    {
        private const string SampleFolder = "Assets/RemoteAvatarCatalogDistribution/Samples/SakuraParticle";
        private const string TexturePath = SampleFolder + "/RAC2-Sakura-Petal.asset";
        private const string MaterialPath = SampleFolder + "/RAC2-Sakura-Petal.mat";
        private const string MeshPath = SampleFolder + "/RAC2-Sakura-Petal-Mesh.asset";
        private const string PrefabPath = SampleFolder + "/RAC2-Sakura-Particle-Test.prefab";

        [InitializeOnLoadMethod]
        private static void CreateMissingSampleAfterReload()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
                EditorApplication.delayCall += CreateSample;
        }

        [MenuItem("Tools/Avatar Catalog/Developer/Samples/Create Sakura Particle Test Prefab")]
        private static void CreateSample()
        {
            EnsureFolders();
            Texture2D texture = CreateOrUpdateTexture();
            Material material = CreateOrUpdateMaterial(texture);
            Mesh mesh = CreateOrUpdateMesh();

            GameObject root = new GameObject("RAC2 Sakura Particle Test");
            GameObject emitterObject = new GameObject("Sakura Emitter", typeof(ParticleSystem));
            emitterObject.transform.SetParent(root.transform, false);
            emitterObject.transform.localPosition = new Vector3(0f, 2.25f, 0f);
            emitterObject.transform.localRotation = Quaternion.LookRotation(new Vector3(0.28f, -1f, 0.14f).normalized, Vector3.up);

            ParticleSystem particle = emitterObject.GetComponent<ParticleSystem>();
            ParticleSystem.MainModule main = particle.main;
            main.loop = true;
            main.duration = 4f;
            main.startLifetime = 4f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.08f, 0.22f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.14f);
            main.startColor = new Color(1f, 0.72f, 0.84f, 0.95f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = 0.025f;
            main.maxParticles = 24;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.playOnAwake = true;

            ParticleSystem.EmissionModule emission = particle.emission;
            emission.enabled = true;
            emission.rateOverTime = 6f;

            ParticleSystem.ShapeModule shape = particle.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(2.2f, 0.08f, 1.3f);

            ParticleSystem.RotationOverLifetimeModule rotation = particle.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = 1.35f;

            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(new Color(1f, 0.76f, 0.86f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0.95f, 0f),
                    new GradientAlphaKey(0.8f, 0.72f),
                    new GradientAlphaKey(0f, 1f),
                });
            ParticleSystem.ColorOverLifetimeModule color = particle.colorOverLifetime;
            color.enabled = true;
            color.color = gradient;

            ParticleSystemRenderer renderer = emitterObject.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Mesh;
            renderer.mesh = mesh;
            renderer.sharedMaterial = material;
            renderer.alignment = ParticleSystemRenderSpace.Local;

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
            Debug.Log("[RAC2] Sakura particle test prefab is ready: " + PrefabPath);
        }

        private static Texture2D CreateOrUpdateTexture()
        {
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            if (texture == null)
            {
                texture = new Texture2D(64, 64, TextureFormat.RGBA32, false, true)
                {
                    name = "RAC2 Sakura Petal",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                AssetDatabase.CreateAsset(texture, TexturePath);
            }

            Color32[] pixels = new Color32[64 * 64];
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    float px = (x + 0.5f) / 32f - 1f;
                    float py = (y + 0.5f) / 32f - 1f;
                    float width = 0.58f * (1f - Mathf.Abs(py) * 0.42f);
                    float notch = py > 0.42f ? 0.16f * (py - 0.42f) / 0.58f : 0f;
                    float shape = 1f - Mathf.Max(Mathf.Abs(px) / Mathf.Max(0.08f, width - notch), Mathf.Abs(py));
                    float alpha = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(shape * 18f));
                    float blush = Mathf.Clamp01(1f - Mathf.Sqrt(px * px + (py + 0.32f) * (py + 0.32f)) * 1.5f);
                    byte red = 255;
                    byte green = (byte)Mathf.RoundToInt(Mathf.Lerp(218f, 245f, 1f - blush));
                    byte blue = (byte)Mathf.RoundToInt(Mathf.Lerp(230f, 250f, 1f - blush));
                    pixels[y * 64 + x] = new Color32(red, green, blue, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            EditorUtility.SetDirty(texture);
            return texture;
        }

        private static Material CreateOrUpdateMaterial(Texture2D texture)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            Shader shader = Shader.Find("Avatar Catalog/RAC2 Particle Alpha");
            if (shader == null) shader = Shader.Find("Unlit/Transparent");
            if (material == null)
            {
                material = new Material(shader) { name = "RAC2 Sakura Petal" };
                AssetDatabase.CreateAsset(material, MaterialPath);
            }
            else material.shader = shader;
            material.mainTexture = texture;
            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Mesh CreateOrUpdateMesh()
        {
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            if (mesh == null)
            {
                mesh = new Mesh { name = "RAC2 Sakura Petal Mesh" };
                AssetDatabase.CreateAsset(mesh, MeshPath);
            }
            else mesh.Clear();

            mesh.vertices = new[]
            {
                new Vector3(-0.46f, -0.5f, 0f), new Vector3(0.46f, -0.5f, 0f),
                new Vector3(-0.46f, 0.5f, 0f), new Vector3(0.46f, 0.5f, 0f),
                new Vector3(0f, -0.5f, -0.46f), new Vector3(0f, -0.5f, 0.46f),
                new Vector3(0f, 0.5f, -0.46f), new Vector3(0f, 0.5f, 0.46f),
            };
            mesh.uv = new[]
            {
                Vector2.zero, Vector2.right, Vector2.up, Vector2.one,
                Vector2.zero, Vector2.right, Vector2.up, Vector2.one,
            };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1, 4, 6, 5, 6, 7, 5 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        private static void EnsureFolders()
        {
            string[] parts = SampleFolder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
