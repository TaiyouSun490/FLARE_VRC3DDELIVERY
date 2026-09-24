using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AvatarCatalog.Remote
{
    /// <summary>Single public authoring workflow for static, VAT, particle, and product data.</summary>
    public sealed partial class Rac2CreatorWindow : EditorWindow
    {
        private enum BlendProfile { Alpha, Additive }

        [SerializeField] private GameObject _root;
        [SerializeField] private AnimationClip _clip;
        [SerializeField] private int _framesPerSecond = 30;
        [SerializeField] private bool _loop = true;
        [SerializeField] private bool _includeVatNormals = true;
        [SerializeField] private bool _includePhysBones;
        [SerializeField] private float _physBoneWarmup = 1f;
        [SerializeField] private float _physBoneSeam = .15f;
        private bool _showConstraintSettings;
        private int _constraintMaxPasses = 32;
        private BlendProfile _particleBlend = BlendProfile.Alpha;
        private bool _hasCollider = true;
        private bool _portable;
        private bool _includeProduct;
        private string _productName = "";
        private string _creatorName = "";
        private string _productUrl = "";
        private string _avatarBlueprintId = "";
        private bool _trialEnabled;
        private bool _compress;
        private bool _shareTextures = true;
        private Vector2 _scroll;
        private string _scanSummary = "";
        private Rac2EditorBakeRunner _bakeRunner;

        [MenuItem("Tools/FLARE/RAC2 Creator...", priority = 1)]
        private static void Open()
        {
            Rac2CreatorWindow window = GetWindow<Rac2CreatorWindow>("FLARE RAC2 Creator");
            window.minSize = new Vector2(520f, 540f);
            window.UseSelection();
        }

        [MenuItem("GameObject/FLARE/Create RAC2 from this object...", false, 20)]
        private static void OpenFromGameObject()
        {
            Open();
        }

        private void OnSelectionChange()
        {
            if (_root == null) UseSelection();
        }

        private void OnGUI()
        {
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            try
            {
                EditorGUIUtility.labelWidth = Mathf.Clamp(position.width * .5f, 240f, 320f);
                DrawCreatorGui();
            }
            finally { EditorGUIUtility.labelWidth = previousLabelWidth; }
        }

        private void DrawCreatorGui()
        {
            if (FlareLocalization.DrawLanguage() && _bakeRunner == null) RefreshSummary();
            if (GUILayout.Button(L("User guide"))) FlareLocalization.OpenGuide();
            if (_bakeRunner != null)
            {
                EditorGUILayout.HelpBox(L("Exporting VAT with clothing constraints. The original object is not modified."), MessageType.Info);
                if (GUILayout.Button(L("Cancel export"))) CancelBake();
                return;
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("FLARE RAC2 Creator", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                L("Choose one root. All supported child renderers and Particle Systems are packed into one RAC2. ") +
                L("If an Animation Clip is supplied, every child SkinnedMeshRenderer is baked to VAT automatically."),
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            _root = (GameObject)EditorGUILayout.ObjectField(L("Exhibit Root"), _root, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) RefreshSummary();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L("Use Selection"))) UseSelection();
                if (GUILayout.Button(L("Refresh Scan"))) RefreshSummary();
            }
            EditorGUILayout.Space(4f);
            EditorGUILayout.HelpBox(_scanSummary, _root == null ? MessageType.Warning : MessageType.None);
            DrawPerformanceRating();
            EditorGUILayout.HelpBox(
                L("Recommended: before exporting, use Mesh Baker or similar tools to combine meshes and materials where practical. ") +
                L("Remove unused vertices and submeshes. RAC2 restores meshes sequentially, ") +
                L("but the cost of applying a single large mesh cannot be fully split across frames."),
                MessageType.Info);

            DrawBoothGuideControls();

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(L("Animation (optional)"), EditorStyles.boldLabel);
            if (Rac2AvatarPreprocessor.HasModularAvatar(_root))
                EditorGUILayout.HelpBox(L("Clothing: automatic MA/NDMF processing. Armatures are merged on an export copy before VAT baking. The original avatar is not modified."), MessageType.Info);
            _clip = (AnimationClip)EditorGUILayout.ObjectField(L("Animation Clip"), _clip, typeof(AnimationClip), false);
            using (new EditorGUI.DisabledScope(_clip == null))
            {
                _framesPerSecond = EditorGUILayout.IntSlider(L("VAT FPS"), _framesPerSecond, 1, 60);
                _loop = EditorGUILayout.Toggle(L("Loop"), _loop);
                _includeVatNormals = EditorGUILayout.Toggle(L("Bake VAT Normals"), _includeVatNormals);
                _includePhysBones = EditorGUILayout.Toggle(L("Include PhysBones"), _includePhysBones);
                if (_includePhysBones)
                {
                    _physBoneWarmup = EditorGUILayout.Slider(L("Warm-up (seconds)"), _physBoneWarmup, 0, 10);
                    using (new EditorGUI.DisabledScope(!_loop))
                        _physBoneSeam = EditorGUILayout.Slider(L("Loop physics blend (seconds)"), _physBoneSeam, 0, .5f);
                    EditorGUILayout.HelpBox("SDK " + Rac2PhysBoneBakeSession.SupportedSdk + L(" supported. PhysBone motion and colliders inside the root are recorded in VAT.")
                        + L("Export takes longer. Live grabbing and touch interaction after loading are not included.")
                        + L("The end blend only closes the physics offset. It does not fix pose or root-motion discontinuities in the clip."), MessageType.Info);
                    if (Rac2PhysBoneBakeSession.Count(_root) == 0)
                        EditorGUILayout.HelpBox(L("No active PhysBones found; standard VAT will be created."), MessageType.Warning);
                }
                _showConstraintSettings = EditorGUILayout.Foldout(_showConstraintSettings, L("Clothing constraint settings"));
                if (_showConstraintSettings)
                    _constraintMaxPasses = EditorGUILayout.IntSlider(new GUIContent(L("Maximum evaluation passes"), L("Maximum native constraint evaluations while waiting for a stable pose per frame.")), _constraintMaxPasses, 2, 120);
            }
            if (_clip == null)
                EditorGUILayout.HelpBox(L("Saved as a static pose: no VAT or PhysBone motion. Select an Animation Clip to record walking or secondary motion."), MessageType.Warning);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(L("Particles"), EditorStyles.boldLabel);
            _particleBlend = (BlendProfile)EditorGUILayout.Popup(L("Blend Profile"), (int)_particleBlend, new[] { L("Alpha"), L("Additive") });
            EditorGUILayout.HelpBox(
                L("Up to 4 child emitters. Point/Sphere/Cone/Box, Alpha/Additive, Billboard/Mesh, ") +
                L("gravity, color fade, rotation, and flipbook are supported."),
                MessageType.None);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(L("Catalog information (optional)"), EditorStyles.boldLabel);
            _includeProduct = EditorGUILayout.Toggle(L("Include Catalog Info"), _includeProduct);
            using (new EditorGUI.DisabledScope(!_includeProduct))
            {
                _productName = EditorGUILayout.TextField(L("Product Name"), _productName);
                _creatorName = EditorGUILayout.TextField(L("Creator Name"), _creatorName);
                _productUrl = EditorGUILayout.TextField(L("Product HTTPS URL"), _productUrl);
                _avatarBlueprintId = EditorGUILayout.TextField(L("Avatar Blueprint ID"), _avatarBlueprintId);
                _trialEnabled = EditorGUILayout.Toggle(L("Trial Enabled"), _trialEnabled);
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(L("Interaction"), EditorStyles.boldLabel);
            _hasCollider = EditorGUILayout.Toggle(L("Add Box Collider"), _hasCollider);
            _portable = EditorGUILayout.Toggle(L("Portable / VRC Pickup"), _portable);
            if (_portable) _hasCollider = true;
            EditorGUILayout.HelpBox(
                L("Portable is stored in the RAC2 metadata. Every load resets the exhibit to the ImagePad's initial position."),
                MessageType.None);

            EditorGUILayout.Space(8f);
            _compress = EditorGUILayout.Toggle(
                L("Smaller file (slower load)"), _compress);
            EditorGUILayout.HelpBox(
                _compress
                    ? L("LZ4 reduces CDN traffic, but large VAT files take much longer to open in VRChat.")
                    : L("Fast loading (recommended). The RAC2 file is larger on the CDN."),
                MessageType.None);
            int frameCount = CalculateFrameCount();
            _shareTextures = EditorGUILayout.Toggle(L("Share identical textures"), _shareTextures);
            if (_shareTextures)
                EditorGUILayout.HelpBox(L("Requires RAC2 runtime 0.2.4 or later. Turn off for older worlds."), MessageType.Info);
            if (_clip != null)
                EditorGUILayout.LabelField(L("VAT Frames"), frameCount.ToString());

            EditorGUILayout.Space(10f);
            using (new EditorGUI.DisabledScope(_root == null || _clip != null && (frameCount < 2 || frameCount > 240)))
            {
                if (GUILayout.Button(L("Create RAC2..."), GUILayout.Height(44f)))
                    CreateRac2(frameCount);
            }
            EditorGUILayout.EndScrollView();
        }

        private int CalculateFrameCount()
        {
            if (_clip == null) return 0;
            return Mathf.Max(2, _loop
                ? Mathf.CeilToInt(_clip.length * _framesPerSecond)
                : Mathf.CeilToInt(_clip.length * _framesPerSecond) + 1);
        }

        private void UseSelection()
        {
            if (Selection.activeGameObject != null) _root = Selection.activeGameObject;
            LoadProductFields();
            RefreshSummary();
            Repaint();
        }

        private void LoadProductFields()
        {
            Rac2ProductMetadata metadata = _root == null
                ? null
                : _root.GetComponent<Rac2ProductMetadata>();
            if (metadata == null && _root != null)
                metadata = _root.GetComponentInParent<Rac2ProductMetadata>();
            if (metadata != null)
            {
                _includeProduct = metadata.IncludeProductMetadata;
                _productName = metadata.ProductName;
                _creatorName = metadata.CreatorName;
                _productUrl = metadata.ProductUrl;
                _avatarBlueprintId = metadata.AvatarBlueprintId;
                _trialEnabled = metadata.TrialEnabled;
                return;
            }
            Rac2ProductMetadataUtility.LoadDefaults(
                out _includeProduct, out _productName, out _creatorName,
                out _productUrl, out _avatarBlueprintId, out _trialEnabled);
        }

        private void RefreshSummary()
        {
            if (_root == null)
            {
                _scanSummary = L("Select the root GameObject of the exhibit.");
                ClearBoothGuide();
                return;
            }
            MeshRenderer[] staticRenderers = _root.GetComponentsInChildren<MeshRenderer>(true);
            SkinnedMeshRenderer[] skinned = _root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            ParticleSystem[] particles = _root.GetComponentsInChildren<ParticleSystem>(true);
            int validStatic = 0;
            int materials = 0;
            for (int index = 0; index < staticRenderers.Length; index++)
            {
                MeshFilter filter = staticRenderers[index].GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                if (staticRenderers[index].GetComponentInParent<ParticleSystem>() != null) continue;
                validStatic++;
                materials += filter.sharedMesh.subMeshCount;
            }
            for (int index = 0; index < skinned.Length; index++)
                if (skinned[index].sharedMesh != null) materials += skinned[index].sharedMesh.subMeshCount;
            _scanSummary =
                validStatic + L(" static renderer(s) + ") + skinned.Length + L(" skinned renderer(s) + ") +
                particles.Length + L(" particle emitter(s)\n") +
                materials + L(" material slot(s). Limits: 16 renderers, 64 materials, 4 emitters.");
            RefreshBoothGuide(true);
        }

        private void CreateRac2(int frameCount)
        {
            if (_bakeRunner != null) return;
            if (AnimationMode.InAnimationMode() || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("RAC2", L("Stop Play Mode and Animation Preview before exporting."), "OK");
                return;
            }
            try
            {
                Rac2AvatarPreprocessor.ValidateSource(_root);
                if (_clip && _includePhysBones && Rac2PhysBoneBakeSession.Count(_root) > 0)
                {
                    Rac2PhysBoneBakeSession.ValidateEnvironment();
                    Rac2VatFrameBaker.ValidateClip(_clip);
                }
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                ShowExportError(L("RAC2: clothing setup required"), error);
                return;
            }
            string output = EditorUtility.SaveFilePanel(L("Create RAC2"), "", _root.name, "rac2");
            if (string.IsNullOrEmpty(output)) return;

            _bakeRunner = new Rac2EditorBakeRunner(CreateRac2Steps(frameCount, output), error =>
            {
                _bakeRunner = null;
                Repaint();
                if (error == null || error is OperationCanceledException) return;
                Debug.LogException(error);
                ShowExportError(L("RAC2 could not be created"), error);
            });
        }

        private void CancelBake()
        {
            var runner = _bakeRunner;
            _bakeRunner = null;
            runner?.Dispose();
            Repaint();
        }

        private IEnumerator CreateRac2Steps(int frameCount, string output)
        {
            GameObject source = _root;
            AnimationClip sourceClip = _clip;
            GameObject clone = null;
            Scene preview = default(Scene);
            Rac2AvatarPreprocessor avatarPreparation = null;

            var temporaryMeshes = new List<Mesh>();
            bool animationModeStarted = false;
            try
            {
                preview = EditorSceneManager.NewPreviewScene();
                clone = Instantiate(source);
                SceneManager.MoveGameObjectToScene(clone, preview);
                clone.hideFlags = HideFlags.HideAndDontSave;
                _root = clone;
                // Bone references and animation bindings must be assembled before controllers
                // are removed, renderers enumerated, or any VAT frame is sampled.
                avatarPreparation = Rac2AvatarPreprocessor.Prepare(clone, source, sourceClip);
                _clip = avatarPreparation.Clip;
                if (_clip != null)
                {
                    foreach (var animator in clone.GetComponentsInChildren<Animator>(true))
                    {
                        animator.runtimeAnimatorController = null;
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    }
                }
                var bundle = new Rac2BinaryExporter.BundleData
                {
                    Interaction = new Rac2BinaryExporter.InteractionData
                    {
                        HasCollider = _hasCollider,
                        IsPortable = _portable,
                    },
                    Product = _includeProduct
                        ? new Rac2BinaryExporter.ProductData
                        {
                            ProductName = _productName ?? "",
                            CreatorName = _creatorName ?? "",
                            ProductUrl = _productUrl ?? "",
                            AvatarBlueprintId = _avatarBlueprintId ?? "",
                            TrialEnabled = _trialEnabled,
                        }
                        : null,
                };

                if (_clip != null)
                {
                    AnimationMode.StartAnimationMode();
                    animationModeStarted = true;
                }
                CollectStaticRenderers(bundle, temporaryMeshes);
                var skinnedSteps = CollectSkinnedRenderersSteps(bundle, temporaryMeshes, frameCount);
                try { while (skinnedSteps.MoveNext()) yield return skinnedSteps.Current; }
                finally { (skinnedSteps as IDisposable)?.Dispose(); }
                if (animationModeStarted)
                {
                    AnimationMode.StopAnimationMode();
                    animationModeStarted = false;
                }
                CollectParticles(bundle, temporaryMeshes);
                // Booth preview selections must point to the user's objects, not the temporary copy.
                _root = source;
                bundle.Bounds = CalculateBundleBounds(bundle);
                Bounds sourceBounds = bundle.Bounds;
                SetExactBoothGuide(bundle);
                Repaint();
                SceneView.RepaintAll();
                ValidateBooth(sourceBounds);
                ApplyAutomaticPlacement(bundle, sourceBounds);
                bundle.Bounds = CalculateBundleBounds(bundle);

                Rac2BinaryExporter.BundleExportSummary result =
                    Rac2BinaryExporter.ExportBundle(bundle, output, 1024, _compress, _shareTextures);
                if (IsInsideAssets(output)) AssetDatabase.Refresh();

                EditorUtility.DisplayDialog(
                    L("RAC2 created"),
                    result.RenderNodeCount + L(" renderers / ") +
                    result.MaterialCount + L(" materials / ") +
                    result.VatNodeCount + L(" VAT nodes / ") +
                    result.ParticleEmitterCount + L(" particle emitters\n") +
                    result.VertexCount + L(" vertices / ") + result.IndexCount + L(" indices\n") +
                    result.FileSize + L(" stored / ") + result.UncompressedFileSize +
                    L(" raw bytes / ") + result.CompressedSectionCount + L(" LZ4 sections\n") +
                    (_compress
                        ? L("Smaller CDN file; VRChat opening can be slower for large VAT.")
                        : L("Fast-loading RAC2 (recommended for large VAT).")) + "\n\n" +
                    L("Automatically placed on the floor and centered using the model geometry."),
                    "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (animationModeStarted) AnimationMode.StopAnimationMode();
                for (int index = 0; index < temporaryMeshes.Count; index++)
                    if (temporaryMeshes[index] != null) DestroyImmediate(temporaryMeshes[index]);
                _root = source;
                _clip = sourceClip;
                if (clone != null) DestroyImmediate(clone);
                avatarPreparation?.Dispose();
                if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        private void CollectStaticRenderers(
            Rac2BinaryExporter.BundleData bundle,
            List<Mesh> temporaryMeshes)
        {
            MeshRenderer[] renderers = _root.GetComponentsInChildren<MeshRenderer>(true);
            for (int index = 0; index < renderers.Length; index++)
            {
                MeshRenderer renderer = renderers[index];
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer.GetComponentInParent<ParticleSystem>() != null) continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                Mesh mesh = CopyReadableMesh(filter.sharedMesh, renderer.name + " RAC2");
                temporaryMeshes.Add(mesh);
                EnsureMeshForMaterials(mesh, renderer.sharedMaterials, renderer.name);
                bundle.RenderNodes.Add(new Rac2BinaryExporter.RenderNodeData
                {
                    Name = RelativePath(renderer.transform),
                    Mesh = mesh,
                    Materials = TrimMaterials(renderer.sharedMaterials, mesh.subMeshCount, renderer.name),
                    LocalPosition = _root.transform.InverseTransformPoint(renderer.transform.position),
                    LocalRotation = Quaternion.Inverse(_root.transform.rotation) * renderer.transform.rotation,
                    LocalScale = RelativeScale(renderer.transform),
                });
            }
        }

        private void CollectSkinnedRenderers(
            Rac2BinaryExporter.BundleData bundle,
            List<Mesh> temporaryMeshes,
            int frameCount)
        {
            // Legacy synchronous callers remain supported only when no player-loop evaluation is needed.
            if (_clip != null && (HasActiveNativeConstraints(_root) || _includePhysBones))
                throw new InvalidOperationException(L("Constraint-driven VAT requires the asynchronous RAC2 Creator bake."));
            var steps = CollectSkinnedRenderersSteps(bundle, temporaryMeshes, frameCount);
            try { while (steps.MoveNext()) { } }
            finally { (steps as IDisposable)?.Dispose(); }
        }

        private static bool HasActiveNativeConstraints(GameObject root)
        {
            foreach (var component in root.GetComponentsInChildren<Behaviour>(true))
                // Clips can enable an initially inactive/zero-weight constraint.
                if (component != null && component is IConstraint) return true;
            return false;
        }

        private IEnumerator CollectSkinnedRenderersSteps(
            Rac2BinaryExporter.BundleData bundle,
            List<Mesh> temporaryMeshes,
            int frameCount)
        {
            SkinnedMeshRenderer[] renderers = _root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (_clip == null)
            {
                for (int index = 0; index < renderers.Length; index++)
                {
                    SkinnedMeshRenderer renderer = renderers[index];
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                        renderer.sharedMesh == null) continue;
                    Mesh baked = new Mesh { name = renderer.name + " RAC2 Pose" };
                    renderer.BakeMesh(baked);
                    // BakeMesh can carry the renderer's conservative skinning bounds.
                    // Placement and META must use the vertices actually written to RAC2.
                    baked.RecalculateBounds();
                    temporaryMeshes.Add(baked);
                    EnsureMeshForMaterials(baked, renderer.sharedMaterials, renderer.name);
                    bundle.RenderNodes.Add(new Rac2BinaryExporter.RenderNodeData
                    {
                        Name = RelativePath(renderer.transform),
                        Mesh = baked,
                        Materials = TrimMaterials(renderer.sharedMaterials, baked.subMeshCount, renderer.name),
                        LocalPosition = _root.transform.InverseTransformPoint(renderer.transform.position),
                        LocalRotation = Quaternion.Inverse(_root.transform.rotation) * renderer.transform.rotation,
                        LocalScale = RelativeScale(renderer.transform),
                    });
                }
                yield break;
            }

            var included = new List<SkinnedMeshRenderer>();
            for (int index = 0; index < renderers.Length; index++)
            {
                if (renderers[index].enabled && renderers[index].gameObject.activeInHierarchy &&
                    renderers[index].sharedMesh != null)
                    included.Add(renderers[index]);
            }
            if (included.Count == 0) yield break;
            bool evaluateConstraints = HasActiveNativeConstraints(_root);

            var baseMeshes = new Mesh[included.Count];
            var framePositions = new Vector3[included.Count][][];
            var frameNormals = _includeVatNormals ? new Vector3[included.Count][][] : null;
            var bakedFrames = new Mesh[included.Count];
            // Keep the authoring origin fixed while sampling, including root motion.
            Matrix4x4 exhibitFromWorld = _root.transform.worldToLocalMatrix;
            for (int index = 0; index < included.Count; index++)
            {
                framePositions[index] = new Vector3[frameCount][];
                if (_includeVatNormals) frameNormals[index] = new Vector3[frameCount][];
                bakedFrames[index] = new Mesh { name = included[index].name + " VAT frame" };
                temporaryMeshes.Add(bakedFrames[index]);
            }

            using (var baker = new Rac2VatFrameBaker(_root, _clip, evaluateConstraints, _constraintMaxPasses,
                _includePhysBones && Rac2PhysBoneBakeSession.Count(_root) > 0))
            {
            var warmup = baker.WarmUp(_physBoneWarmup);
            try { while (warmup.MoveNext()) yield return warmup.Current; }
            finally { (warmup as IDisposable)?.Dispose(); }
            for (int frame = 0; frame < frameCount; frame++)
            {
                var sample = baker.SampleFrame(frame, frameCount, _loop, _physBoneSeam);
                try { while (sample.MoveNext()) yield return sample.Current; }
                finally { (sample as IDisposable)?.Dispose(); }
                for (int rendererIndex = 0; rendererIndex < included.Count; rendererIndex++)
                {
                    SkinnedMeshRenderer renderer = included[rendererIndex];
                    Mesh baked = bakedFrames[rendererIndex];
                    baked.Clear();
                    renderer.BakeMesh(baked);
                    if (baked.vertexCount != renderer.sharedMesh.vertexCount)
                        throw new InvalidOperationException(
                            L("VAT topology changed on '") + RelativePath(renderer.transform) + "'.");
                    Matrix4x4 exhibitFromRenderer = exhibitFromWorld * renderer.transform.localToWorldMatrix;
                    Vector3[] positions = baked.vertices;
                    for (int vertex = 0; vertex < positions.Length; vertex++)
                        positions[vertex] = exhibitFromRenderer.MultiplyPoint3x4(positions[vertex]);
                    framePositions[rendererIndex][frame] = positions;
                    if (_includeVatNormals)
                    {
                        Vector3[] normals = baked.normals;
                        if (normals == null || normals.Length != baked.vertexCount)
                        {
                            baked.RecalculateNormals();
                            normals = baked.normals;
                        }
                        Matrix4x4 normalMatrix = exhibitFromRenderer.inverse.transpose;
                        for (int vertex = 0; vertex < normals.Length; vertex++)
                            normals[vertex] = normalMatrix.MultiplyVector(normals[vertex]).normalized;
                        frameNormals[rendererIndex][frame] = normals;
                    }
                    if (frame == 0)
                    {
                        baseMeshes[rendererIndex] = Instantiate(baked);
                        Mesh baseFrame = baseMeshes[rendererIndex];
                        baseFrame.vertices = positions;
                        Vector3[] baseNormals = baked.normals;
                        Matrix4x4 baseNormalMatrix = exhibitFromRenderer.inverse.transpose;
                        for (int vertex = 0; vertex < baseNormals.Length; vertex++)
                            baseNormals[vertex] = baseNormalMatrix.MultiplyVector(baseNormals[vertex]).normalized;
                        baseFrame.normals = baseNormals;
                        Vector4[] baseTangents = baked.tangents;
                        float handedness = exhibitFromRenderer.determinant < 0f ? -1f : 1f;
                        for (int vertex = 0; vertex < baseTangents.Length; vertex++)
                        {
                            Vector4 sourceTangent = baseTangents[vertex];
                            Vector3 tangent = exhibitFromRenderer.MultiplyVector(
                                new Vector3(sourceTangent.x, sourceTangent.y, sourceTangent.z)).normalized;
                            baseTangents[vertex] = new Vector4(tangent.x, tangent.y, tangent.z, sourceTangent.w * handedness);
                        }
                        baseFrame.tangents = baseTangents;
                        if (handedness < 0f)
                        {
                            for (int submesh = 0; submesh < baseFrame.subMeshCount; submesh++)
                            {
                                int[] triangles = baseFrame.GetTriangles(submesh);
                                for (int triangle = 0; triangle < triangles.Length; triangle += 3)
                                {
                                    int swap = triangles[triangle + 1];
                                    triangles[triangle + 1] = triangles[triangle + 2];
                                    triangles[triangle + 2] = swap;
                                }
                                baseFrame.SetTriangles(triangles, submesh, false);
                            }
                        }
                        baseFrame.RecalculateBounds();
                        baseMeshes[rendererIndex].name = renderer.name + " RAC2 VAT Base";
                        temporaryMeshes.Add(baseMeshes[rendererIndex]);
                    }
                }
                baker.RestoreOutput();
                if (EditorUtility.DisplayCancelableProgressBar(
                    L("Creating RAC2 VAT"), L("Frame ") + (frame + 1) + " / " + frameCount,
                    (frame + 1f) / frameCount)) throw new OperationCanceledException();
            }
            }

            for (int index = 0; index < included.Count; index++)
            {
                SkinnedMeshRenderer renderer = included[index];
                Mesh baseMesh = baseMeshes[index];
                EnsureMeshForMaterials(baseMesh, renderer.sharedMaterials, renderer.name);
                bundle.RenderNodes.Add(new Rac2BinaryExporter.RenderNodeData
                {
                    Name = RelativePath(renderer.transform),
                    Mesh = baseMesh,
                    Materials = TrimMaterials(renderer.sharedMaterials, baseMesh.subMeshCount, renderer.name),
                    LocalPosition = Vector3.zero,
                    LocalRotation = Quaternion.identity,
                    LocalScale = Vector3.one,
                    Vat = new Rac2BinaryExporter.VatClipData
                    {
                        Name = _clip.name,
                        FramesPerSecond = _framesPerSecond,
                        Loop = _loop,
                        Positions = framePositions[index],
                        Normals = _includeVatNormals ? frameNormals[index] : null,
                    },
                });
            }
        }

        private void CollectParticles(
            Rac2BinaryExporter.BundleData bundle,
            List<Mesh> temporaryMeshes)
        {
            ParticleSystem[] systems = _root.GetComponentsInChildren<ParticleSystem>(true);
            for (int index = 0; index < systems.Length; index++)
            {
                ParticleSystem source = systems[index];
                if (!source.gameObject.activeInHierarchy) continue;
                RejectUnsupportedParticleModules(source);
                ParticleSystemRenderer sourceRenderer = source.GetComponent<ParticleSystemRenderer>();
                if (sourceRenderer == null)
                    throw new InvalidOperationException(L("Particle renderer is missing on '") + RelativePath(source.transform) + "'.");
                bool billboard = sourceRenderer.renderMode != ParticleSystemRenderMode.Mesh;
                if (!billboard && sourceRenderer.mesh == null)
                    throw new InvalidOperationException(L("Particle mesh is missing on '") + RelativePath(source.transform) + "'.");
                if (sourceRenderer.renderMode != ParticleSystemRenderMode.Mesh &&
                    sourceRenderer.renderMode != ParticleSystemRenderMode.Billboard &&
                    sourceRenderer.renderMode != ParticleSystemRenderMode.HorizontalBillboard &&
                    sourceRenderer.renderMode != ParticleSystemRenderMode.VerticalBillboard)
                    throw new NotSupportedException(
                        L("Particle '") + RelativePath(source.transform) + L("' must use Billboard or Mesh rendering."));

                Mesh particleMesh = billboard
                    ? CreateParticleQuad()
                    : CopyReadableMesh(sourceRenderer.mesh, source.name + " RAC2 Particle");
                temporaryMeshes.Add(particleMesh);

                ParticleSystem.MainModule main = source.main;
                ParticleSystem.EmissionModule emission = source.emission;
                ParticleSystem.ShapeModule shape = source.shape;
                float lifeMin, lifeMax, speedMin, speedMax, sizeMin, sizeMax, gravityMin, gravityMax;
                ReadParticleCurve(main.startLifetime, L("start lifetime"), out lifeMin, out lifeMax);
                ReadParticleCurve(main.startSpeed, L("start speed"), out speedMin, out speedMax);
                ReadParticleCurve(main.startSize, L("start size"), out sizeMin, out sizeMax);
                ReadParticleCurve(main.gravityModifier, L("gravity"), out gravityMin, out gravityMax);
                if (Mathf.Abs(lifeMax - lifeMin) > 0.0001f)
                    throw new NotSupportedException(L("Particle lifetime must be constant."));
                if (Mathf.Abs(gravityMax - gravityMin) > 0.0001f)
                    throw new NotSupportedException(L("Particle gravity must be constant."));
                float rateMin, rateMax;
                ReadParticleCurve(emission.rateOverTime, L("emission rate"), out rateMin, out rateMax);
                if (Mathf.Abs(rateMax - rateMin) > 0.0001f)
                    throw new NotSupportedException(L("Particle emission rate must be constant."));

                Color startColor;
                Color endColor;
                ReadParticleColors(source, out startColor, out endColor);
                int shapeType;
                float shapeRadius;
                float shapeAngle;
                Vector3 shapeScale;
                ReadParticleShape(shape, out shapeType, out shapeRadius, out shapeAngle, out shapeScale);
                int columns = 1;
                int rows = 1;
                ParticleSystem.TextureSheetAnimationModule sheet = source.textureSheetAnimation;
                if (sheet.enabled)
                {
                    columns = sheet.numTilesX;
                    rows = sheet.numTilesY;
                }
                float angularSpeed = 0f;
                ParticleSystem.RotationOverLifetimeModule rotation = source.rotationOverLifetime;
                if (rotation.enabled)
                {
                    float rotationMin;
                    float rotationMax;
                    ReadParticleCurve(rotation.z, L("rotation over lifetime"), out rotationMin, out rotationMax);
                    angularSpeed = rotationMax * Mathf.Rad2Deg;
                }
                Texture texture = null;
                Material sourceMaterial = sourceRenderer.sharedMaterial;
                if (sourceMaterial != null)
                {
                    if (sourceMaterial.HasProperty("_MainTex"))
                        texture = sourceMaterial.GetTexture("_MainTex");
                    if (texture == null) texture = sourceMaterial.mainTexture;
                }

                Quaternion relativeRotation =
                    Quaternion.Inverse(_root.transform.rotation) * source.transform.rotation;
                bundle.ParticleEmitters.Add(new Rac2BinaryExporter.ParticleEmitterData
                {
                    Name = RelativePath(source.transform),
                    Mesh = particleMesh,
                    Particle = new Rac2BinaryExporter.ParticleData
                    {
                        Loop = main.loop,
                        Billboard = billboard,
                        HideBaseMesh = true,
                        ShaderProfile = (int)_particleBlend,
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
                        Origin = _root.transform.InverseTransformPoint(source.transform.position),
                        Direction = relativeRotation * Vector3.forward,
                        Shape = shapeType,
                        ShapeRadius = shapeRadius * MaxAbs(RelativeScale(source.transform)),
                        ShapeAngle = shapeAngle,
                        ShapeScale = Vector3.Scale(shapeScale, Abs(RelativeScale(source.transform))),
                        StartColor = startColor,
                        EndColor = endColor,
                        FlipbookColumns = columns,
                        FlipbookRows = rows,
                        Texture = texture,
                    },
                });
            }
        }

        private Bounds CalculateBundleBounds(Rac2BinaryExporter.BundleData bundle)
        {
            bool initialized = false;
            Vector3 minimum = Vector3.zero;
            Vector3 maximum = Vector3.zero;
            for (int nodeIndex = 0; nodeIndex < bundle.RenderNodes.Count; nodeIndex++)
            {
                Rac2BinaryExporter.RenderNodeData node = bundle.RenderNodes[nodeIndex];
                Bounds source = node.Vat == null ? node.Mesh.bounds : VatBounds(node.Vat);
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = new Vector3(
                        (corner & 1) == 0 ? source.min.x : source.max.x,
                        (corner & 2) == 0 ? source.min.y : source.max.y,
                        (corner & 4) == 0 ? source.min.z : source.max.z);
                    IncludePoint(node.LocalPosition + node.LocalRotation * Vector3.Scale(local, node.LocalScale),
                        ref initialized, ref minimum, ref maximum);
                }
            }
            if (initialized)
                return new Bounds(
                    (minimum + maximum) * 0.5f,
                    maximum - minimum);

            if (bundle.ParticleEmitters.Count > 0)
            {
                // A particle-only exhibit has no visual geometry from which to
                // derive a floor. Use one zero-size anchor for normalization;
                // emitter positions and shapes never expand booth bounds.
                return new Bounds(
                    bundle.ParticleEmitters[0].Particle.Origin,
                    Vector3.zero);
            }

            throw new InvalidOperationException(
                L("No supported renderer or particle was found."));
        }

        private static Bounds VatBounds(Rac2BinaryExporter.VatClipData vat)
        {
            bool initialized = false;
            Vector3 minimum = Vector3.zero;
            Vector3 maximum = Vector3.zero;
            for (int frame = 0; frame < vat.Positions.Length; frame++)
            {
                Vector3[] positions = vat.Positions[frame];
                for (int index = 0; index < positions.Length; index++)
                    IncludePoint(positions[index], ref initialized, ref minimum, ref maximum);
            }
            return new Bounds((minimum + maximum) * 0.5f, maximum - minimum);
        }

        private static Vector3 CalculateAutomaticPlacementOffset(Bounds bounds)
        {
            return new Vector3(
                -bounds.center.x,
                -bounds.min.y,
                -bounds.center.z);
        }

        private static void ApplyAutomaticPlacement(
            Rac2BinaryExporter.BundleData bundle,
            Bounds sourceBounds)
        {
            Vector3 offset = CalculateAutomaticPlacementOffset(sourceBounds);
            for (int index = 0; index < bundle.RenderNodes.Count; index++)
                bundle.RenderNodes[index].LocalPosition += offset;
            for (int index = 0; index < bundle.ParticleEmitters.Count; index++)
                bundle.ParticleEmitters[index].Particle.Origin += offset;
        }

        private static void IncludePoint(
            Vector3 value, ref bool initialized, ref Vector3 minimum, ref Vector3 maximum)
        {
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

        private static void ValidateBooth(Bounds bounds)
        {
            if (!BoothBoundsFit(bounds))
                throw new InvalidOperationException(
                    L("The exhibit exceeds the 3m x 3m x 2.7m booth.\n") +
                    DescribeBoothOverflow(bounds) + "\n" +
                    L("Root position and pivot do not affect this check. Export centers the model on the floor.\n") +
                    L("Particles are excluded from floor, centering and size checks. Particles outside the booth are hidden during playback."));
        }

        private Mesh CopyReadableMesh(Mesh source, string name)
        {
            if (source == null) throw new InvalidOperationException(L("A source mesh is missing."));
            if (!source.isReadable)
                throw new InvalidOperationException(L("Mesh '") + source.name + L("' must have Read/Write enabled."));
            Mesh copy = Instantiate(source);
            copy.name = name;
            copy.RecalculateBounds();
            return copy;
        }

        private static Material[] TrimMaterials(Material[] source, int count, string label)
        {
            if (source == null || source.Length < count)
                throw new InvalidOperationException(
                    L("Renderer '") + label + L("' needs one material per submesh."));
            Material[] result = new Material[count];
            for (int index = 0; index < count; index++)
            {
                if (source[index] == null)
                    throw new InvalidOperationException(
                        L("Renderer '") + label + L("' has an empty material slot ") + index + ".");
                result[index] = source[index];
            }
            return result;
        }

        private static void EnsureMeshForMaterials(Mesh mesh, Material[] materials, string label)
        {
            Material[] trimmed = TrimMaterials(materials, mesh.subMeshCount, label);
            for (int index = 0; index < trimmed.Length; index++)
            {
                Material material = trimmed[index];
                bool hasNormal = material.shader != null &&
                    material.shader.name.IndexOf("lilToon", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    material.HasProperty("_UseBumpMap") && material.GetFloat("_UseBumpMap") > 0.5f &&
                    material.HasProperty("_BumpMap") && material.GetTexture("_BumpMap") != null;
                if (!hasNormal) continue;
                if (mesh.uv == null || mesh.uv.Length != mesh.vertexCount)
                    throw new InvalidOperationException(
                        L("Normal-mapped material '") + material.name + L("' requires UV0."));
                if (mesh.normals == null || mesh.normals.Length != mesh.vertexCount)
                    mesh.RecalculateNormals();
                if (mesh.tangents == null || mesh.tangents.Length != mesh.vertexCount)
                    mesh.RecalculateTangents();
            }
        }

        private Vector3 RelativeScale(Transform child)
        {
            Vector3 rootScale = _root.transform.lossyScale;
            Vector3 childScale = child.lossyScale;
            return new Vector3(
                SafeDivide(childScale.x, rootScale.x),
                SafeDivide(childScale.y, rootScale.y),
                SafeDivide(childScale.z, rootScale.z));
        }

        private static float SafeDivide(float value, float divisor)
        {
            return Mathf.Abs(divisor) < 0.000001f ? value : value / divisor;
        }

        private string RelativePath(Transform child)
        {
            if (child == _root.transform) return _root.name;
            var parts = new List<string>();
            Transform current = child;
            while (current != null && current != _root.transform)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static bool IsInsideAssets(string path)
        {
            return path.Replace('\\', '/').StartsWith(
                Application.dataPath.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string FriendlyMessage(Exception exception)
        {
            if (exception == null) return L("Unknown error.");
            string message = FlareLocalization.Text(exception.Message);
            // Unmapped exporter/SDK diagnostics remain available verbatim through Details and Console.
            if (FlareLocalization.Japanese && !ContainsJapanese(message))
                message = "書き出しデータまたは設定に問題があります。「技術情報を表示」で対象と理由を確認してください。";
            return message +
                L("\n\nSelect the exhibit root, fix the named object, then press Create RAC2 again.");
        }

        private static bool ContainsJapanese(string text)
        {
            foreach (char c in text) if (c >= '\u3040' && c <= '\u9fff') return true;
            return false;
        }

        private static void ShowExportError(string title, Exception error)
        {
            if (!EditorUtility.DisplayDialog(title, FriendlyMessage(error), "OK", L("Show technical details")))
                EditorUtility.DisplayDialog(L("Show technical details"), error.ToString(), "OK");
        }

        private static string L(string english) { return FlareLocalization.Text(english); }

        private static void RejectUnsupportedParticleModules(ParticleSystem source)
        {
            if (source.noise.enabled) throw new NotSupportedException(L("Particle Noise is not supported."));
            if (source.collision.enabled) throw new NotSupportedException(L("Particle Collision is not supported."));
            if (source.trails.enabled) throw new NotSupportedException(L("Particle Trails are not supported."));
            if (source.subEmitters.enabled) throw new NotSupportedException(L("Particle Sub Emitters are not supported."));
            if (source.lights.enabled) throw new NotSupportedException(L("Particle Lights are not supported."));
            if (source.main.simulationSpace != ParticleSystemSimulationSpace.Local)
                throw new NotSupportedException(L("Particle simulation space must be Local."));
            if (source.main.startSize3D)
                throw new NotSupportedException(L("Particle start size must be uniform."));
        }

        private static void ReadParticleCurve(
            ParticleSystem.MinMaxCurve curve, string label, out float minimum, out float maximum)
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
            throw new NotSupportedException(
                L("Particle ") + label + L(" supports Constant or Two Constants only."));
        }

        private static void ReadParticleColors(ParticleSystem source, out Color start, out Color end)
        {
            ParticleSystem.MinMaxGradient initial = source.main.startColor;
            if (initial.mode == ParticleSystemGradientMode.Color) start = initial.color;
            else if (initial.mode == ParticleSystemGradientMode.TwoColors) start = initial.colorMax;
            else throw new NotSupportedException(L("Particle start color must be Color or Two Colors."));

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
            else throw new NotSupportedException(
                L("Particle color over lifetime must use one Color or Gradient."));
        }

        private static void ReadParticleShape(
            ParticleSystem.ShapeModule shape,
            out int type, out float radius, out float angle, out Vector3 scale)
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
                    throw new NotSupportedException(
                        L("Particle shape must be Point, Sphere, Cone, or Box."));
            }
        }

        private static Mesh CreateParticleQuad()
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

        private static Vector3 Abs(Vector3 value)
        {
            return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        }

        private static float MaxAbs(Vector3 value)
        {
            return Mathf.Max(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        }
    }
}
