using System;
using System.Collections.Generic;
using UnityEngine;

namespace AvatarCatalog.Remote
{
    public enum Rac1BoothMessageSeverity
    {
        Info,
        Warning,
        Error
    }

    public enum Rac1BoothValidationFixKind
    {
        None,
        EnableRepeatUvBake
    }

    [Serializable]
    public sealed class Rac1BoothValidationMessage
    {
        public Rac1BoothMessageSeverity Severity;
        public string Message;
        public UnityEngine.Object Context;
        public Rac1BoothValidationFixKind FixKind;

        public Rac1BoothValidationMessage(
            Rac1BoothMessageSeverity severity,
            string message,
            UnityEngine.Object context = null,
            Rac1BoothValidationFixKind fixKind = Rac1BoothValidationFixKind.None)
        {
            Severity = severity;
            Message = message;
            Context = context;
            FixKind = fixKind;
        }
    }

    /// <summary>
    /// Result shown by the Booth Authoring inspector. This is deliberately an
    /// editor-only report: only the generated RAC1 bytes are uploaded.
    /// </summary>
    public sealed class Rac1BoothValidationReport
    {
        public readonly List<Rac1BoothValidationMessage> Messages =
            new List<Rac1BoothValidationMessage>();

        public int SourceRendererCount;
        public int MaterialCount;
        public int VertexCount;
        public int IndexCount;
        public long EstimatedFileSize;
        public Bounds ContentBounds;

