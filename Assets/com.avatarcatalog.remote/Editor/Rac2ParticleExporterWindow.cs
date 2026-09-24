using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public sealed class Rac2ParticleExporterWindow : EditorWindow
    {
        private enum BlendProfile { Alpha, Additive }

        private ParticleSystem _source;
        private BlendProfile _blend = BlendProfile.Alpha;
        private bool _hideBaseMesh = true;
        private bool _hasCollider = true;
        private bool _portable;

        [MenuItem("Tools/FLARE/Developer/Legacy Exporters/Export ParticleSystem to RAC2...")]
        private static void Open()
        {
            Rac2ParticleExporterWindow window = GetWindow<Rac2ParticleExporterWindow>("RAC2 Particle Export");
            window._source = Selection.activeGameObject == null ? null : Selection.activeGameObject.GetComponent<ParticleSystem>();
            window.minSize = new Vector2(430f, 190f);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("RAC2 Particle MVP", EditorStyles.boldLabel);
            _source = (ParticleSystem)EditorGUILayout.ObjectField("Particle System", _source, typeof(ParticleSystem), true);
            _blend = (BlendProfile)EditorGUILayout.EnumPopup("Blend Profile", _blend);
            _hideBaseMesh = EditorGUILayout.Toggle("Particle-only / Hide Base", _hideBaseMesh);
            _hasCollider = EditorGUILayout.Toggle("Add Box Collider", _hasCollider);
            _portable = EditorGUILayout.Toggle("Portable / VRC Pickup", _portable);
            if (_portable) _hasCollider = true;
            EditorGUILayout.HelpBox("Supports one emitter, max 32 particles, Point/Sphere/Cone/Box, Alpha/Additive, Billboard/Mesh, gravity, color fade and flipbook. Noise, Collision, Trails, Sub Emitters and Lights are rejected.", MessageType.Info);
            using (new EditorGUI.DisabledScope(_source == null))
            {
                if (GUILayout.Button("Export RAC2 Particle...", GUILayout.Height(30f))) Export();
            }
        }

        private void Export()
        {
            ParticleSystemRenderer sourceRenderer = _source.GetComponent<ParticleSystemRenderer>();
            if (sourceRenderer == null) throw new InvalidOperationException("ParticleSystemRenderer is missing.");
            bool billboard = sourceRenderer.renderMode != ParticleSystemRenderMode.Mesh;
            if (!billboard && sourceRenderer.mesh == null) throw new InvalidOperationException("Mesh particle renderer has no mesh.");
            if (sourceRenderer.renderMode != ParticleSystemRenderMode.Mesh &&
                sourceRenderer.renderMode != ParticleSystemRenderMode.Billboard &&
                sourceRenderer.renderMode != ParticleSystemRenderMode.HorizontalBillboard &&
                sourceRenderer.renderMode != ParticleSystemRenderMode.VerticalBillboard)
                throw new NotSupportedException("RAC2 supports Billboard and Mesh particle render modes.");

            RejectUnsupportedModules(_source);
            string output = EditorUtility.SaveFilePanel("Save RAC2 Particle", "", _source.name, "rac2");
            if (string.IsNullOrEmpty(output)) return;

            Mesh mesh = billboard ? CreateQuad() : UnityEngine.Object.Instantiate(sourceRenderer.mesh);
            Material carrier = new Material(Shader.Find("Standard"));
            try
            {
                ParticleSystem.MainModule main = _source.main;
                ParticleSystem.EmissionModule emission = _source.emission;
                ParticleSystem.ShapeModule shape = _source.shape;
                float lifeMin, lifeMax, speedMin, speedMax, sizeMin, sizeMax, gravityMin, gravityMax;
                ReadCurve(main.startLifetime, "startLifetime", out lifeMin, out lifeMax);
                ReadCurve(main.startSpeed, "startSpeed", out speedMin, out speedMax);
                ReadCurve(main.startSize, "startSize", out sizeMin, out sizeMax);
                ReadCurve(main.gravityModifier, "gravityModifier", out gravityMin, out gravityMax);
                if (Mathf.Abs(lifeMax - lifeMin) > 0.0001f)
                    throw new NotSupportedException("RAC2 MVP requires a constant particle lifetime.");
                if (Mathf.Abs(gravityMax - gravityMin) > 0.0001f)
                    throw new NotSupportedException("RAC2 MVP requires a constant gravity modifier.");

                float rateMin, rateMax;
                ReadCurve(emission.rateOverTime, "emission rate", out rateMin, out rateMax);
                if (Mathf.Abs(rateMax - rateMin) > 0.0001f)
                    throw new NotSupportedException("RAC2 MVP requires a constant emission rate.");

                Color startColor, endColor;
                ReadColors(_source, out startColor, out endColor);
                int shapeType;
                float radius, angle;
                Vector3 boxScale;
                ReadShape(shape, out shapeType, out radius, out angle, out boxScale);

                int columns = 1;
                int rows = 1;
                ParticleSystem.TextureSheetAnimationModule sheet = _source.textureSheetAnimation;
                if (sheet.enabled)
                {
                    columns = sheet.numTilesX;
                    rows = sheet.numTilesY;
                }

                float angularSpeed = 0f;
                ParticleSystem.RotationOverLifetimeModule rotation = _source.rotationOverLifetime;
                if (rotation.enabled)
                {
                    float angularMin, angularMax;
                    ReadCurve(rotation.z, "rotation over lifetime", out angularMin, out angularMax);
                    angularSpeed = angularMax * Mathf.Rad2Deg;
                }

                Texture texture = null;
                Material sourceMaterial = sourceRenderer.sharedMaterial;
                if (sourceMaterial != null)
                {
                    if (sourceMaterial.HasProperty("_MainTex")) texture = sourceMaterial.GetTexture("_MainTex");
                    if (texture == null) texture = sourceMaterial.mainTexture;
                }

                var particle = new Rac2BinaryExporter.ParticleData
                {
                    Loop = main.loop,
                    Billboard = billboard,
                    HideBaseMesh = _hideBaseMesh,
                    ShaderProfile = (int)_blend,
                    MaximumParticles = Mathf.Clamp(main.maxParticles, 1, 32),
                    Duration = main.duration,
                    Lifetime = lifeMax,
                    EmissionRate = emission.enabled ? rateMax : 0f,
                    SpeedMinimum = speedMin,
                    SpeedMaximum = speedMax,
                    SizeMinimum = sizeMin,
                    SizeMaximum = sizeMax,
                    Gravity = gravityMax * Physics.gravity.magnitude,
                    AngularSpeed = angularSpeed,
                    Origin = _source.transform.localPosition,
                    Direction = _source.transform.localRotation * Vector3.forward,
                    Shape = shapeType,
                    ShapeRadius = radius,
                    ShapeAngle = angle,
                    ShapeScale = boxScale,
                    StartColor = startColor,
                    EndColor = endColor,
                    FlipbookColumns = columns,
                    FlipbookRows = rows,
                    Texture = texture,
                };

                var interaction = new Rac2BinaryExporter.InteractionData
                {
                    HasCollider = _hasCollider,
                    IsPortable = _portable,
                };
                Rac2BinaryExporter.ProductData product = Rac2ProductMetadataUtility.From(_source.gameObject);
                Rac2BinaryExporter.ExportSummary result = Rac2BinaryExporter.ExportMesh(
                    mesh, carrier, output, particle: particle, interaction: interaction, product: product);
                if (output.Replace('\\', '/').StartsWith(Application.dataPath.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase))
                    AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("RAC2 Particle exported",
                    result.ParticleMaximum + " particle slots / " + result.FileSize + " stored / " +
                    result.UncompressedFileSize + " raw bytes / " + result.CompressedSectionCount + " LZ4 chunks", "OK");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(carrier);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        private static void RejectUnsupportedModules(ParticleSystem source)
        {
            if (source.noise.enabled) throw new NotSupportedException("Noise is not supported by the RAC2 particle MVP.");
            if (source.collision.enabled) throw new NotSupportedException("Collision is not supported by the RAC2 particle MVP.");
            if (source.trails.enabled) throw new NotSupportedException("Trails are not supported by the RAC2 particle MVP.");
            if (source.subEmitters.enabled) throw new NotSupportedException("Sub Emitters are not supported by the RAC2 particle MVP.");
            if (source.lights.enabled) throw new NotSupportedException("Particle Lights are not supported by the RAC2 particle MVP.");
            if (source.main.simulationSpace != ParticleSystemSimulationSpace.Local)
                throw new NotSupportedException("RAC2 particle simulation space must be Local.");
            if (source.main.startSize3D)
                throw new NotSupportedException("RAC2 particle MVP uses uniform start size.");
        }

        private static void ReadCurve(ParticleSystem.MinMaxCurve curve, string label, out float minimum, out float maximum)
        {
            if (curve.mode == ParticleSystemCurveMode.Constant)
            {
                minimum = curve.constant;
                maximum = curve.constant;
                return;
            }
            if (curve.mode == ParticleSystemCurveMode.TwoConstants)
            {
                minimum = curve.constantMin;
                maximum = curve.constantMax;
                return;
            }
            throw new NotSupportedException("RAC2 particle " + label + " supports Constant or Two Constants only.");
        }

        private static void ReadColors(ParticleSystem source, out Color start, out Color end)
        {
            ParticleSystem.MinMaxGradient initial = source.main.startColor;
            if (initial.mode == ParticleSystemGradientMode.Color) start = initial.color;
            else if (initial.mode == ParticleSystemGradientMode.TwoColors) start = initial.colorMax;
            else throw new NotSupportedException("RAC2 particle start color supports Color or Two Colors only.");

            end = new Color(start.r, start.g, start.b, 0f);
            ParticleSystem.ColorOverLifetimeModule color = source.colorOverLifetime;
            if (!color.enabled) return;
            ParticleSystem.MinMaxGradient gradient = color.color;
            if (gradient.mode == ParticleSystemGradientMode.Gradient)
            {
                Color initialColor = start;
                start = initialColor * gradient.gradient.Evaluate(0f);
                end = initialColor * gradient.gradient.Evaluate(1f);
            }
            else if (gradient.mode == ParticleSystemGradientMode.Color)
            {
                start *= gradient.color;
                end = new Color(start.r, start.g, start.b, 0f);
            }
            else throw new NotSupportedException("RAC2 color over lifetime supports one Color or Gradient.");
        }

        private static void ReadShape(ParticleSystem.ShapeModule shape, out int type, out float radius, out float angle, out Vector3 scale)
        {
            type = 0;
            radius = 0f;
            angle = 0f;
            scale = Vector3.one;
            if (!shape.enabled) return;
            switch (shape.shapeType)
            {
                case ParticleSystemShapeType.Sphere:
                case ParticleSystemShapeType.SphereShell:
                    type = 1;
                    radius = shape.radius;
                    return;
                case ParticleSystemShapeType.Cone:
                case ParticleSystemShapeType.ConeVolume:
                    type = 2;
                    radius = shape.radius;
                    angle = shape.angle;
                    return;
                case ParticleSystemShapeType.Box:
                case ParticleSystemShapeType.BoxShell:
                case ParticleSystemShapeType.BoxEdge:
                    type = 3;
                    scale = shape.scale;
                    return;
                default:
                    throw new NotSupportedException("RAC2 particle shape must be Point, Sphere, Cone, or Box.");
            }
        }

        private static Mesh CreateQuad()
        {
            Mesh mesh = new Mesh { name = "RAC2 Particle Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
