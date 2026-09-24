using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AvatarCatalog.Remote
{
    /// <summary>Optional MA/NDMF authoring integration. Never processes the user's source object.</summary>
    internal sealed class Rac2AvatarPreprocessor : IDisposable
    {
        private const string TagType = "nadena.dev.modular_avatar.core.AvatarTagComponent";
        private const string StateName = "FLARE VAT input";
        private readonly HashSet<Object> _protected = new HashSet<Object>();
        private readonly HashSet<Object> _owned = new HashSet<Object>();
        private GameObject _copy;
        public AnimationClip Clip { get; private set; }

        internal static Type FindType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var result = assembly.GetType(name, false);
                if (result != null) return result;
            }
            return null;
        }

        internal static bool HasModularAvatar(GameObject root)
        {
            var tag = FindType(TagType);
            return root != null && tag != null && root.GetComponentsInChildren(tag, true).Length != 0;
        }

        public static void ValidateSource(GameObject root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            // Missing scripts cannot safely be assumed to be irrelevant to clothing assembly.
            // Once MA is installed, avatar-only SDK components can remain missing in a world project.
            if (FindType(TagType) == null)
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject) > 0)
                        throw new InvalidOperationException("'" + t.name + FlareLocalization.Text("' has a Missing Script.")
                            + FlareLocalization.Text("For MA clothing, install Modular Avatar and NDMF.")
                            + FlareLocalization.Text("RAC2 cannot be created without processing the clothing armature merge."));
            if (HasModularAvatar(root)) ResolveApi();
        }

        private static MethodInfo ResolveApi()
        {
            var processor = FindType("nadena.dev.ndmf.AvatarProcessor");
            var platform = FindType("nadena.dev.ndmf.platform.INDMFPlatformProvider");
            var generic = FindType("nadena.dev.ndmf.platform.GenericPlatform");
            var scope = FindType("nadena.dev.ndmf.OverrideTemporaryDirectoryScope");
            var method = processor == null || platform == null ? null : processor.GetMethod(
                "ProcessAvatar", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(GameObject), platform }, null);
            if (method == null || generic?.GetProperty("Instance") == null ||
                scope?.GetConstructor(new[] { typeof(string) }) == null ||
                method.ReturnType.GetProperty("Successful")?.PropertyType != typeof(bool))
                throw new NotSupportedException(FlareLocalization.Text("MA clothing requires NDMF with Generic platform support.")
                    + FlareLocalization.Text("Verified combination: Modular Avatar 1.18.7 / NDMF 1.14.8.")
                    + FlareLocalization.Text("Unprocessed clothing cannot be exported."));
            return method;
        }

        public static Rac2AvatarPreprocessor Prepare(GameObject copy, GameObject source, AnimationClip clip)
        {
            if (!copy || !source || copy == source || EditorUtility.IsPersistent(copy))
                throw new ArgumentException("Avatar preprocessing requires an independent scene copy.");
            ValidateSource(source);
            var result = new Rac2AvatarPreprocessor { _copy = copy, Clip = clip };
            try
            {
                var inputs = clip ? new Object[] { source, clip } : new Object[] { source };
                foreach (var obj in EditorUtility.CollectDependencies(inputs))
                    if (obj) result._protected.Add(obj);
                if (HasModularAvatar(copy)) result.Process();
                return result;
            }
            catch
            {
                result.CaptureGeneratedAssets();
                result.Dispose();
                throw;
            }
        }

        private void Process()
        {
            var process = ResolveApi();
            var animator = _copy.GetComponent<Animator>();
            if (Clip != null)
            {
                if (!animator) animator = _copy.AddComponent<Animator>();
                // Include the selected clip in NDMF's binding rewrite. Sampling the original
                // clip after a hierarchy merge would break generic/transform animation paths.
                var controller = new AnimatorController { name = "FLARE VAT input" };
                _owned.Add(controller);
                controller.AddLayer("VAT");
                var machine = controller.layers[0].stateMachine;
                _owned.Add(machine);
                var state = machine.AddState(StateName);
                _owned.Add(state);
                state.motion = Clip;
                machine.defaultState = state;
                animator.runtimeAnimatorController = controller;
            }

            var scopeType = FindType("nadena.dev.ndmf.OverrideTemporaryDirectoryScope");
            var generic = FindType("nadena.dev.ndmf.platform.GenericPlatform").GetProperty("Instance").GetValue(null);
            // Use the public, no-serialization scope; no shared GeneratedAssets folder is deleted.
            using ((IDisposable)Activator.CreateInstance(scopeType, new object[] { null }))
            {
                object context;
                try { context = process.Invoke(null, new[] { (object)_copy, generic }); }
                catch (TargetInvocationException error)
                {
                    throw new InvalidOperationException(FlareLocalization.Text("MA/NDMF clothing processing failed.")
                        + FlareLocalization.Text("RAC2 was not saved.\n") + error.InnerException?.Message, error.InnerException ?? error);
                }
                CaptureGeneratedAssets();
                if (context == null || !(bool)process.ReturnType.GetProperty("Successful").GetValue(context))
                    throw new InvalidOperationException(FlareLocalization.Text("MA/NDMF reported a clothing processing error.")
                        + FlareLocalization.Text("Resolve the NDMF errors before exporting again. RAC2 was not saved."));
            }
            var mergeType = FindType("nadena.dev.modular_avatar.core.ModularAvatarMergeArmature");
            if (mergeType != null && _copy.GetComponentsInChildren(mergeType, true).Length != 0)
                throw new InvalidOperationException(FlareLocalization.Text("Export stopped because MA Merge Armature was not processed."));
            if (Clip != null)
            {
                var controller = animator ? animator.runtimeAnimatorController as AnimatorController : null;
                AnimationClip processed = null;
                if (controller)
                    foreach (var layer in controller.layers)
                        foreach (var state in layer.stateMachine.states)
                            if (state.state.name == StateName && state.state.motion is AnimationClip candidate)
                            {
                                if (processed) throw new InvalidOperationException(FlareLocalization.Text("Multiple VAT clips found after NDMF processing."));
                                processed = candidate;
                            }
                if (!processed) throw new InvalidOperationException(FlareLocalization.Text("VAT clip not found after NDMF processing. Export stopped."));
                Clip = processed;
            }
        }

        private void CaptureGeneratedAssets()
        {
            if (!_copy) return;
            foreach (var obj in EditorUtility.CollectDependencies(new Object[] { _copy }))
                if (obj && !(obj is GameObject) && !(obj is Component) &&
                    !EditorUtility.IsPersistent(obj) && !_protected.Contains(obj)) _owned.Add(obj);
        }

        public void Dispose()
        {
            foreach (var obj in _owned)
                if (obj && !EditorUtility.IsPersistent(obj) && !_protected.Contains(obj)) Object.DestroyImmediate(obj);
            _owned.Clear();
            _copy = null;
        }
    }
}