        public bool HasErrors
        {
            get
            {
                for (int i = 0; i < Messages.Count; i++)
                {
                    if (Messages[i].Severity == Rac1BoothMessageSeverity.Error)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public int ErrorCount => Count(Rac1BoothMessageSeverity.Error);
        public int WarningCount => Count(Rac1BoothMessageSeverity.Warning);

        public void Add(
            Rac1BoothMessageSeverity severity,
            string message,
            UnityEngine.Object context = null,
            Rac1BoothValidationFixKind fixKind = Rac1BoothValidationFixKind.None)
        {
            Messages.Add(new Rac1BoothValidationMessage(severity, message, context, fixKind));
        }

        private int Count(Rac1BoothMessageSeverity severity)
        {
            int count = 0;
            for (int i = 0; i < Messages.Count; i++)
            {
                if (Messages[i].Severity == severity)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Editor-only marker placed on the floor-centre of a 3 x 3 x 2.7 metre
    /// mall booth. AvatarRoot and ShopVisualRoot are flattened into one RAC1
    /// static snapshot in this component's local coordinate system.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Avatar Catalog/RAC1 Booth Authoring")]
    public sealed class Rac1BoothAuthoring : MonoBehaviour
    {
        public const string ProfileId = "Standard-3x3-v1";
        public const float ProfileWidth = 3f;
        public const float ProfileDepth = 3f;
        public const float ProfileHeight = 2.7f;

#if UNITY_EDITOR
        internal const float FloorGuideYOffset = 0.002f;
        internal const string FloorGuideObjectName = "__RAC1 Booth Floor Guide";
#endif

        [Tooltip("The avatar hierarchy whose current pose will be baked.")]
        public Transform AvatarRoot;

        [Tooltip("Optional, visual-only shop decoration hierarchy. Scripts and physics are never exported.")]
        public Transform ShopVisualRoot;

        [SerializeField]
        [Tooltip("Shows the decoded-equivalent static capture on top of the source hierarchy.")]
        private bool showCapturedPreview = true;

        [SerializeField]
        [Tooltip("Shows a translucent 3 x 3 metre floor guide in the Unity Editor. The guide is never saved or exported.")]
        private bool showFloorGuide = true;

        [SerializeField, HideInInspector]
        [Tooltip("When enabled by a validation Fix, finite Repeat UV domains are baked into temporary atlas tiles without changing source assets.")]
        private bool bakeRepeatUvDomains;

        [NonSerialized] private Mesh capturedMesh;
        [NonSerialized] private Texture2D capturedAtlas;
        [NonSerialized] private Material previewMaterial;
        [NonSerialized] private GameObject previewObject;
        [NonSerialized] private Rac1BoothValidationReport lastReport;

#if UNITY_EDITOR
        [NonSerialized] private GameObject floorGuideObject;
        [NonSerialized] private Mesh floorGuideMesh;
        [NonSerialized] private Material floorGuideMaterial;
#endif

        public static Vector3 ProfileSize =>
            new Vector3(ProfileWidth, ProfileHeight, ProfileDepth);

        public static Bounds ProfileBounds =>
            new Bounds(new Vector3(0f, ProfileHeight * 0.5f, 0f), ProfileSize);

        public bool ShowCapturedPreview
        {
            get => showCapturedPreview;
            set
            {
                showCapturedPreview = value;
                UpdatePreviewState();
            }
        }

        public bool ShowFloorGuide
        {
            get => showFloorGuide;
            set
            {
                showFloorGuide = value;
#if UNITY_EDITOR
                UpdateFloorGuideState();
#endif
            }
        }

        public bool HasCapture => capturedMesh != null && capturedAtlas != null;
        public Mesh CapturedMesh => capturedMesh;
        public Texture2D CapturedAtlas => capturedAtlas;
        public Rac1BoothValidationReport LastReport => lastReport;

        internal GameObject PreviewObject => previewObject;

        internal bool BakeRepeatUvDomains
        {
            get => bakeRepeatUvDomains;
            set => bakeRepeatUvDomains = value;
        }

#if UNITY_EDITOR
        internal GameObject FloorGuideObject => floorGuideObject;
        internal Mesh FloorGuideMesh => floorGuideMesh;
#endif

        internal void SetLastReport(Rac1BoothValidationReport report)
        {
            lastReport = report;
        }

        internal void ReplaceCapture(
            Mesh mesh,
            Texture2D atlas,
            Material material,
            Rac1BoothValidationReport report)
        {
            ReleaseCapture();

            capturedMesh = mesh;
            capturedAtlas = atlas;
            previewMaterial = material;
            lastReport = report;

            if (capturedMesh == null || capturedAtlas == null || previewMaterial == null)
            {
                return;
            }

            previewObject = new GameObject("__RAC1 Booth Captured Preview");
            previewObject.hideFlags = HideFlags.HideAndDontSave;
            previewObject.transform.SetParent(transform, false);
            MeshFilter filter = previewObject.AddComponent<MeshFilter>();
            MeshRenderer renderer = previewObject.AddComponent<MeshRenderer>();
            filter.sharedMesh = capturedMesh;
            renderer.sharedMaterial = previewMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.receiveShadows = true;
            UpdatePreviewState();
        }

        public void ReleaseCapture()
        {
            DestroyEditorObject(previewObject);
            DestroyEditorObject(previewMaterial);
            DestroyEditorObject(capturedMesh);
            DestroyEditorObject(capturedAtlas);

            previewObject = null;
            previewMaterial = null;
            capturedMesh = null;
            capturedAtlas = null;
        }

        private void OnEnable()
        {
#if UNITY_EDITOR
            UpdateFloorGuideState();
#endif
        }

        private void OnValidate()
        {
            UpdatePreviewState();
#if UNITY_EDITOR
            UpdateFloorGuideState();
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            ReleaseFloorGuide();
#endif
        }

        private void OnDestroy()
        {
            ReleaseCapture();
#if UNITY_EDITOR
            ReleaseFloorGuide();
#endif
        }

        private void UpdatePreviewState()
        {
            if (previewObject == null)
            {
                return;
            }

            Transform previewTransform = previewObject.transform;
            if (previewTransform.parent != transform)
            {
                previewTransform.SetParent(transform, false);
            }

            previewTransform.localPosition = Vector3.zero;
            previewTransform.localRotation = Quaternion.identity;
            previewTransform.localScale = Vector3.one;

            MeshRenderer renderer = previewObject.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.enabled = showCapturedPreview;
            }
        }

#if UNITY_EDITOR
        internal bool IsGeneratedHelper(Renderer renderer)
        {
            if (renderer == null)
            {
                return false;
            }

            Transform rendererTransform = renderer.transform;
            return IsChildOfHelper(rendererTransform, previewObject) ||
                   IsChildOfHelper(rendererTransform, floorGuideObject);
        }

        internal void RefreshFloorGuide()
        {
            UpdateFloorGuideState();
        }

        private void UpdateFloorGuideState()
        {
            if (Application.isPlaying || !isActiveAndEnabled)
            {
                ReleaseFloorGuide();
                return;
            }

            if (floorGuideObject == null)
            {
                CreateFloorGuide();
            }

            if (floorGuideObject == null)
            {
                return;
            }

            Transform guideTransform = floorGuideObject.transform;
            if (guideTransform.parent != transform)
            {
                guideTransform.SetParent(transform, false);
            }

            guideTransform.localPosition = Vector3.zero;
            guideTransform.localRotation = Quaternion.identity;
            guideTransform.localScale = Vector3.one;

            MeshRenderer renderer = floorGuideObject.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.enabled = showFloorGuide;
            }
        }

        private void CreateFloorGuide()
        {
            ReleaseFloorGuide();

            Shader shader = Shader.Find("Hidden/Avatar Catalog/RAC1 Booth Floor Guide");
            if (shader == null)
            {
                return;
            }

            floorGuideMesh = CreateFloorGuideMesh();
            floorGuideMaterial = new Material(shader)
            {
                name = "RAC1 Booth Floor Guide Material",
                hideFlags = HideFlags.HideAndDontSave
            };
            if (floorGuideMaterial.HasProperty("_Color"))
            {
                floorGuideMaterial.SetColor("_Color", new Color(0.08f, 0.7f, 1f, 0.14f));
            }

            floorGuideObject = new GameObject(FloorGuideObjectName)
            {
                hideFlags = HideFlags.HideAndDontSave,
                tag = "EditorOnly"
            };
            floorGuideObject.transform.SetParent(transform, false);

            MeshFilter filter = floorGuideObject.AddComponent<MeshFilter>();
            MeshRenderer renderer = floorGuideObject.AddComponent<MeshRenderer>();
            filter.sharedMesh = floorGuideMesh;
            renderer.sharedMaterial = floorGuideMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        }

        private static Mesh CreateFloorGuideMesh()
        {
            float halfWidth = ProfileWidth * 0.5f;
            float halfDepth = ProfileDepth * 0.5f;
            Mesh mesh = new Mesh
            {
                name = "RAC1 Booth Floor Guide Mesh",
                hideFlags = HideFlags.HideAndDontSave
            };
            mesh.vertices = new[]
            {
                new Vector3(-halfWidth, FloorGuideYOffset, -halfDepth),
                new Vector3(-halfWidth, FloorGuideYOffset, halfDepth),
                new Vector3(halfWidth, FloorGuideYOffset, halfDepth),
                new Vector3(halfWidth, FloorGuideYOffset, -halfDepth)
            };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private void ReleaseFloorGuide()
        {
            DestroyEditorObject(floorGuideObject);
            DestroyEditorObject(floorGuideMaterial);
            DestroyEditorObject(floorGuideMesh);
            floorGuideObject = null;
            floorGuideMaterial = null;
            floorGuideMesh = null;
        }

        private static bool IsChildOfHelper(Transform candidate, GameObject helper)
        {
            return candidate != null && helper != null &&
                   (candidate == helper.transform || candidate.IsChildOf(helper.transform));
        }
#endif

        private void OnDrawGizmosSelected()
        {
            Matrix4x4 previousMatrix = Gizmos.matrix;
            Color previousColor = Gizmos.color;
            Gizmos.matrix = transform.localToWorldMatrix;

            Gizmos.color = new Color(0.2f, 1f, 0.45f, 0.95f);
            Gizmos.DrawWireCube(ProfileBounds.center, ProfileBounds.size);

            Gizmos.color = new Color(0.1f, 0.75f, 1f, 0.9f);
            Gizmos.DrawLine(new Vector3(-ProfileWidth * 0.5f, 0f, -ProfileDepth * 0.5f),
                new Vector3(ProfileWidth * 0.5f, 0f, -ProfileDepth * 0.5f));
            Gizmos.DrawLine(new Vector3(ProfileWidth * 0.5f, 0f, -ProfileDepth * 0.5f),
                new Vector3(ProfileWidth * 0.5f, 0f, ProfileDepth * 0.5f));
            Gizmos.DrawLine(new Vector3(ProfileWidth * 0.5f, 0f, ProfileDepth * 0.5f),
                new Vector3(-ProfileWidth * 0.5f, 0f, ProfileDepth * 0.5f));
            Gizmos.DrawLine(new Vector3(-ProfileWidth * 0.5f, 0f, ProfileDepth * 0.5f),
                new Vector3(-ProfileWidth * 0.5f, 0f, -ProfileDepth * 0.5f));

            // +Z is the customer-facing direction.
            Gizmos.color = new Color(1f, 0.75f, 0.15f, 1f);
            Vector3 arrowStart = new Vector3(0f, 0.04f, ProfileDepth * 0.5f);
            Vector3 arrowEnd = arrowStart + Vector3.forward * 0.45f;
            Gizmos.DrawLine(arrowStart, arrowEnd);
            Gizmos.DrawLine(arrowEnd, arrowEnd + new Vector3(-0.12f, 0f, -0.14f));
            Gizmos.DrawLine(arrowEnd, arrowEnd + new Vector3(0.12f, 0f, -0.14f));

            Gizmos.matrix = previousMatrix;
            Gizmos.color = previousColor;
        }

        private static void DestroyEditorObject(UnityEngine.Object value)
        {
            if (value == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(value);
            }
            else
            {
                DestroyImmediate(value);
            }
        }
    }
}

