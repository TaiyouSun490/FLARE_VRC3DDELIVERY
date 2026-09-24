using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public sealed partial class Rac2CreatorWindow
    {
        private const float BoothHalfWidth = 1.5f;
        private const float BoothHalfDepth = 1.5f;
        private const float BoothHeight = 2.7f;
        private const float BoothTolerance = 0.1f;

        private sealed class BoothPreviewItem
        {
            public UnityEngine.Object Source;
            public string Label;
            public bool IsParticle;
            public Bounds LocalBounds;
        }

        private readonly List<BoothPreviewItem> _boothPreviewItems =
            new List<BoothPreviewItem>();
        private bool _showBoothGuide = true;
        private bool _hasBoothPreviewBounds;
        private bool _boothPreviewIsExact;
        private Bounds _boothPreviewBounds;
        private string _boothPreviewError = "";
        private double _nextBoothPreviewRefresh;
        private double _keepExactPreviewUntil;

        private void OnEnable()
        {
            RefreshSummary();
            SceneView.duringSceneGui -= DrawBoothSceneGuide;
            SceneView.duringSceneGui += DrawBoothSceneGuide;
            RefreshBoothGuide(false);
        }

        private void OnDisable()
        {
            CancelBake();
            SceneView.duringSceneGui -= DrawBoothSceneGuide;
        }

        private void OnInspectorUpdate()
        {
            if (_bakeRunner != null) return;
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextBoothPreviewRefresh ||
                now < _keepExactPreviewUntil)
                return;

            _nextBoothPreviewRefresh = now + 0.35d;
            RefreshBoothGuide(false);
            Repaint();
            if (_showBoothGuide) SceneView.RepaintAll();
        }

        private void OnHierarchyChange()
        {
            if (_bakeRunner != null) return;
            _keepExactPreviewUntil = 0d;
            RefreshBoothGuide(true);
            Repaint();
        }

        private void DrawBoothGuideControls()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(
                L("Placement preview after export"), EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _showBoothGuide = EditorGUILayout.ToggleLeft(
                L("Show the 3m x 3m x 2.7m placement guide in the Scene view"),
                _showBoothGuide);
            if (EditorGUI.EndChangeCheck())
                SceneView.RepaintAll();

            MessageType type = MessageType.Info;
            if (_root == null || !_hasBoothPreviewBounds)
                type = MessageType.Warning;
            else if (!BoothBoundsFit(_boothPreviewBounds))
                type = MessageType.Error;
            else if (!string.IsNullOrEmpty(_boothPreviewError))
                type = MessageType.Warning;

            EditorGUILayout.HelpBox(BuildBoothGuideMessage(), type);

            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(_root == null))
            {
                if (GUILayout.Button(L("Frame exported booth")))
                    FocusBoothInScene();
                using (new EditorGUI.DisabledScope(!HasBoothOffender()))
                {
                    if (GUILayout.Button(L("Select oversized object")))
                        SelectFirstBoothOffender();
                }
            }
        }

        private void ClearBoothGuide()
        {
            _boothPreviewItems.Clear();
            _hasBoothPreviewBounds = false;
            _boothPreviewIsExact = false;
            _boothPreviewBounds = default(Bounds);
            _boothPreviewError = "";
            _keepExactPreviewUntil = 0d;
            SceneView.RepaintAll();
        }

        private void RefreshBoothGuide(bool repaintScene)
        {
            _boothPreviewItems.Clear();
            _hasBoothPreviewBounds = false;
            _boothPreviewIsExact = false;
            _boothPreviewError = "";

            if (_root == null)
            {
                if (repaintScene) SceneView.RepaintAll();
                return;
            }

            try
            {
                MeshRenderer[] staticRenderers =
                    _root.GetComponentsInChildren<MeshRenderer>(true);
                for (int index = 0; index < staticRenderers.Length; index++)
                {
                    MeshRenderer renderer = staticRenderers[index];
                    if (!renderer.enabled ||
                        !renderer.gameObject.activeInHierarchy ||
                        renderer.GetComponentInParent<ParticleSystem>() != null)
                        continue;

                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    Bounds local = TransformBounds(
                        filter.sharedMesh.bounds,
                        _root.transform.worldToLocalMatrix *
                        renderer.transform.localToWorldMatrix);
                    AddBoothPreviewItem(
                        renderer.gameObject,
                        RelativePath(renderer.transform),
                        false,
                        local);
                }

                SkinnedMeshRenderer[] skinnedRenderers =
                    _root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (int index = 0; index < skinnedRenderers.Length; index++)
                {
                    SkinnedMeshRenderer renderer = skinnedRenderers[index];
                    if (!renderer.enabled ||
                        !renderer.gameObject.activeInHierarchy ||
                        renderer.sharedMesh == null)
                        continue;

                    Mesh baked = new Mesh
                        { name = renderer.name + " RAC2 Booth Preview" };
                    try
                    {
                        renderer.BakeMesh(baked);
                        Bounds local = TransformBounds(
                            baked.bounds,
                            _root.transform.worldToLocalMatrix *
                            renderer.transform.localToWorldMatrix);
                        AddBoothPreviewItem(
                            renderer.gameObject,
                            RelativePath(renderer.transform),
                            false,
                            local);
                    }
                    finally
                    {
                        DestroyImmediate(baked);
                    }
                }

                ParticleSystem[] particles =
                    _root.GetComponentsInChildren<ParticleSystem>(true);
                if (!_hasBoothPreviewBounds)
                {
                    for (int index = 0; index < particles.Length; index++)
                    {
                        ParticleSystem particle = particles[index];
                        if (!particle.gameObject.activeInHierarchy) continue;
                        // Particle-only content needs a placement anchor, but
                        // emitter position, Shape, size, and travel never
                        // contribute to floor, center, or booth dimensions.
                        _boothPreviewBounds = new Bounds(
                            _root.transform.InverseTransformPoint(
                                particle.transform.position),
                            Vector3.zero);
                        _hasBoothPreviewBounds = true;
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                AppendBoothPreviewError(exception.Message);
            }

            if (repaintScene) SceneView.RepaintAll();
        }

        private void SetExactBoothGuide(
            Rac2BinaryExporter.BundleData bundle)
        {
            _boothPreviewItems.Clear();
            _hasBoothPreviewBounds = false;
            _boothPreviewIsExact = true;
            _boothPreviewError = "";

            for (int index = 0; index < bundle.RenderNodes.Count; index++)
            {
                Rac2BinaryExporter.RenderNodeData node =
                    bundle.RenderNodes[index];
                Bounds source = node.Vat == null
                    ? node.Mesh.bounds
                    : VatBounds(node.Vat);
                Bounds local = TransformBounds(
                    source,
                    Matrix4x4.TRS(
                        node.LocalPosition,
                        node.LocalRotation,
                        node.LocalScale));
                AddBoothPreviewItem(
                    FindBoothSource(node.Name),
                    node.Name,
                    false,
                    local);
            }

            _boothPreviewBounds = bundle.Bounds;
            _hasBoothPreviewBounds = true;
            _boothPreviewIsExact = true;
            _keepExactPreviewUntil =
                EditorApplication.timeSinceStartup + 12d;
        }

        private UnityEngine.Object FindBoothSource(string relativePath)
        {
            if (_root == null) return null;
            if (string.IsNullOrEmpty(relativePath) ||
                relativePath == _root.name)
                return _root;

            Transform found = _root.transform.Find(relativePath);
            return found == null ? _root : found.gameObject;
        }

        private void AddBoothPreviewItem(
            UnityEngine.Object source,
            string label,
            bool isParticle,
            Bounds localBounds)
        {
            var item = new BoothPreviewItem
            {
                Source = source,
                Label = string.IsNullOrEmpty(label)
                    ? L("(unnamed)")
                    : label,
                IsParticle = isParticle,
                LocalBounds = localBounds,
            };
            _boothPreviewItems.Add(item);

            if (isParticle) return;

            if (!_hasBoothPreviewBounds)
            {
                _boothPreviewBounds = localBounds;
                _hasBoothPreviewBounds = true;
            }
            else
            {
                _boothPreviewBounds.Encapsulate(localBounds.min);
                _boothPreviewBounds.Encapsulate(localBounds.max);
            }
        }

        private void AppendBoothPreviewError(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (!string.IsNullOrEmpty(_boothPreviewError))
                _boothPreviewError += "\n";
            _boothPreviewError += message;
        }

        private Bounds CalculateLiveParticleBounds(
            ParticleSystem source)
        {
            RejectUnsupportedParticleModules(source);
            ParticleSystemRenderer renderer =
                source.GetComponent<ParticleSystemRenderer>();
            if (renderer == null)
                throw new InvalidOperationException(
                    L("Particle renderer is missing."));

            bool billboard =
                renderer.renderMode != ParticleSystemRenderMode.Mesh;
            if (!billboard && renderer.mesh == null)
                throw new InvalidOperationException(
                    L("Particle mesh is missing."));
            if (renderer.renderMode != ParticleSystemRenderMode.Mesh &&
                renderer.renderMode !=
                    ParticleSystemRenderMode.Billboard &&
                renderer.renderMode !=
                    ParticleSystemRenderMode.HorizontalBillboard &&
                renderer.renderMode !=
                    ParticleSystemRenderMode.VerticalBillboard)
                throw new NotSupportedException(
                    L("Use Billboard or Mesh rendering."));

            ParticleSystem.MainModule main = source.main;
            float lifeMin;
            float lifeMax;
            float speedMin;
            float speedMax;
            float sizeMin;
            float sizeMax;
            float gravityMin;
            float gravityMax;
            ReadParticleCurve(
                main.startLifetime,
                L("start lifetime"),
                out lifeMin,
                out lifeMax);
            ReadParticleCurve(
                main.startSpeed,
                L("start speed"),
                out speedMin,
                out speedMax);
            ReadParticleCurve(
                main.startSize,
                L("start size"),
                out sizeMin,
                out sizeMax);
            ReadParticleCurve(
                main.gravityModifier,
                L("gravity"),
                out gravityMin,
                out gravityMax);
            if (Mathf.Abs(lifeMax - lifeMin) > 0.0001f)
                throw new NotSupportedException(
                    L("Lifetime must be Constant."));
            if (Mathf.Abs(gravityMax - gravityMin) > 0.0001f)
                throw new NotSupportedException(
                    L("Gravity must be Constant."));

            int shapeType;
            float shapeRadius;
            float shapeAngle;
            Vector3 shapeScale;
            ReadParticleShape(
                source.shape,
                out shapeType,
                out shapeRadius,
                out shapeAngle,
                out shapeScale);

            Vector3 relativeScale = RelativeScale(source.transform);
            Quaternion relativeRotation =
                Quaternion.Inverse(_root.transform.rotation) *
                source.transform.rotation;
            float meshRadius = billboard
                ? Mathf.Sqrt(0.5f)
                : CalculateMeshRadius(renderer.mesh);

            return CalculateParticleSpawnBounds(
                _root.transform.InverseTransformPoint(
                    source.transform.position),
                relativeRotation * Vector3.forward,
                shapeType,
                shapeRadius * MaxAbs(relativeScale),
                Vector3.Scale(shapeScale, Abs(relativeScale)),
                speedMin,
                speedMax,
                lifeMax,
                gravityMax * Physics.gravity.magnitude,
                meshRadius,
                sizeMax);
        }

        private static Bounds CalculateParticleSpawnBounds(
            Vector3 origin,
            Vector3 direction,
            int shape,
            float shapeRadius,
            Vector3 shapeScale,
            float speedMinimum,
            float speedMaximum,
            float lifetime,
            float gravity,
            float meshRadius,
            float sizeMaximum)
        {
            Vector3 extents =
                Vector3.one * (meshRadius * sizeMaximum);
            if (shape == 1)
                extents += Vector3.one * shapeRadius;
            else if (shape == 3)
                extents += shapeScale * 0.5f;

            return new Bounds(origin, extents * 2f);
        }

        private static float CalculateMeshRadius(Mesh mesh)
        {
            if (mesh == null) return 0f;
            float radiusSquared = 0f;
            if (mesh.isReadable)
            {
                Vector3[] vertices = mesh.vertices;
                for (int index = 0; index < vertices.Length; index++)
                    radiusSquared = Mathf.Max(
                        radiusSquared,
                        vertices[index].sqrMagnitude);
                return Mathf.Sqrt(radiusSquared);
            }

            Bounds bounds = mesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 value = new Vector3(
                    (corner & 1) == 0
                        ? bounds.min.x
                        : bounds.max.x,
                    (corner & 2) == 0
                        ? bounds.min.y
                        : bounds.max.y,
                    (corner & 4) == 0
                        ? bounds.min.z
                        : bounds.max.z);
                radiusSquared = Mathf.Max(
                    radiusSquared,
                    value.sqrMagnitude);
            }
            return Mathf.Sqrt(radiusSquared);
        }

        private static Bounds TransformBounds(
            Bounds source,
            Matrix4x4 matrix)
        {
            bool initialized = false;
            Vector3 minimum = Vector3.zero;
            Vector3 maximum = Vector3.zero;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = new Vector3(
                    (corner & 1) == 0
                        ? source.min.x
                        : source.max.x,
                    (corner & 2) == 0
                        ? source.min.y
                        : source.max.y,
                    (corner & 4) == 0
                        ? source.min.z
                        : source.max.z);
                Vector3 value = matrix.MultiplyPoint3x4(local);
                if (!initialized)
                {
                    minimum = value;
                    maximum = value;
                    initialized = true;
                }
                else
                {
                    minimum = Vector3.Min(minimum, value);
                    maximum = Vector3.Max(maximum, value);
                }
            }

            return new Bounds(
                (minimum + maximum) * 0.5f,
                maximum - minimum);
        }

        private string BuildBoothGuideMessage()
        {
            string legend =
                L("Green = exported booth / Blue = floor / Yellow or red = model and VAT bounds");

            if (_root == null)
                return legend + L("\nSelect the Exhibit Root first.");

            if (!_hasBoothPreviewBounds)
            {
                string emptyMessage =
                    L("\nNo visible models or particles found.");
                if (!string.IsNullOrEmpty(_boothPreviewError))
                    emptyMessage += "\n" + _boothPreviewError;
                return legend + emptyMessage;
            }

            bool fits = BoothBoundsFit(_boothPreviewBounds);
            Vector3 size = _boothPreviewBounds.size;
            string source = _boothPreviewIsExact
                ? L("Exported model / VAT bounds")
                : L("Current model bounds");
            string result = legend + "\n" +
                (fits
                    ? L("OK: ready to export. Automatically centered on the floor when saved.")
                    : L("Too large: reduce the exhibit size. ") +
                      DescribeBoothOverflow(_boothPreviewBounds)) +
                "\n" + source + L(": width ") + size.x.ToString("0.00") +
                L("m / height ") + size.y.ToString("0.00") +
                L("m / depth ") + size.z.ToString("0.00") + "m" +
                L("\nRoot and pivot position do not matter. Particles do not affect floor, centering or dimensions. Out-of-booth particles are hidden during playback.");

            string offenders = BuildBoothOffenderText();
            if (!string.IsNullOrEmpty(offenders))
                result += L("\nOversized objects:\n") + offenders;
            if (!string.IsNullOrEmpty(_boothPreviewError))
                result += L("\nPreview warning:\n") + _boothPreviewError;
            return result;
        }

        private string BuildBoothOffenderText()
        {
            string result = "";
            int shown = 0;
            for (int index = 0;
                 index < _boothPreviewItems.Count;
                 index++)
            {
                BoothPreviewItem item =
                    _boothPreviewItems[index];
                if (item.IsParticle) continue;
                if (BoothBoundsFit(item.LocalBounds)) continue;
                if (shown > 0) result += "\n";
                result += "・" +
                    (item.IsParticle
                        ? L("Particle ")
                        : L("Model ")) +
                    item.Label + L(": ") +
                    DescribeBoothOverflow(item.LocalBounds);
                shown++;
                if (shown >= 4) break;
            }
            return result;
        }

        private static bool BoothBoundsFit(Bounds bounds)
        {
            Vector3 size = bounds.size;
            return
                size.x <= BoothHalfWidth * 2f +
                          BoothTolerance * 2f &&
                size.y <= BoothHeight + BoothTolerance &&
                size.z <= BoothHalfDepth * 2f +
                          BoothTolerance * 2f;
        }

        private static string DescribeBoothOverflow(Bounds bounds)
        {
            var dimensions = new List<string>();
            Vector3 size = bounds.size;
            float width = BoothHalfWidth * 2f;
            float depth = BoothHalfDepth * 2f;

            if (size.x > width + BoothTolerance * 2f)
                dimensions.Add(
                    L("Width ") + size.x.ToString("0.00") +
                    L("m / limit ") + width.ToString("0.00") +
                    L("m (") + (size.x - width).ToString("0.00") +
                    L("m reduction needed)"));
            if (size.y > BoothHeight + BoothTolerance)
                dimensions.Add(
                    L("Height ") + size.y.ToString("0.00") +
                    L("m / limit ") + BoothHeight.ToString("0.00") +
                    L("m (") + (size.y - BoothHeight).ToString("0.00") +
                    L("m reduction needed)"));
            if (size.z > depth + BoothTolerance * 2f)
                dimensions.Add(
                    L("Depth ") + size.z.ToString("0.00") +
                    L("m / limit ") + depth.ToString("0.00") +
                    L("m (") + (size.z - depth).ToString("0.00") +
                    L("m reduction needed)"));

            return dimensions.Count == 0
                ? L("Fits inside the booth.")
                : string.Join(" / ", dimensions.ToArray());
        }

        private Vector3 BoothPreviewFloorCenter()
        {
            if (!_hasBoothPreviewBounds) return Vector3.zero;
            return new Vector3(
                _boothPreviewBounds.center.x,
                _boothPreviewBounds.min.y,
                _boothPreviewBounds.center.z);
        }

        private bool HasBoothOffender()
        {
            for (int index = 0;
                 index < _boothPreviewItems.Count;
                 index++)
            {
                BoothPreviewItem item =
                    _boothPreviewItems[index];
                if (!item.IsParticle &&
                    !BoothBoundsFit(item.LocalBounds))
                    return true;
            }
            return _hasBoothPreviewBounds &&
                   !BoothBoundsFit(_boothPreviewBounds);
        }

        private void FocusBoothInScene()
        {
            if (_root == null) return;
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null) return;

            Vector3 boothOrigin = BoothPreviewFloorCenter();
            Bounds localBooth = new Bounds(
                boothOrigin + new Vector3(0f, BoothHeight * 0.5f, 0f),
                new Vector3(
                    BoothHalfWidth * 2f,
                    BoothHeight,
                    BoothHalfDepth * 2f));
            Bounds worldBooth = TransformBounds(
                localBooth,
                _root.transform.localToWorldMatrix);
            view.Frame(worldBooth, false);
            view.Repaint();
        }

        private void SelectFirstBoothOffender()
        {
            if (_root == null) return;
            for (int index = 0;
                 index < _boothPreviewItems.Count;
                 index++)
            {
                BoothPreviewItem item =
                    _boothPreviewItems[index];
                if (item.IsParticle) continue;
                if (BoothBoundsFit(item.LocalBounds)) continue;

                GameObject selected =
                    item.Source as GameObject;
                Component component =
                    item.Source as Component;
                if (selected == null && component != null)
                    selected = component.gameObject;
                if (selected != null)
                {
                    Selection.activeGameObject = selected;
                    EditorGUIUtility.PingObject(selected);
                }

                SceneView view =
                    SceneView.lastActiveSceneView;
                if (view != null)
                {
                    Bounds world = TransformBounds(
                        item.LocalBounds,
                        _root.transform.localToWorldMatrix);
                    view.Frame(world, false);
                    view.Repaint();
                }
                return;
            }
        }

        private void DrawBoothSceneGuide(SceneView sceneView)
        {
            if (!_showBoothGuide || _root == null) return;

            Matrix4x4 oldMatrix = Handles.matrix;
            try
            {
                Matrix4x4 rootMatrix =
                    _root.transform.localToWorldMatrix;
                Vector3 boothOrigin =
                    BoothPreviewFloorCenter();
                Handles.matrix = rootMatrix *
                    Matrix4x4.Translate(boothOrigin);
                DrawBoothFloor();
                Handles.color =
                    new Color(0.15f, 1f, 0.3f, 0.95f);
                Handles.DrawWireCube(
                    new Vector3(
                        0f,
                        BoothHeight * 0.5f,
                        0f),
                    new Vector3(
                        BoothHalfWidth * 2f,
                        BoothHeight,
                        BoothHalfDepth * 2f));

                Handles.matrix = rootMatrix;
                if (_hasBoothPreviewBounds)
                {
                    Handles.color =
                        BoothBoundsFit(_boothPreviewBounds)
                            ? new Color(
                                1f, 0.75f, 0.05f, 0.95f)
                            : new Color(
                                1f, 0.08f, 0.08f, 1f);
                    Handles.DrawWireCube(
                        _boothPreviewBounds.center,
                        _boothPreviewBounds.size);
                }

                Handles.color =
                    new Color(1f, 0.05f, 0.7f, 1f);
                for (int index = 0;
                     index < _boothPreviewItems.Count;
                     index++)
                {
                    BoothPreviewItem item =
                        _boothPreviewItems[index];
                    if (item.IsParticle) continue;
                    if (BoothBoundsFit(item.LocalBounds))
                        continue;
                    Handles.DrawWireCube(
                        item.LocalBounds.center,
                        item.LocalBounds.size);
                }
            }
            finally
            {
                Handles.matrix = oldMatrix;
            }

            DrawBoothLabels();
            DrawBoothSceneOverlay();
        }

        private void DrawBoothFloor()
        {
            Vector3[] floor =
            {
                new Vector3(
                    -BoothHalfWidth, 0f, -BoothHalfDepth),
                new Vector3(
                    -BoothHalfWidth, 0f, BoothHalfDepth),
                new Vector3(
                    BoothHalfWidth, 0f, BoothHalfDepth),
                new Vector3(
                    BoothHalfWidth, 0f, -BoothHalfDepth),
            };
            Handles.DrawSolidRectangleWithOutline(
                floor,
                new Color(0.05f, 0.45f, 1f, 0.10f),
                new Color(0.1f, 0.75f, 1f, 0.95f));

            Handles.color =
                new Color(0.1f, 0.65f, 1f, 0.45f);
            for (float value = -1f;
                 value <= 1.001f;
                 value += 0.5f)
            {
                Handles.DrawLine(
                    new Vector3(
                        value, 0f, -BoothHalfDepth),
                    new Vector3(
                        value, 0f, BoothHalfDepth));
                Handles.DrawLine(
                    new Vector3(
                        -BoothHalfWidth, 0f, value),
                    new Vector3(
                        BoothHalfWidth, 0f, value));
            }

            Handles.color =
                new Color(0.05f, 1f, 1f, 1f);
            Handles.DrawAAPolyLine(
                5f,
                new Vector3(
                    -BoothHalfWidth, 0.002f,
                    BoothHalfDepth),
                new Vector3(
                    BoothHalfWidth, 0.002f,
                    BoothHalfDepth));
        }

        private void DrawBoothLabels()
        {
            GUIStyle style =
                new GUIStyle(EditorStyles.whiteBoldLabel);
            style.normal.textColor = Color.white;
            style.fontSize = 12;

            Transform rootTransform = _root.transform;
            Vector3 boothOrigin = BoothPreviewFloorCenter();
            Handles.Label(
                rootTransform.TransformPoint(
                    boothOrigin + new Vector3(
                        -BoothHalfWidth,
                        0.03f,
                        -BoothHalfDepth)),
                L("Floor Y=0"),
                style);
            Handles.Label(
                rootTransform.TransformPoint(
                    boothOrigin + new Vector3(
                        -BoothHalfWidth,
                        BoothHeight,
                        -BoothHalfDepth)),
                L("Ceiling Y=2.7m"),
                style);
            Handles.Label(
                rootTransform.TransformPoint(
                    boothOrigin + new Vector3(
                        0f,
                        0.03f,
                        BoothHalfDepth + 0.16f)),
                L("Front +Z"),
                style);
        }

        private void DrawBoothSceneOverlay()
        {
            Handles.BeginGUI();
            GUILayout.BeginArea(
                new Rect(12f, 12f, 510f, 190f),
                GUI.skin.box);
            GUILayout.Label(
                L("RAC2 exported placement: 3m x 3m x 2.7m"),
                EditorStyles.boldLabel);
            GUILayout.Label(
                L("Independent of root/pivot. Centered on the floor at export."),
                EditorStyles.wordWrappedMiniLabel);

            if (_hasBoothPreviewBounds)
            {
                bool fits = BoothBoundsFit(_boothPreviewBounds);
                Vector3 size = _boothPreviewBounds.size;
                GUIStyle resultStyle =
                    new GUIStyle(EditorStyles.wordWrappedLabel);
                resultStyle.normal.textColor = fits
                    ? new Color(0.25f, 0.85f, 0.35f)
                    : new Color(1f, 0.25f, 0.25f);
                GUILayout.Label(
                    (fits
                        ? L("OK: ready to export")
                        : L("Too large: ") + DescribeBoothOverflow(
                            _boothPreviewBounds)) +
                    L("\nWidth ") + size.x.ToString("0.00") +
                    L("m / height ") + size.y.ToString("0.00") +
                    L("m / depth ") + size.z.ToString("0.00") + "m",
                    resultStyle);
                GUILayout.Label(
                    L("Particles do not affect placement or dimensions. Out-of-booth particles are hidden during playback."),
                    EditorStyles.wordWrappedMiniLabel);
            }
            else
            {
                GUILayout.Label(
                    L("Place models and particles under the Exhibit Root."),
                    EditorStyles.wordWrappedLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L("Frame exported booth")))
                    FocusBoothInScene();
                using (new EditorGUI.DisabledScope(
                    !HasBoothOffender()))
                {
                    if (GUILayout.Button(L("Select oversized object")))
                        SelectFirstBoothOffender();
                }
            }

            GUILayout.EndArea();
            Handles.EndGUI();
        }
    }
}
